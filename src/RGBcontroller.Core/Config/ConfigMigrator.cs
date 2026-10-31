using System.Text.Json.Nodes;

namespace RGBcontroller.Core.Config;

/// <summary>One schema step: transforms a raw config document from <see cref="FromVersion"/> to FromVersion + 1.</summary>
public interface IConfigMigration
{
    int FromVersion { get; }

    void Apply(JsonObject root);
}

/// <summary>Runs a contiguous chain of <see cref="IConfigMigration"/> steps on the raw JSON document.</summary>
public sealed class ConfigMigrator
{
    public const string SchemaVersionProperty = "schemaVersion";

    private readonly Dictionary<int, IConfigMigration> _steps;

    public ConfigMigrator(IEnumerable<IConfigMigration> migrations, int targetVersion)
    {
        ArgumentNullException.ThrowIfNull(migrations);
        _steps = new Dictionary<int, IConfigMigration>();
        foreach (var migration in migrations)
        {
            if (!_steps.TryAdd(migration.FromVersion, migration))
            {
                throw new ArgumentException($"Duplicate config migration from version {migration.FromVersion}.", nameof(migrations));
            }
        }

        TargetVersion = targetVersion;
    }

    /// <summary>Migrations shipped with the app.</summary>
    public static ConfigMigrator Default { get; } = new([new LightingV2Migration()], AppConfig.CurrentSchemaVersion);

    public int TargetVersion { get; }

    /// <summary>Migrates <paramref name="root"/> in place from <paramref name="fromVersion"/> to <see cref="TargetVersion"/>.</summary>
    /// <exception cref="InvalidOperationException">A step in the chain is missing.</exception>
    public void Migrate(JsonObject root, int fromVersion)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (fromVersion < 1 || fromVersion > TargetVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(fromVersion), fromVersion, $"Expected 1..{TargetVersion}.");
        }

        for (var version = fromVersion; version < TargetVersion; version++)
        {
            if (!_steps.TryGetValue(version, out var step))
            {
                throw new InvalidOperationException($"No config migration from version {version} to {version + 1}.");
            }

            step.Apply(root);
            root[SchemaVersionProperty] = version + 1;
        }
    }
}
