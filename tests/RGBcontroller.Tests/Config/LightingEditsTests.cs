using RGBcontroller.Core.Config;

namespace RGBcontroller.Tests.Config;

public sealed class LightingEditsTests
{
    private static readonly LightingConfig Defaults = new();

    [Fact]
    public void FrameRate_ChangesTheGroupOnly()
    {
        var result = LightingEdits.Apply(Defaults, new GroupTuning("ram", MaxFps: 5), out var error)!;

        Assert.Null(error);
        Assert.Equal(5, result.Groups.Single(g => g.Name == DefaultLighting.RamGroup).MaxFps);
        Assert.Equal(30, result.Groups.Single(g => g.Name == DefaultLighting.KeyboardGroup).MaxFps);
        Assert.Equal(Defaults.Profiles, result.Profiles);
    }

    [Fact]
    public void Effect_ChangesInTheActiveProfileByDefault()
    {
        var result = LightingEdits.Apply(Defaults, new GroupTuning("Keyboard", Speed: 0.5, Scale: 2, BrightnessPercent: 40), out _)!;

        var keyboard = result.FindProfile("Rainbow")!.FindGroup("Keyboard")!;
        Assert.Equal((0.5, 2.0, 40), (keyboard.Speed, keyboard.Scale, keyboard.BrightnessPercent));
        Assert.Equal(EffectKind.Wave, keyboard.Effect);
        Assert.Equal(180, keyboard.Angle);
        Assert.Equal(Defaults.FindProfile("Cyberpunk"), result.FindProfile("Cyberpunk"));
        Assert.Empty(ConfigValidator.Validate(AppConfig.CreateDefault() with { Lighting = result }));
    }

    [Fact]
    public void Effect_CanTargetAnotherProfile()
    {
        var result = LightingEdits.Apply(Defaults, new GroupTuning("RAM", Profile: "aurora", Speed: 0.1), out _)!;

        Assert.Equal(0.1, result.FindProfile("Aurora")!.FindGroup("RAM")!.Speed);
        Assert.Equal(Defaults.FindProfile("Rainbow"), result.FindProfile("Rainbow"));
    }

    [Theory]
    [InlineData("Fridge", null)]
    [InlineData("RAM", "Disco")]
    public void UnknownNames_AreRejected(string group, string? profile)
    {
        Assert.Null(LightingEdits.Apply(Defaults, new GroupTuning(group, Profile: profile, Speed: 1), out var error));
        Assert.NotNull(error);
    }
}
