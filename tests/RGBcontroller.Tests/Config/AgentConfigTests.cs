using RGBcontroller.Core.Config;
using RGBcontroller.Core.Hotkeys;

namespace RGBcontroller.Tests.Config;

public sealed class AgentConfigTests
{
    [Theory]
    [InlineData("Ctrl+F9", HotkeyModifiers.Ctrl, 0x78, "Ctrl+F9")]
    [InlineData("alt + pageup", HotkeyModifiers.Alt, 0x21, "Alt+PageUp")]
    [InlineData("Win+B", HotkeyModifiers.Win, 'B', "Win+B")]
    [InlineData("Shift+Num5", HotkeyModifiers.Shift, 0x65, "Shift+Num5")]
    public void GesturesParseToRegisterHotKeyValues(string text, HotkeyModifiers modifier, int key, string canonical)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var gesture, out _));
        Assert.Equal(modifier, gesture.Modifier);
        Assert.Equal(key, gesture.VirtualKey);
        Assert.Equal(canonical, gesture.ToString());
    }

    [Theory]
    [InlineData("F9")]
    [InlineData("Ctrl+Alt+F9")]
    [InlineData("Ctrl+")]
    [InlineData("Hyper+F9")]
    [InlineData("Ctrl+Escape")]
    [InlineData("1+F9")]
    public void GesturesOtherThanModifierPlusKeyAreRejected(string text)
    {
        Assert.False(HotkeyGesture.TryParse(text, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void DefaultsAreValid()
    {
        Assert.Empty(ConfigValidator.Validate(AppConfig.CreateDefault()));
    }

    [Fact]
    public void DuplicateHotkeysAndUnknownProfilesAreReported()
    {
        var config = AppConfig.CreateDefault() with
        {
            Hotkeys = new HotkeysConfig
            {
                Bindings = [new("Ctrl+F9", HotkeyAction.NextProfile), new("ctrl+f9", HotkeyAction.PreviousProfile), new("Alt+P", HotkeyAction.Profile, "Nope")],
            },
        };

        var errors = ConfigValidator.Validate(config);

        Assert.Contains(errors, e => e.Contains("assigned twice", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("'Nope'", StringComparison.Ordinal));
    }

    [Fact]
    public void LayoutGapsMustBeStripLeds()
    {
        var config = AppConfig.CreateDefault();
        config = config with { Strip = config.Strip with { LedCount = 60, Layout = new StripLayoutConfig { Gaps = [3, 60] } } };

        Assert.Equal(["strip.layout.gaps must list distinct LED indices from 0 to 59."], ConfigValidator.Validate(config));
    }

    [Fact]
    public void CaptureRangesAreChecked()
    {
        var config = AppConfig.CreateDefault() with { Capture = new ScreenCaptureConfig { Gamma = double.NaN, CaptureFps = 0, Monitor = "COM3" } };

        var errors = ConfigValidator.Validate(config);

        Assert.Equal(3, errors.Count);
    }
}
