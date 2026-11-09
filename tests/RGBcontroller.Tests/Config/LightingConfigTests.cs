using RGBcontroller.Core.Config;

namespace RGBcontroller.Tests.Config;

public sealed class LightingConfigTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    private string ConfigPath => _dir.Combine("config.json");

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Defaults_AreValidAndPreMappedToTheOwnersDevices()
    {
        var lighting = AppConfig.CreateDefault().Lighting;

        Assert.Empty(ConfigValidator.Validate(AppConfig.CreateDefault()));
        Assert.Equal(["Rainbow", "Cyberpunk", "Aurora", "Night", "Solid White", "Logon"], lighting.Profiles.Select(p => p.Name));
        var rainbow = lighting.FindProfile("Rainbow")!;
        Assert.Equal(EffectKind.Radial, rainbow.FindGroup(DefaultLighting.PumpAndFansGroup)!.Effect);
        Assert.Equal(180, rainbow.FindGroup(DefaultLighting.KeyboardGroup)!.Angle);
        Assert.Equal(EffectKind.Mirror, rainbow.FindGroup(DefaultLighting.RamGroup)!.Effect);
        Assert.Equal(EffectKind.ColorCycle, rainbow.FindGroup(DefaultLighting.MouseGroup)!.Effect);
        Assert.Equal(EffectKind.ColorCycle, rainbow.FindGroup(DefaultLighting.GpuGroup)!.Effect);
        Assert.Equal(AmbilightMode.Screen, rainbow.Ambilight.Mode);
        Assert.Equal(5, lighting.Groups.Single(g => g.Name == DefaultLighting.RamGroup).MaxFps);
        Assert.Equal(10, lighting.Groups.Single(g => g.Name == DefaultLighting.GpuGroup).MaxFps);
    }

    [Fact]
    public void StageOneConfig_MigratesToTheShippedProfiles()
    {
        File.WriteAllText(ConfigPath, """
            {
              "schemaVersion": 1,
              "strip": { "ledCount": 113, "portName": "COM3" },
              "lighting": { "staticColor": "#00E5FF", "deviceBrightnessPercent": 100, "stripBrightnessPercent": 50 }
            }
            """);

        var result = new ConfigStore(ConfigPath).Load();

        Assert.Equal(ConfigLoadOutcome.Migrated, result.Outcome);
        Assert.Equal("COM3", result.Config.Strip.PortName);
        Assert.Equal(new LightingConfig(), result.Config.Lighting);
        Assert.True(File.Exists(_dir.Combine("config.v1.bak.json")));
        Assert.Equal(ConfigLoadOutcome.Loaded, new ConfigStore(ConfigPath).Load().Outcome);
    }

    [Fact]
    public void LightingSection_RoundTripsThroughJson()
    {
        var store = new ConfigStore(ConfigPath);
        var config = AppConfig.CreateDefault() with
        {
            Lighting = new LightingConfig { ActiveProfile = "Night", LockProfile = "Aurora", DefaultGroup = DefaultLighting.MotherboardGroup },
        };

        store.Save(config);
        var loaded = store.Load();

        Assert.Equal(ConfigLoadOutcome.Loaded, loaded.Outcome);
        Assert.Equal(config, loaded.Config);
        Assert.Contains("\"effect\": \"Radial\"", File.ReadAllText(ConfigPath));
    }

    [Fact]
    public void HandEditedProfile_KeepsDefaultsForMissingFields()
    {
        File.WriteAllText(ConfigPath, """
            {
              "schemaVersion": 3,
              "lighting": {
                "activeProfile": "Mine",
                "logonProfile": "Mine",
                "profiles": [ { "name": "Mine", "groups": [ { "group": "Keyboard", "effect": "Wave" } ] } ]
              }
            }
            """);

        var result = new ConfigStore(ConfigPath).Load();

        Assert.Equal(ConfigLoadOutcome.Loaded, result.Outcome);
        var effect = result.Config.Lighting.FindProfile("mine")!.FindGroup("keyboard")!;
        Assert.Equal(EffectKind.Wave, effect.Effect);
        Assert.Equal("Rainbow", effect.Palette);
        Assert.Equal(100, effect.BrightnessPercent);
        Assert.Equal(6, result.Config.Lighting.Groups.Count);
    }

    [Theory]
    [InlineData("activeProfile")]
    [InlineData("unknownGroup")]
    [InlineData("unknownPalette")]
    [InlineData("badStop")]
    [InlineData("hardwareWithoutMode")]
    [InlineData("duplicateProfile")]
    [InlineData("speed")]
    [InlineData("screenFallback")]
    [InlineData("groupFps")]
    public void Validator_RejectsBrokenLighting(string broken)
    {
        var lighting = new LightingConfig();
        var rainbow = lighting.Profiles[0];
        lighting = broken switch
        {
            "activeProfile" => lighting with { ActiveProfile = "Missing" },
            "unknownGroup" => WithRainbow(lighting, rainbow with { Groups = [new GroupEffectConfig { Group = "Fridge" }] }),
            "unknownPalette" => WithRainbow(lighting, rainbow with { Groups = [new GroupEffectConfig { Group = "RAM", Palette = "Pastel" }] }),
            "badStop" => lighting with { Palettes = [new PaletteConfig { Name = "Rainbow", Stops = [new PaletteStop(1.5, "#FFFFFF")] }] },
            "hardwareWithoutMode" => WithRainbow(lighting, rainbow with { Groups = [new GroupEffectConfig { Group = "RAM", Effect = EffectKind.Hardware }] }),
            "duplicateProfile" => lighting with { Profiles = [.. lighting.Profiles, rainbow] },
            "speed" => WithRainbow(lighting, rainbow with { Groups = [new GroupEffectConfig { Group = "RAM", Speed = 50 }] }),
            "screenFallback" => WithRainbow(lighting, rainbow with { Ambilight = new AmbilightConfig { Fallback = AmbilightMode.Screen } }),
            "groupFps" => lighting with { Groups = [.. lighting.Groups.Select(g => g with { MaxFps = 0 })] },
            _ => throw new ArgumentOutOfRangeException(nameof(broken)),
        };

        Assert.NotEmpty(ConfigValidator.Validate(AppConfig.CreateDefault() with { Lighting = lighting }));
    }

    private static LightingConfig WithRainbow(LightingConfig lighting, ProfileConfig rainbow) =>
        lighting with { Profiles = [rainbow, .. lighting.Profiles.Skip(1)] };
}
