using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RGBcontroller.Core.Config;

public enum ConfigLoadOutcome
{
    /// <summary>File read and valid at the current schema version.</summary>
    Loaded,

    /// <summary>No file existed; defaults were written.</summary>
    CreatedDefault,

    /// <summary>File was at an older schema; it was backed up, migrated and rewritten.</summary>
    Migrated,

    /// <summary>File was not valid JSON; it was moved aside and defaults were written.</summary>
    RecoveredFromCorrupt,

    /// <summary>File parsed but failed type or range checks; defaults are used in memory, the file is left untouched.</summary>
    InvalidUsingDefaults,

    /// <summary>File comes from a newer app version; defaults are used in memory, the file is left untouched.</summary>
    TooNewUsingDefaults,
}

public sealed record ConfigLoadResult(AppConfig Config, ConfigLoadOutcome Outcome, IReadOnlyList<string> Messages);

/// <summary>
/// Loads and saves <see cref="AppConfig"/> with schema migrations and atomic writes.
/// Only the Service writes the config; other processes ask the Service over IPC.
/// </summary>
public sealed class ConfigStore
{
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly ConfigMigrator _migrator;
    private readonly TimeProvider _time;

    public ConfigStore(string path, ConfigMigrator? migrator = null, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        FilePath = Path.GetFullPath(path);
        _migrator = migrator ?? ConfigMigrator.Default;
        _time = time ?? TimeProvider.System;
    }

    public string FilePath { get; }

    public ConfigLoadResult Load()
    {
        if (!File.Exists(FilePath))
        {
            var created = AppConfig.CreateDefault();
            Save(created);
            return new ConfigLoadResult(created, ConfigLoadOutcome.CreatedDefault, [$"Created default config at {FilePath}."]);
        }

        var text = File.ReadAllText(FilePath);
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(text, documentOptions: DocumentOptions) as JsonObject;
        }
        catch (JsonException)
        {
            root = null;
        }

        if (root is null || !TryReadSchemaVersion(root, out var version))
        {
            return RecoverFromCorrupt();
        }

        if (version > _migrator.TargetVersion)
        {
            return UseDefaults(ConfigLoadOutcome.TooNewUsingDefaults,
                $"Config schema {version} is newer than supported {_migrator.TargetVersion}; using defaults, file left untouched.");
        }

        var migrated = false;
        if (version < _migrator.TargetVersion)
        {
            File.Copy(FilePath, SiblingPath($"config.v{version}.bak.json"), overwrite: true);
            _migrator.Migrate(root, version);
            migrated = true;
        }

        AppConfig? config;
        try
        {
            config = root.Deserialize(ConfigJsonContext.Default.AppConfig);
        }
        catch (JsonException ex)
        {
            return UseDefaults(ConfigLoadOutcome.InvalidUsingDefaults, $"Config has invalid values: {ex.Message}");
        }

        if (config is null)
        {
            return RecoverFromCorrupt();
        }

        var errors = ConfigValidator.Validate(config);
        if (errors.Count > 0)
        {
            return UseDefaults(ConfigLoadOutcome.InvalidUsingDefaults, ["Config failed validation; using defaults, file left untouched.", .. errors]);
        }

        if (migrated)
        {
            Save(config);
            return new ConfigLoadResult(config, ConfigLoadOutcome.Migrated,
                [$"Migrated config from schema {version} to {_migrator.TargetVersion}."]);
        }

        return new ConfigLoadResult(config, ConfigLoadOutcome.Loaded, []);
    }

    /// <summary>Validates and atomically writes <paramref name="config"/>, stamped with the current schema version.</summary>
    /// <exception cref="ArgumentException">The config fails validation.</exception>
    public void Save(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var errors = ConfigValidator.Validate(config);
        if (errors.Count > 0)
        {
            throw new ArgumentException("Invalid config: " + string.Join(" ", errors), nameof(config));
        }

        var stamped = config with { SchemaVersion = _migrator.TargetVersion };
        AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(stamped, ConfigJsonContext.Default.AppConfig));
    }

    private static bool TryReadSchemaVersion(JsonObject root, out int version)
    {
        version = 0;
        return root[ConfigMigrator.SchemaVersionProperty] is JsonValue value
            && value.TryGetValue(out version)
            && version >= 1;
    }

    private ConfigLoadResult RecoverFromCorrupt()
    {
        var stamp = _time.GetUtcNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var quarantine = SiblingPath($"config.corrupt-{stamp}.json");
        File.Move(FilePath, quarantine, overwrite: true);
        var config = AppConfig.CreateDefault();
        Save(config);
        return new ConfigLoadResult(config, ConfigLoadOutcome.RecoveredFromCorrupt,
            [$"Config was unreadable; moved to {quarantine} and wrote defaults."]);
    }

    private static ConfigLoadResult UseDefaults(ConfigLoadOutcome outcome, params IReadOnlyList<string> messages) =>
        new(AppConfig.CreateDefault(), outcome, messages);

    private string SiblingPath(string fileName) => Path.Combine(Path.GetDirectoryName(FilePath)!, fileName);
}
