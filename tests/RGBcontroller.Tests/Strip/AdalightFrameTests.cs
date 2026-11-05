using RGBcontroller.Core;
using RGBcontroller.Core.Config;
using RGBcontroller.Core.Strip;

namespace RGBcontroller.Tests.Strip;

public sealed class AdalightFrameTests
{
    [Fact]
    public void Header_MatchesVerifiedProtocolFor113Leds()
    {
        var frame = new AdalightFrame(113, ColorOrder.Rgb, 4500);

        // hi/lo = 112 big-endian, chk = hi ^ lo ^ 0x55 (SPEC section 2, verified on the hardware).
        Assert.Equal(new byte[] { (byte)'A', (byte)'d', (byte)'a', 0, 112, 0 ^ 112 ^ 0x55 }, frame.Bytes[..6].ToArray());
        Assert.Equal(345, frame.Bytes.Length);
    }

    [Fact]
    public void MaxFramesPerSecond_MatchesMeasuredLinkLimit()
    {
        Assert.Equal(33.4, AdalightFrame.MaxFramesPerSecond(115200, 113), precision: 1);
    }

    [Theory]
    [InlineData(ColorOrder.Rgb, 10, 20, 30)]
    [InlineData(ColorOrder.Grb, 20, 10, 30)]
    [InlineData(ColorOrder.Bgr, 30, 20, 10)]
    [InlineData(ColorOrder.Brg, 30, 10, 20)]
    [InlineData(ColorOrder.Rbg, 10, 30, 20)]
    [InlineData(ColorOrder.Gbr, 20, 30, 10)]
    public void Render_AppliesColorOrder(ColorOrder order, byte b0, byte b1, byte b2)
    {
        var frame = new AdalightFrame(1, order, 4500);

        frame.Render([new RgbColor(10, 20, 30)], 100);

        Assert.Equal(new[] { b0, b1, b2 }, frame.Bytes[6..].ToArray());
    }

    [Fact]
    public void Render_FullBrightnessIsFullDiodePower()
    {
        var frame = new AdalightFrame(2, ColorOrder.Rgb, 4500);

        frame.Render([new RgbColor(255, 255, 255), new RgbColor(255, 0, 0)], 100);

        Assert.Equal(new byte[] { 255, 255, 255, 255, 0, 0 }, frame.Bytes[6..].ToArray());
        Assert.False(frame.WasLimited);
    }

    [Fact]
    public void Render_ScalesBrightnessAndBlackIsExactlyZero()
    {
        var frame = new AdalightFrame(2, ColorOrder.Rgb, 4500);

        frame.Render([new RgbColor(200, 100, 2), RgbColor.Black], 50);

        Assert.Equal(new byte[] { 100, 50, 1, 0, 0, 0 }, frame.Bytes[6..].ToArray());
    }

    [Fact]
    public void Render_MissingColorsAreBlack()
    {
        var frame = new AdalightFrame(3, ColorOrder.Rgb, 4500);
        frame.Render([new RgbColor(9, 9, 9), new RgbColor(9, 9, 9), new RgbColor(9, 9, 9)], 100);

        frame.Render([new RgbColor(1, 2, 3)], 100);

        Assert.Equal(new byte[] { 1, 2, 3, 0, 0, 0, 0, 0, 0 }, frame.Bytes[6..].ToArray());
    }

    [Fact]
    public void FullWhite113Leds_EstimatesMeasuredCurrent()
    {
        var frame = new AdalightFrame(113, ColorOrder.Rgb, 4500);
        var white = Enumerable.Repeat(new RgbColor(255, 255, 255), 113).ToArray();

        frame.Render(white, 100);

        // Measured: 2.2 A at 100% white; estimate 113 x 19.5 mA = 2203.5 mA.
        Assert.Equal(2203.5, frame.EstimatedMilliamps, precision: 1);
        Assert.False(frame.WasLimited);
    }

    [Fact]
    public void CurrentLimiter_DimsUniformlyBelowTheLimit()
    {
        var frame = new AdalightFrame(113, ColorOrder.Rgb, 1000);
        var white = Enumerable.Repeat(new RgbColor(255, 255, 255), 113).ToArray();

        frame.Render(white, 100);

        Assert.True(frame.WasLimited);
        Assert.InRange(frame.EstimatedMilliamps, 950, 1000);
        var payload = frame.Bytes[6..].ToArray();
        Assert.All(payload, b => Assert.Equal(payload[0], b));
    }

    [Fact]
    public void Render_DoesNotAllocate()
    {
        var frame = new AdalightFrame(113, ColorOrder.Grb, 4500);
        var colors = new RgbColor[113];
        frame.Render(colors, 80);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
        {
            frame.Render(colors, 80);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Theory]
    [InlineData("#00E5FF", true, 0, 229, 255)]
    [InlineData("#ffffff", true, 255, 255, 255)]
    [InlineData("00E5FF", false, 0, 0, 0)]
    [InlineData("#00E5F", false, 0, 0, 0)]
    [InlineData("#GG0000", false, 0, 0, 0)]
    public void RgbColor_ParsesHex(string text, bool ok, byte r, byte g, byte b)
    {
        Assert.Equal(ok, RgbColor.TryParseHex(text, out var color));
        Assert.Equal(new RgbColor(r, g, b), color);
    }
}
