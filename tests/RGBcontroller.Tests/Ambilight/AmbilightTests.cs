using RGBcontroller.Core;
using RGBcontroller.Core.Ambilight;
using RGBcontroller.Core.Config;

namespace RGBcontroller.Tests.Ambilight;

/// <summary>A BGRA8 test image filled per pixel.</summary>
internal sealed class TestImage(int width, int height)
{
    public byte[] Data { get; } = new byte[width * height * 4];

    public int Width => width;

    public int Height => height;

    public TestImage Fill(Func<int, int, RgbColor> color)
    {
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var c = color(x, y);
                var o = (y * width + x) * 4;
                Data[o] = c.B;
                Data[o + 1] = c.G;
                Data[o + 2] = c.R;
                Data[o + 3] = 255;
            }
        }

        return this;
    }

    public ScreenImage View() => new(Data, width, height, width * 4, ScreenPixelFormat.Bgra8);
}

public sealed class StripLayoutTests
{
    [Fact]
    public void ClockwiseFromBottomLeftGoesUpTheLeftEdgeFirst()
    {
        var layout = new StripLayout(new StripLayoutConfig { StartCorner = StripCorner.BottomLeft, Top = 4, Right = 2, Bottom = 4, Left = 2 }, 12);

        Assert.Equal(new StripLedPlacement(ScreenEdge.Left, 0.75f, 0.5f), layout.Leds[0]);
        Assert.Equal(new StripLedPlacement(ScreenEdge.Left, 0.25f, 0.5f), layout.Leds[1]);
        Assert.Equal(new StripLedPlacement(ScreenEdge.Top, 0.125f, 0.25f), layout.Leds[2]);
        Assert.Equal(ScreenEdge.Right, layout.Leds[6]!.Value.Edge);
        Assert.Equal(0.25f, layout.Leds[6]!.Value.Along);
        Assert.Equal(new StripLedPlacement(ScreenEdge.Bottom, 0.875f, 0.25f), layout.Leds[8]);
    }

    [Fact]
    public void CounterclockwiseFromTopRightGoesLeftAlongTheTop()
    {
        var layout = new StripLayout(new StripLayoutConfig { StartCorner = StripCorner.TopRight, Clockwise = false, Top = 2, Right = 1, Bottom = 2, Left = 1 }, 6);

        Assert.Equal(new StripLedPlacement(ScreenEdge.Top, 0.75f, 0.5f), layout.Leds[0]);
        Assert.Equal(new StripLedPlacement(ScreenEdge.Top, 0.25f, 0.5f), layout.Leds[1]);
        Assert.Equal(ScreenEdge.Left, layout.Leds[2]!.Value.Edge);
        Assert.Equal(new StripLedPlacement(ScreenEdge.Bottom, 0.25f, 0.5f), layout.Leds[3]);
        Assert.Equal(ScreenEdge.Right, layout.Leds[5]!.Value.Edge);
    }

    [Fact]
    public void GapsStayDarkAndOffsetRotatesThePath()
    {
        var config = new StripLayoutConfig { StartCorner = StripCorner.TopLeft, Top = 2, Right = 1, Bottom = 2, Left = 1, Offset = 1, Gaps = [0] };
        var layout = new StripLayout(config, 8);

        Assert.Null(layout.Leds[0]);
        Assert.Equal(new StripLedPlacement(ScreenEdge.Top, 0.75f, 0.5f), layout.Leds[1]);
        Assert.Equal(new StripLedPlacement(ScreenEdge.Left, 0.5f, 1f), layout.Leds[5]);
        Assert.Equal(new StripLedPlacement(ScreenEdge.Top, 0.25f, 0.5f), layout.Leds[6]);
        // Past the end of the path the strip overlaps its start.
        Assert.Equal(layout.Leds[1], layout.Leds[7]);
        Assert.Equal([(1, 7)], layout.LitRuns());
    }

    [Fact]
    public void OwnersStripStartsMidwayUpTheLeftEdgeAndOverlapsThreeLeds()
    {
        // 14 LEDs up the left edge, 41 top, 23 right, 43 bottom, 12 up the left edge again (3 beside the first ones).
        var config = new StripLayoutConfig { StartCorner = StripCorner.BottomLeft, Left = 23, Top = 41, Right = 23, Bottom = 43, Offset = 9 };
        var layout = new StripLayout(config, 133);

        Assert.Equal(ScreenEdge.Left, layout.Leds[0]!.Value.Edge);
        Assert.InRange(layout.Leds[0]!.Value.Along, 0.5f, 0.6f);
        Assert.Equal(ScreenEdge.Left, layout.Leds[13]!.Value.Edge);
        Assert.Equal(new StripLedPlacement(ScreenEdge.Top, 0.5f / 41, 1f / 41), layout.Leds[14]);
        Assert.Equal(ScreenEdge.Right, layout.Leds[55]!.Value.Edge);
        Assert.Equal(ScreenEdge.Bottom, layout.Leds[78]!.Value.Edge);
        Assert.Equal(new StripLedPlacement(ScreenEdge.Left, 1 - 0.5f / 23, 1f / 23), layout.Leds[121]);
        Assert.Equal(layout.Leds[0], layout.Leds[130]);
        Assert.Equal(layout.Leds[2], layout.Leds[132]);
    }
}

public sealed class AmbilightPipelineTests
{
    private static readonly ScreenCaptureConfig Linear = new()
    {
        Gamma = 1, BlackThreshold = 0, BlackKnee = 0, AttackMilliseconds = 0, ReleaseMilliseconds = 0, BlackReleaseMilliseconds = 0,
        LetterboxDetection = false, FadeInMilliseconds = 0,
    };

    private static StripLayout FourLeds() =>
        new(new StripLayoutConfig { StartCorner = StripCorner.TopLeft, Top = 1, Right = 1, Bottom = 1, Left = 1 }, 4);

    [Fact]
    public void EachLedAveragesItsOwnEdge()
    {
        var config = Linear with { SegmentOverlapPercent = 0, EdgeDepthCm = 5 };
        var topBottom = new StripLayout(new StripLayoutConfig { StartCorner = StripCorner.TopLeft, Top = 1, Right = 0, Bottom = 1, Left = 0 }, 2);
        var sides = new StripLayout(new StripLayoutConfig { StartCorner = StripCorner.TopLeft, Top = 0, Right = 1, Bottom = 0, Left = 1 }, 2);
        var rows = new TestImage(100, 60).Fill((_, y) => y < 10 ? new RgbColor(255, 0, 0) : y >= 50 ? new RgbColor(0, 0, 255) : new RgbColor(0, 255, 0));
        var columns = new TestImage(100, 60).Fill((x, _) => x < 10 ? new RgbColor(0, 255, 0) : x >= 90 ? new RgbColor(255, 255, 0) : default);

        var pipeline = new AmbilightPipeline(topBottom, config, screenWidthCm: 50);
        pipeline.OnImage(rows.View(), TimeSpan.Zero);
        Assert.True(pipeline.Step(TimeSpan.FromMilliseconds(33)));
        Assert.Equal([new RgbColor(255, 0, 0), new RgbColor(0, 0, 255)], pipeline.Output.ToArray());

        pipeline = new AmbilightPipeline(sides, config, screenWidthCm: 50);
        pipeline.OnImage(columns.View(), TimeSpan.Zero);
        pipeline.Step(TimeSpan.FromMilliseconds(33));
        Assert.Equal([new RgbColor(255, 255, 0), new RgbColor(0, 255, 0)], pipeline.Output.ToArray());
    }

    [Fact]
    public void BlackScreenTurnsTheStripCompletelyOffWithoutATail()
    {
        var config = new ScreenCaptureConfig { FadeInMilliseconds = 0, LetterboxDetection = false };
        var pipeline = new AmbilightPipeline(FourLeds(), config, 50);
        pipeline.OnImage(new TestImage(64, 36).Fill((_, _) => new RgbColor(255, 255, 255)).View(), TimeSpan.Zero);
        for (var i = 0; i < 30; i++)
        {
            pipeline.Step(TimeSpan.FromMilliseconds(33));
        }

        Assert.Equal(new RgbColor(255, 255, 255), pipeline.Output[0]);

        pipeline.OnImage(new TestImage(64, 36).View(), TimeSpan.FromSeconds(1));
        var steps = 0;
        while (!pipeline.IsSettled && steps < 100)
        {
            pipeline.Step(TimeSpan.FromMilliseconds(33));
            steps++;
        }

        Assert.All(pipeline.Output.ToArray(), c => Assert.Equal(default, c));
        // 60 ms black release at 33 ms steps: black within about 0.3 s, not the 300 ms release tail of colors.
        Assert.InRange(steps, 1, 10);
        Assert.False(pipeline.Step(TimeSpan.FromMilliseconds(33)));
    }

    [Fact]
    public void NearBlackNoiseIsZeroAndDarkScenesStayDark()
    {
        var pipeline = new AmbilightPipeline(FourLeds(), new ScreenCaptureConfig { FadeInMilliseconds = 0, AttackMilliseconds = 0, LetterboxDetection = false }, 50);

        pipeline.OnImage(new TestImage(64, 36).Fill((_, _) => new RgbColor(8, 9, 10)).View(), TimeSpan.Zero);
        pipeline.Step(TimeSpan.FromMilliseconds(33));
        Assert.Equal(default, pipeline.Output[0]);

        pipeline.OnImage(new TestImage(64, 36).Fill((_, _) => new RgbColor(64, 64, 64)).View(), TimeSpan.Zero);
        pipeline.Step(TimeSpan.FromMilliseconds(33));
        // Gamma 1.5: a quarter-bright grey becomes about 1/8, still dark.
        Assert.InRange(pipeline.Output[0].R, 30, 34);
    }

    [Fact]
    public void ReleaseIsSlowerThanAttack()
    {
        var config = new ScreenCaptureConfig { FadeInMilliseconds = 0, Gamma = 1, LetterboxDetection = false };
        var pipeline = new AmbilightPipeline(FourLeds(), config, 50);

        pipeline.OnImage(new TestImage(64, 36).Fill((_, _) => new RgbColor(200, 200, 200)).View(), TimeSpan.Zero);
        pipeline.Step(TimeSpan.FromMilliseconds(80));
        var risen = pipeline.Output[0].R;
        for (var i = 0; i < 50; i++)
        {
            pipeline.Step(TimeSpan.FromMilliseconds(33));
        }

        pipeline.OnImage(new TestImage(64, 36).Fill((_, _) => new RgbColor(100, 100, 100)).View(), TimeSpan.Zero);
        pipeline.Step(TimeSpan.FromMilliseconds(80));
        var fallen = 200 - pipeline.Output[0].R;

        // 40 ms attack and 120 ms release: after 80 ms 86% of the rise and 49% of the fall.
        Assert.InRange(risen, 168, 178);
        Assert.InRange(fallen, 45, 53);
    }

    [Fact]
    public void ANewPipelineFadesIn()
    {
        var config = new ScreenCaptureConfig { FadeInMilliseconds = 800, AttackMilliseconds = 0, Gamma = 1, LetterboxDetection = false };
        var pipeline = new AmbilightPipeline(FourLeds(), config, 50);
        pipeline.OnImage(new TestImage(64, 36).Fill((_, _) => new RgbColor(200, 200, 200)).View(), TimeSpan.Zero);

        pipeline.Step(TimeSpan.FromMilliseconds(200));
        Assert.InRange(pipeline.Output[0].R, 49, 51);
        Assert.False(pipeline.IsSettled);

        pipeline.Step(TimeSpan.FromMilliseconds(600));
        Assert.Equal(200, pipeline.Output[0].R);
        Assert.True(pipeline.IsSettled);

        // Only the start fades: later changes follow the attack time constant.
        pipeline.OnImage(new TestImage(64, 36).Fill((_, _) => new RgbColor(250, 250, 250)).View(), TimeSpan.FromSeconds(1));
        pipeline.Step(TimeSpan.FromMilliseconds(33));
        Assert.Equal(250, pipeline.Output[0].R);
    }
}

public sealed class LetterboxDetectorTests
{
    private static TestImage Letterboxed(int bar, bool subtitles = false) => new TestImage(200, 100).Fill((x, y) =>
        y < bar || y >= 100 - bar
            ? subtitles && y > 100 - bar / 2 && x is > 80 and < 120 ? new RgbColor(255, 255, 255) : default
            : new RgbColor(120, 60, 30));

    [Fact]
    public void BarsAreAdoptedOnlyAfterTheStableTime()
    {
        var detector = new LetterboxDetector(TimeSpan.FromSeconds(1));
        var image = Letterboxed(12);

        Assert.Equal(new ContentRect(0, 0, 200, 100), detector.Update(image.View(), 1, TimeSpan.Zero));
        Assert.Equal(new ContentRect(0, 0, 200, 100), detector.Update(image.View(), 1, TimeSpan.FromSeconds(0.5)));
        Assert.Equal(new ContentRect(0, 12, 200, 88), detector.Update(image.View(), 1, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void SubtitlesInTheBottomBarAndDarkScenesDoNotMoveTheBands()
    {
        var detector = new LetterboxDetector(TimeSpan.Zero);
        detector.Update(Letterboxed(12).View(), 1, TimeSpan.Zero);
        detector.Update(Letterboxed(12).View(), 1, TimeSpan.Zero);

        Assert.Equal(new ContentRect(0, 12, 200, 88), detector.Update(Letterboxed(12, subtitles: true).View(), 1, TimeSpan.FromSeconds(1)));
        Assert.Equal(new ContentRect(0, 12, 200, 88), detector.Update(new TestImage(200, 100).View(), 1, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void PlayerOverlaysOnAPillarboxDoNotHideIt()
    {
        // A 4:3 film in a fullscreen player: the title runs over the left bar, the progress bar and buttons cross both bars.
        var detector = new LetterboxDetector(TimeSpan.Zero);
        var image = new TestImage(200, 100).Fill((x, y) =>
            y is >= 2 and < 5 && x < 120 ? new RgbColor(255, 255, 255)
            : y is >= 92 and < 96 ? new RgbColor(230, 30, 30)
            : x is < 25 or >= 175 ? default : new RgbColor(120, 90, 160));

        detector.Update(image.View(), 1, TimeSpan.Zero);

        Assert.Equal(new ContentRect(24, 0, 176, 100), detector.Update(image.View(), 1, TimeSpan.Zero));
    }

    [Fact]
    public void ALogoInTheBottomBarDoesNotHideTheLetterbox()
    {
        // A 2.39:1 clip whose bottom bar carries a channel logo in its left quarter.
        var detector = new LetterboxDetector(TimeSpan.Zero);
        var image = new TestImage(200, 100).Fill((x, y) =>
            y < 12 ? default
            : y >= 88 ? x is >= 6 and < 50 && y is >= 91 and < 97 ? new RgbColor(255, 255, 255) : default
            : new RgbColor(90, 140, 60));

        detector.Update(image.View(), 1, TimeSpan.Zero);

        Assert.Equal(new ContentRect(0, 12, 200, 88), detector.Update(image.View(), 1, TimeSpan.Zero));
    }

    [Fact]
    public void ThinLinesAcrossTheBarsDoNotEndThem()
    {
        // A 21:9 trailer in a fullscreen player: a full-width red progress bar inside the bottom bar, a title line in the top bar.
        var detector = new LetterboxDetector(TimeSpan.Zero);
        var image = new TestImage(200, 100).Fill((x, y) =>
            y is 3 && x < 150 ? new RgbColor(255, 255, 255)
            : y is 94 ? new RgbColor(220, 20, 20)
            : y is < 12 or >= 88 ? default
            : new RgbColor(30, 90, 120));

        detector.Update(image.View(), 1, TimeSpan.Zero);

        Assert.Equal(new ContentRect(0, 12, 200, 88), detector.Update(image.View(), 1, TimeSpan.Zero));
    }

    [Fact]
    public void PillarboxBarsAreFound()
    {
        var detector = new LetterboxDetector(TimeSpan.Zero);
        var image = new TestImage(200, 100).Fill((x, _) => x is < 20 or >= 180 ? default : new RgbColor(90, 90, 200));

        detector.Update(image.View(), 1, TimeSpan.Zero);

        Assert.Equal(new ContentRect(20, 0, 180, 100), detector.Update(image.View(), 1, TimeSpan.Zero));
    }
}
