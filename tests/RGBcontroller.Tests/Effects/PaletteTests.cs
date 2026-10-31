using RGBcontroller.Core;
using RGBcontroller.Core.Config;
using RGBcontroller.Core.Effects;

namespace RGBcontroller.Tests.Effects;

public sealed class PaletteTests
{
    private static readonly RgbColor Red = new(255, 0, 0);
    private static readonly RgbColor Yellow = new(255, 255, 0);
    private static readonly RgbColor Cyan = new(0, 255, 255);

    private static Palette Rainbow => Palette.Resolve("Rainbow", DefaultLighting.Palettes)!;

    [Fact]
    public void Rainbow_HitsTheHueWheelStops()
    {
        Assert.Equal(Red, Rainbow.Sample(0));
        AssertNear(Yellow, Rainbow.Sample(1.0 / 6));
        AssertNear(Cyan, Rainbow.Sample(0.5));
        Assert.False(Rainbow.IsSolid);
    }

    /// <summary>The lookup table has 1024 entries, so a stop is hit within one step (at most 2 of 255 per channel).</summary>
    private static void AssertNear(RgbColor expected, RgbColor actual)
    {
        Assert.InRange(actual.R, expected.R - 2, expected.R + 2);
        Assert.InRange(actual.G, expected.G - 2, expected.G + 2);
        Assert.InRange(actual.B, expected.B - 2, expected.B + 2);
    }

    [Fact]
    public void Rainbow_BlendsBetweenStops()
    {
        var orange = Rainbow.Sample(1.0 / 12);

        Assert.Equal(255, orange.R);
        Assert.InRange(orange.G, 125, 130);
        Assert.Equal(0, orange.B);
    }

    [Theory]
    [InlineData(1.25, 0.25)]
    [InlineData(-0.25, 0.75)]
    [InlineData(7.0, 0.0)]
    public void Sample_UsesOnlyTheFractionalPart(double t, double same)
    {
        Assert.Equal(Rainbow.Sample(same), Rainbow.Sample(t));
    }

    [Fact]
    public void LastStop_BlendsBackIntoTheFirst()
    {
        var palette = new Palette([(0, new RgbColor(0, 0, 0)), (0.5, new RgbColor(200, 200, 200))]);

        Assert.Equal(new RgbColor(100, 100, 100), palette.Sample(0.75));
        Assert.Equal(new RgbColor(100, 100, 100), palette.Sample(0.25));
    }

    [Fact]
    public void Resolve_HexIsASolidColor()
    {
        var palette = Palette.Resolve("#112233", [])!;

        Assert.True(palette.IsSolid);
        Assert.Equal(new RgbColor(0x11, 0x22, 0x33), palette.Sample(0.37));
    }

    [Fact]
    public void Resolve_NamesAreCaseInsensitiveAndUnknownIsNull()
    {
        Assert.NotNull(Palette.Resolve("cyberpunk", DefaultLighting.Palettes));
        Assert.Null(Palette.Resolve("Nope", DefaultLighting.Palettes));
        Assert.Null(Palette.Resolve("#12345", DefaultLighting.Palettes));
    }
}
