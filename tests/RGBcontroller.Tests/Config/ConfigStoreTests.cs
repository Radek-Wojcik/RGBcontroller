using System.Text.Json.Nodes;
using RGBcontroller.Core.Config;

namespace RGBcontroller.Tests.Config;

public sealed class ConfigStoreTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    private string ConfigPath => _dir.Combine("config.json");

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Load_MissingFile_WritesDefaults()
    {
        var store = new ConfigStore(ConfigPath);

        var result = store.Load();

        Assert.Equal(ConfigLoadOutcome.CreatedDefault, result.Outcome);
        Assert.Equal(AppConfig.CreateDefault(), result.Config);
        Assert.True(File.Exists(ConfigPath));
        Assert.Contains("\"schemaVersion\": 2", File.ReadAllText(ConfigPath));
    }

    [Fact]
    public void Defaults_MatchVerifiedHardware()
    {
        var strip = AppConfig.CreateDefault().Strip;

        Assert.Equal(113, strip.LedCount);
        Assert.Equal(115200, strip.BaudRate);
        Assert.Equal(ColorOrder.Rgb, strip.ColorOrder);
        Assert.Equal(30, strip.MaxFps);
        Assert.Equal("1A86", strip.UsbVendorId);
        Assert.Equal("7523", strip.UsbProductId);
        Assert.Null(strip.PortName);
        Assert.Equal(4500, strip.CurrentLimitMilliamps);
        Assert.Equal("127.0.0.1", AppConfig.CreateDefault().OpenRgb.Host);
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var store = new ConfigStore(ConfigPath);
        var config = AppConfig.CreateDefault() with
        {
            Strip = new StripConfig { PortName = "COM7", LedCount = 120, ColorOrder = ColorOrder.Grb },
        };

        store.Save(config);
        var result = store.Load();

        Assert.Equal(ConfigLoadOutcome.Loaded, result.Outcome);
        Assert.Equal(config, result.Config);
        Assert.Contains("\"colorOrder\": \"Grb\"", File.ReadAllText(ConfigPath));
    }

    [Fact]
    public void Load_MissingSections_FallBackToDefaults()
    {
        File.WriteAllText(ConfigPath, """{ "schemaVersion": 2, "strip": { "ledCount": 60 } }""");

        var result = new ConfigStore(ConfigPath).Load();

        Assert.Equal(ConfigLoadOutcome.Loaded, result.Outcome);
        Assert.Equal(60, result.Config.Strip.LedCount);
        Assert.Equal(115200, result.Config.Strip.BaudRate);
        Assert.Equal(6742, result.Config.OpenRgb.Port);
    }

    [Fact]
    public void Load_AcceptsCommentsAndTrailingCommas()
    {
        File.WriteAllText(ConfigPath, """
            {
              // hand edited
              "schemaVersion": 2,
              "strip": { "maxFps": 25, },
            }
            """);

        var result = new ConfigStore(ConfigPath).Load();

        Assert.Equal(ConfigLoadOutcome.Loaded, result.Outcome);
        Assert.Equal(25, result.Config.Strip.MaxFps);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[1, 2, 3]")]
    [InlineData("""{ "strip": {} }""")]
    [InlineData("""{ "schemaVersion": "one" }""")]
    public void Load_CorruptFile_IsQuarantinedAndDefaultsWritten(string content)
    {
        File.WriteAllText(ConfigPath, content);
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 10, 30, 12, 34, 56, TimeSpan.Zero));

        var result = new ConfigStore(ConfigPath, time: time).Load();

        Assert.Equal(ConfigLoadOutcome.RecoveredFromCorrupt, result.Outcome);
        Assert.Equal(AppConfig.CreateDefault(), result.Config);
        Assert.Equal(content, File.ReadAllText(_dir.Combine("config.corrupt-20261030-123456.json")));
        Assert.Equal(ConfigLoadOutcome.Loaded, new ConfigStore(ConfigPath).Load().Outcome);
    }

    [Theory]
    [InlineData("""{ "schemaVersion": 2, "strip": { "ledCount": 0 } }""")]
    [InlineData("""{ "schemaVersion": 2, "strip": { "ledCount": "many" } }""")]
    [InlineData("""{ "schemaVersion": 2, "strip": null }""")]
    [InlineData("""{ "schemaVersion": 2, "openRgb": { "host": "192.168.1.10" } }""")]
    [InlineData("""{ "schemaVersion": 2, "strip": { "colorOrder": "Purple" } }""")]
    public void Load_InvalidValues_UseDefaultsAndKeepFile(string content)
    {
        File.WriteAllText(ConfigPath, content);

        var result = new ConfigStore(ConfigPath).Load();

        Assert.Equal(ConfigLoadOutcome.InvalidUsingDefaults, result.Outcome);
        Assert.Equal(AppConfig.CreateDefault(), result.Config);
        Assert.NotEmpty(result.Messages);
        Assert.Equal(content, File.ReadAllText(ConfigPath));
    }

    [Fact]
    public void Load_NewerSchema_UsesDefaultsAndKeepsFile()
    {
        const string content = """{ "schemaVersion": 99, "future": true }""";
        File.WriteAllText(ConfigPath, content);

        var result = new ConfigStore(ConfigPath).Load();

        Assert.Equal(ConfigLoadOutcome.TooNewUsingDefaults, result.Outcome);
        Assert.Equal(content, File.ReadAllText(ConfigPath));
    }

    [Fact]
    public void Load_OlderSchema_BacksUpMigratesAndRewrites()
    {
        // Pretend the current schema is 2 and v1 called the LED count "leds".
        var migrator = new ConfigMigrator([new RenameLedsMigration()], targetVersion: 2);
        const string original = """{ "schemaVersion": 1, "strip": { "leds": 77 } }""";
        File.WriteAllText(ConfigPath, original);

        var result = new ConfigStore(ConfigPath, migrator).Load();

        Assert.Equal(ConfigLoadOutcome.Migrated, result.Outcome);
        Assert.Equal(77, result.Config.Strip.LedCount);
        Assert.Equal(original, File.ReadAllText(_dir.Combine("config.v1.bak.json")));
        var rewritten = JsonNode.Parse(File.ReadAllText(ConfigPath))!;
        Assert.Equal(2, rewritten["schemaVersion"]!.GetValue<int>());
        Assert.Equal(77, rewritten["strip"]!["ledCount"]!.GetValue<int>());
        Assert.Null(rewritten["strip"]!["leds"]);
        Assert.Equal(ConfigLoadOutcome.Loaded, new ConfigStore(ConfigPath, migrator).Load().Outcome);
    }

    [Fact]
    public void Save_InvalidConfig_ThrowsAndDoesNotWrite()
    {
        var store = new ConfigStore(ConfigPath);
        var invalid = AppConfig.CreateDefault() with { Strip = new StripConfig { MaxFps = 500 } };

        Assert.Throws<ArgumentException>(() => store.Save(invalid));
        Assert.False(File.Exists(ConfigPath));
    }

    [Fact]
    public void Save_ReplacesExistingFileAndLeavesNoTempFile()
    {
        var store = new ConfigStore(ConfigPath);
        store.Save(AppConfig.CreateDefault());

        store.Save(AppConfig.CreateDefault() with { Strip = new StripConfig { LedCount = 50 } });

        Assert.Equal(50, store.Load().Config.Strip.LedCount);
        Assert.False(File.Exists(ConfigPath + AtomicFile.TempSuffix));
        Assert.Single(Directory.GetFiles(_dir.Path));
    }

    private sealed class RenameLedsMigration : IConfigMigration
    {
        public int FromVersion => 1;

        public void Apply(JsonObject root)
        {
            var strip = (JsonObject)root["strip"]!;
            strip["ledCount"] = strip["leds"]!.GetValue<int>();
            strip.Remove("leds");
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
