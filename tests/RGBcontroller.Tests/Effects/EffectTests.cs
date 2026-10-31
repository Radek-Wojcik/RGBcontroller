using RGBcontroller.Core;
using RGBcontroller.Core.Config;
using RGBcontroller.Core.Effects;
using RGBcontroller.Core.Lighting;

namespace RGBcontroller.Tests.Effects;

public sealed class EffectTests
{
    private static Effect Create(EffectKind kind, double speed = 0.25, double angle = 0, double scale = 1, bool reverse = false, string palette = "Rainbow") =>
        Effect.Create(new GroupEffectConfig { Group = "g", Effect = kind, Speed = speed, Angle = angle, Scale = scale, Reverse = reverse, Palette = palette },
            DefaultLighting.Palettes);

    private static RgbColor At(Effect effect, LedPoint point, double seconds)
    {
        Span<RgbColor> color = stackalloc RgbColor[1];
        effect.Render([point], color, seconds);
        return color[0];
    }

    private static RgbColor[] Render(Effect effect, LedPoint[] points, double seconds)
    {
        var colors = new RgbColor[points.Length];
        effect.Render(points, colors, seconds);
        return colors;
    }

    [Fact]
    public void Wave_At180Degrees_TravelsRightToLeft()
    {
        var wave = Create(EffectKind.Wave, speed: 0.25, angle: 180);

        // What the right edge shows now, a point 0.1 further left shows 0.1 / 0.25 s later.
        Assert.Equal(At(wave, new LedPoint(1, 0.5f), 3), At(wave, new LedPoint(0.9f, 0.5f), 3 + 0.4));
        Assert.True(wave.IsAnimated);
    }

    [Fact]
    public void Radial_RotatesClockwiseAndCounterclockwiseWhenReversed()
    {
        var ring = LedLayoutBuilder.Ring(20);
        var step = 1.0 / 20 / 0.25; // time for the pattern to move one LED at 0.25 rotations per second

        var clockwise = Create(EffectKind.Radial);
        Assert.Equal(Render(clockwise, ring, 2)[3], Render(clockwise, ring, 2 + step)[4]);

        var counter = Create(EffectKind.Radial, reverse: true);
        Assert.Equal(Render(counter, ring, 2)[4], Render(counter, ring, 2 + step)[3]);
    }

    [Fact]
    public void Mirror_IsSymmetricOnARamStickAndMovesOutward()
    {
        var stick = Enumerable.Range(0, 5).Select(i => new LedPoint(0.5f, i / 4f)).ToArray();
        var mirror = Create(EffectKind.Mirror, speed: 0.3, angle: 90, scale: 0.5);

        for (var t = 0.0; t < 3; t += 0.37)
        {
            var colors = Render(mirror, stick, t);
            Assert.Equal(colors[0], colors[4]);
            Assert.Equal(colors[1], colors[3]);
        }

        // The middle LED's color reaches the next LED (distance 0.5, coordinate 0.25) 0.25 / 0.3 s later.
        Assert.Equal(Render(mirror, stick, 1)[2], Render(mirror, stick, 1 + 0.25 / 0.3)[1]);
    }

    [Fact]
    public void ColorCycle_PaintsEveryLedTheSameColor()
    {
        var colors = Render(Create(EffectKind.ColorCycle, speed: 0.1), LedLayoutBuilder.Ring(7), 4.2);

        Assert.All(colors, c => Assert.Equal(colors[0], c));
    }

    [Fact]
    public void Breathing_GoesFromBlackToFullColor()
    {
        var breathing = Create(EffectKind.Breathing, speed: 0.5, palette: "#FF8000");

        Assert.Equal(RgbColor.Black, At(breathing, LedPoint.Center, 0));
        Assert.Equal(new RgbColor(255, 128, 0), At(breathing, LedPoint.Center, 1));
    }

    [Fact]
    public void Static_And_Off_AreNotAnimated()
    {
        Assert.False(Create(EffectKind.Static).IsAnimated);
        Assert.False(Create(EffectKind.Off).IsAnimated);
        Assert.False(Create(EffectKind.Hardware).IsAnimated);
        Assert.Equal(RgbColor.Black, At(Create(EffectKind.Off), LedPoint.Center, 1));
    }

    [Theory]
    [InlineData(EffectKind.Wave)]
    [InlineData(EffectKind.Radial)]
    [InlineData(EffectKind.Mirror)]
    [InlineData(EffectKind.ColorCycle)]
    public void HueEffects_WithOneColorOrNoSpeed_AreStatic(EffectKind kind)
    {
        Assert.False(Create(kind, palette: "#FFFFFF").IsAnimated);
        Assert.False(Create(kind, speed: 0).IsAnimated);
        Assert.True(Create(kind).IsAnimated);
    }

    [Fact]
    public void Aurora_StaysInsideThePaletteAndNeverGoesDark()
    {
        var aurora = Create(EffectKind.Aurora, speed: 0.05, palette: "Aurora");
        var grid = Enumerable.Range(0, 30).Select(i => new LedPoint(i % 6 / 5f, i / 6 / 4f)).ToArray();

        for (var t = 0.0; t < 60; t += 7.3)
        {
            Assert.All(Render(aurora, grid, t), c => Assert.True(Math.Max(c.R, Math.Max(c.G, c.B)) > 50, $"{c} too dark"));
        }

        Assert.True(aurora.IsAnimated);
    }
}
