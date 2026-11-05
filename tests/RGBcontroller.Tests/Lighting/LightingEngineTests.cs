using RGBcontroller.Core;
using RGBcontroller.Core.Config;
using RGBcontroller.Core.Lighting;
using RGBcontroller.Core.OpenRgb;
using RGBcontroller.Tests.OpenRgb;
using Serilog.Core;

namespace RGBcontroller.Tests.Lighting;

public sealed class LightingEngineTests
{
    private static readonly ControllerData Keyboard = ControllerDataWriter.Keyboard() with { Name = DefaultLighting.Keyboard };
    private static readonly ControllerData Ram = LedLayoutBuilderTests.Ram();
    private static readonly ControllerData Mouse = ControllerDataWriter.Mouse();

    [Fact]
    public void StaticProfile_RendersOnceRepeatsTwiceThenIdles()
    {
        var engine = Engine(Config("Solid White"));
        engine.SetDevices([Keyboard, Ram]);
        var output = new RecordingOutput();

        var next = engine.Render(TimeSpan.Zero, output);
        Assert.Equal(1, output.LedWrites[0]);
        Assert.Equal(1, output.LedWrites[1]);
        Assert.All(output.Leds[0], c => Assert.Equal(new RgbColor(255, 255, 255), c));
        Assert.Equal([(0, "Direct"), (1, "Direct")], output.Modes);

        // The repeats guard against a lost USB packet (the Razer keyboard is written row by row).
        var now = TimeSpan.Zero;
        while (next is not null)
        {
            now += next.Value;
            next = engine.Render(now, output);
        }

        Assert.Equal(3, output.LedWrites[0]);
        Assert.Equal(3, output.LedWrites[1]);
        Assert.InRange(now, TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(600));

        engine.Render(TimeSpan.FromSeconds(5), output);
        Assert.Equal(3, output.LedWrites[0]);
        Assert.Equal(2, output.Modes.Count);
    }

    [Fact]
    public void RainbowProfile_RespectsPerGroupFrameRates()
    {
        var engine = Engine(Config(DefaultLighting.RainbowProfile));
        engine.SetDevices([Keyboard, Ram]);
        var output = new RecordingOutput();

        var now = TimeSpan.Zero;
        while (now < TimeSpan.FromSeconds(2))
        {
            var next = engine.Render(now, output);
            Assert.NotNull(next);
            now += next.Value;
        }

        Assert.InRange(output.LedWrites[0], 58, 62); // keyboard 30 FPS
        Assert.InRange(output.LedWrites[1], 9, 11); // RAM 5 FPS
    }

    [Fact]
    public void WritesOnlyWhenColorsChange()
    {
        var config = Config(DefaultLighting.RainbowProfile);
        var slow = config.Profiles[0] with
        {
            Groups = [new GroupEffectConfig { Group = DefaultLighting.KeyboardGroup, Effect = EffectKind.ColorCycle, Speed = 0.0001 }],
        };
        var engine = Engine(config with { Profiles = [slow] });
        engine.SetDevices([Keyboard]);
        var output = new RecordingOutput();

        for (var frame = 0; frame < 30; frame++)
        {
            engine.Render(TimeSpan.FromSeconds(frame / 30.0), output);
        }

        Assert.Equal(1, output.LedWrites[0]);
    }

    [Fact]
    public void Brightness_IsGroupTimesMaster()
    {
        var config = Config("Solid White");
        var profile = config.FindProfile("Solid White")! with
        {
            MasterBrightnessPercent = 50,
            Groups = [new GroupEffectConfig { Group = DefaultLighting.KeyboardGroup, Palette = "#FFFFFF", BrightnessPercent = 50 }],
        };
        var engine = Engine(config with { Profiles = [profile] });
        engine.SetDevices([Keyboard]);
        var output = new RecordingOutput();

        engine.Render(TimeSpan.Zero, output);

        Assert.All(output.Leds[0], c => Assert.Equal(new RgbColor(64, 64, 64), c));
    }

    [Fact]
    public void UnclaimedZones_StayOffUnlessADefaultGroupExists()
    {
        var config = Config("Solid White");
        var output = new RecordingOutput();
        var engine = Engine(config);
        engine.SetDevices([Mouse with { Name = "Unknown mouse" }]);

        engine.Render(TimeSpan.Zero, output);
        Assert.All(output.Leds[0], c => Assert.Equal(RgbColor.Black, c));

        engine.SetConfig(config with { DefaultGroup = DefaultLighting.KeyboardGroup });
        engine.Render(TimeSpan.Zero, output);
        Assert.All(output.Leds[0], c => Assert.Equal(new RgbColor(255, 255, 255), c));
    }

    [Fact]
    public void HardwareMode_SetsTheDeviceModeAndWritesNoLeds()
    {
        var config = Config(DefaultLighting.RainbowProfile);
        var profile = config.Profiles[0] with
        {
            Groups = [new GroupEffectConfig { Group = DefaultLighting.RamGroup, Effect = EffectKind.Hardware, HardwareMode = "rainbow" }],
        };
        var engine = Engine(config with { Profiles = [profile] });
        engine.SetDevices([Ram]);
        var output = new RecordingOutput();

        var next = engine.Render(TimeSpan.Zero, output);

        Assert.Null(next);
        Assert.Equal([(0, "Rainbow")], output.Modes);
        Assert.False(output.LedWrites.ContainsKey(0));
    }

    [Fact]
    public void Modes_AreResentAfterReEnumerationButNotAfterAProfileSwitch()
    {
        var config = Config(DefaultLighting.RainbowProfile);
        var engine = Engine(config);
        var output = new RecordingOutput();
        engine.SetDevices([Keyboard]);
        engine.Render(TimeSpan.Zero, output);

        engine.SetConfig(config with { ActiveProfile = "Solid White" });
        engine.Render(TimeSpan.FromSeconds(1), output);
        Assert.Single(output.Modes);

        engine.SetDevices([Keyboard]);
        engine.Render(TimeSpan.FromSeconds(2), output);
        Assert.Equal(2, output.Modes.Count);
    }

    [Fact]
    public void ResendAll_PushesModesAndFramesAgain()
    {
        var engine = Engine(Config("Solid White"));
        engine.SetDevices([Keyboard, Ram]);
        var output = new RecordingOutput();
        engine.Render(TimeSpan.Zero, output);
        var writes = output.LedWrites[1];

        engine.ResendAll();
        engine.Render(TimeSpan.FromSeconds(5), output);

        Assert.Equal(4, output.Modes.Count);
        Assert.Equal(writes + 1, output.LedWrites[1]);
    }

    [Fact]
    public void ClearDevices_StopsAllDeviceWrites()
    {
        var engine = Engine(Config(DefaultLighting.RainbowProfile));
        var output = new RecordingOutput();
        engine.SetDevices([Keyboard]);
        engine.Render(TimeSpan.Zero, output);

        engine.ClearDevices();
        engine.Render(TimeSpan.FromSeconds(1), output);

        Assert.Equal(1, output.LedWrites[0]);
    }

    [Fact]
    public void Strip_ScreenModeFallsBackToRadialAndSolidUsesItsOwnPower()
    {
        var config = Config(DefaultLighting.RainbowProfile);
        var engine = Engine(config, stripLeds: 113);
        var output = new RecordingOutput();

        Assert.NotNull(engine.Render(TimeSpan.Zero, output));
        Assert.Equal(1, output.StripWrites);
        Assert.Equal(113, output.Strip.Length);

        var solid = config.Profiles[0] with
        {
            MasterBrightnessPercent = 50,
            Ambilight = new AmbilightConfig { Mode = AmbilightMode.Solid, SolidColor = "#FF0000", SolidBrightnessPercent = 100, BrightnessPercent = 10 },
        };
        engine.SetConfig(config with { Profiles = [solid] });
        Assert.Null(engine.Render(TimeSpan.FromSeconds(1), output));
        Assert.All(output.Strip, c => Assert.Equal(new RgbColor(128, 0, 0), c));
    }

    [Fact]
    public void Blackout_SendsBlackOnceThenReturnsToTheProfile()
    {
        var engine = Engine(Config(DefaultLighting.RainbowProfile), stripLeds: 113);
        engine.SetDevices([Keyboard]);
        var output = new RecordingOutput();
        engine.Render(TimeSpan.Zero, output);

        engine.SetBlackout(true);
        var now = TimeSpan.FromSeconds(1);
        for (var next = engine.Render(now, output); next is not null; next = engine.Render(now, output))
        {
            now += next.Value;
        }

        Assert.All(output.Leds[0], c => Assert.Equal(RgbColor.Black, c));
        Assert.All(output.Strip, c => Assert.Equal(RgbColor.Black, c));
        var writes = output.LedWrites[0];
        Assert.Null(engine.Render(TimeSpan.FromSeconds(2), output));
        Assert.Equal(writes, output.LedWrites[0]);

        engine.SetBlackout(false);
        Assert.NotNull(engine.Render(TimeSpan.FromSeconds(3), output));
        Assert.Contains(output.Leds[0], c => c != RgbColor.Black);
    }

    [Fact]
    public void Situation_SelectsTheLogonAndLockProfiles()
    {
        var config = Config(DefaultLighting.RainbowProfile) with { LogonProfile = "Solid White", LockProfile = "Night" };
        var engine = Engine(config);
        engine.SetDevices([Keyboard]);
        var output = new RecordingOutput();

        // Static Solid White waits only for its repeat (250 ms); animated Rainbow is due within a frame (30 FPS).
        engine.SetSituation(new(SessionKind.LogonScreen, DisplayOn: true));
        Assert.Equal(TimeSpan.FromMilliseconds(250), engine.Render(TimeSpan.Zero, output));
        Assert.All(output.Leds[0], c => Assert.Equal(new RgbColor(255, 255, 255), c));

        engine.SetSituation(new(SessionKind.Unlocked, DisplayOn: true));
        Assert.InRange(engine.Render(TimeSpan.FromSeconds(1), output)!.Value, TimeSpan.Zero, TimeSpan.FromMilliseconds(40));
    }

    [Fact]
    public void DisplayOffWhileLocked_TurnsOffTheSetAndKeepsTheRest()
    {
        var engine = Engine(Config("Solid White"), stripLeds: 113);
        var gpu = Mouse with { Name = DefaultLighting.Gpu };
        engine.SetDevices([Keyboard, gpu]);
        var output = new RecordingOutput();
        engine.Render(TimeSpan.Zero, output);
        var modes = output.Modes.Count;

        engine.SetSituation(new(SessionKind.Locked, DisplayOn: false));
        engine.Render(TimeSpan.FromSeconds(1), output);

        Assert.All(output.Leds[0], c => Assert.Equal(RgbColor.Black, c));
        Assert.All(output.Strip, c => Assert.Equal(RgbColor.Black, c));
        Assert.Contains(output.Leds[1], c => c != RgbColor.Black);
        Assert.Equal(modes, output.Modes.Count); // still Direct, no mode change on the device

        engine.SetSituation(new(SessionKind.Locked, DisplayOn: true));
        engine.Render(TimeSpan.FromSeconds(2), output);
        Assert.All(output.Leds[0], c => Assert.Equal(new RgbColor(255, 255, 255), c));
        Assert.Contains(output.Strip, c => c != RgbColor.Black);
    }

    [Fact]
    public void DisplayOffWhileUnlocked_TurnsOffOnlyTheStripByDefault()
    {
        var engine = Engine(Config("Solid White"), stripLeds: 113);
        engine.SetDevices([Keyboard]);
        var output = new RecordingOutput();

        engine.SetSituation(new(SessionKind.Unlocked, DisplayOn: false));
        engine.Render(TimeSpan.Zero, output);

        Assert.All(output.Strip, c => Assert.Equal(RgbColor.Black, c));
        Assert.All(output.Leds[0], c => Assert.Equal(new RgbColor(255, 255, 255), c));
    }

    [Fact]
    public void Render_DoesNotAllocate()
    {
        var engine = Engine(Config(DefaultLighting.RainbowProfile), stripLeds: 113);
        engine.SetDevices([Keyboard, Ram, Mouse with { Name = DefaultLighting.Mouse }, LedLayoutBuilderTests.Board(60)]);
        var output = new RecordingOutput();
        var now = TimeSpan.Zero;
        for (var i = 0; i < 10; i++)
        {
            now += engine.Render(now, output) ?? TimeSpan.FromMilliseconds(10);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 500; i++)
        {
            now += engine.Render(now, output) ?? TimeSpan.FromMilliseconds(10);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void ScreenMode_ShowsTheFallbackUntilTheAgentSendsColors()
    {
        var engine = Engine(ScreenConfig(), stripLeds: 4);
        var output = new RecordingOutput();

        Assert.True(engine.IsScreenWanted);
        Assert.False(engine.PushScreenFrame([new RgbColor(9, 9, 9)]));
        Assert.NotNull(engine.Render(TimeSpan.Zero, output));
        Assert.Contains(output.Strip, c => c != default);

        RgbColor[] screen = [new(200, 0, 0), new(0, 200, 0), new(0, 0, 200), new(10, 20, 30)];
        engine.SetScreenFeed(true);
        engine.Render(TimeSpan.FromSeconds(1), output);
        Assert.True(engine.PushScreenFrame(screen));
        var writes = output.StripWrites;
        Assert.Null(engine.Render(TimeSpan.FromSeconds(1.1), output));
        Assert.Equal(writes + 1, output.StripWrites);
        // 50% strip brightness from the profile is applied to the Agent's colors.
        Assert.Equal([new(100, 0, 0), new(0, 100, 0), new(0, 0, 100), new(5, 10, 15)], output.Strip);

        engine.SetScreenFeed(false);
        Assert.NotNull(engine.Render(TimeSpan.FromSeconds(2), output));
    }

    [Fact]
    public void ScreenIsNotWantedWhileLockedOrAllOff()
    {
        var engine = Engine(ScreenConfig(), stripLeds: 4);
        engine.SetScreenFeed(true);

        engine.SetSituation(new LightingSituation(SessionKind.Locked, DisplayOn: true));
        Assert.False(engine.IsScreenWanted);
        var output = new RecordingOutput();
        Assert.NotNull(engine.Render(TimeSpan.Zero, output));
        Assert.False(engine.PushScreenFrame([new RgbColor(1, 2, 3)]));

        engine.SetSituation(LightingSituation.Default);
        Assert.True(engine.IsScreenWanted);
        engine.SetAllOff(true);
        Assert.False(engine.IsScreenWanted);
        engine.Render(TimeSpan.FromSeconds(1), output);
        Assert.All(output.Strip, c => Assert.Equal(default, c));
    }

    private static LightingConfig ScreenConfig() => new()
    {
        ActiveProfile = "Screen",
        Profiles = [new ProfileConfig { Name = "Screen", Ambilight = new AmbilightConfig { Mode = AmbilightMode.Screen, BrightnessPercent = 50 } }],
    };

    private static LightingEngine Engine(LightingConfig config, int stripLeds = 0) => new(config, stripLeds, 30, Logger.None);

    private static LightingConfig Config(string activeProfile) => new() { ActiveProfile = activeProfile };

    /// <summary>Records writes into buffers sized up front, so it does not allocate while the engine renders.</summary>
    private sealed class RecordingOutput : ILightingOutput
    {
        public Dictionary<int, int> LedWrites { get; } = [];

        public Dictionary<int, RgbColor[]> Leds { get; } = [];

        public List<(int Device, string Mode)> Modes { get; } = [];

        public RgbColor[] Strip { get; private set; } = [];

        public int StripWrites { get; private set; }

        public void SetDeviceMode(int deviceIndex, int modeIndex, ControllerMode mode) => Modes.Add((deviceIndex, mode.Name));

        public void SetDeviceLeds(int deviceIndex, ReadOnlySpan<RgbColor> colors)
        {
            if (!Leds.TryGetValue(deviceIndex, out var buffer) || buffer.Length != colors.Length)
            {
                Leds[deviceIndex] = buffer = new RgbColor[colors.Length];
            }

            colors.CopyTo(buffer);
            LedWrites[deviceIndex] = LedWrites.GetValueOrDefault(deviceIndex) + 1;
        }

        public void SetStripLeds(ReadOnlySpan<RgbColor> colors)
        {
            if (Strip.Length != colors.Length)
            {
                Strip = new RgbColor[colors.Length];
            }

            colors.CopyTo(Strip);
            StripWrites++;
        }
    }
}
