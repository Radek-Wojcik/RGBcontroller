using RGBcontroller.Core.Config;
using RGBcontroller.Core.Effects;

namespace RGBcontroller.Core.Ambilight;

public enum ScreenEdge
{
    Top,
    Right,
    Bottom,
    Left,
}

/// <param name="Along">Center of the LED along its edge, 0..1 in screen coordinates (x for top and bottom, y for left and right).</param>
/// <param name="Extent">Share of the edge length that belongs to this LED.</param>
public readonly record struct StripLedPlacement(ScreenEdge Edge, float Along, float Extent)
{
    /// <summary>Position on the screen outline, 0..1 with y pointing down.</summary>
    public LedPoint ToPoint() => Edge switch
    {
        ScreenEdge.Top => new LedPoint(Along, 0),
        ScreenEdge.Right => new LedPoint(1, Along),
        ScreenEdge.Bottom => new LedPoint(Along, 1),
        _ => new LedPoint(0, Along),
    };
}

/// <summary>Resolves <see cref="StripLayoutConfig"/> into a placement for every strip LED (null = dark LED).</summary>
public sealed class StripLayout
{
    public StripLayout(StripLayoutConfig config, int ledCount)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentOutOfRangeException.ThrowIfNegative(ledCount);
        var path = BuildPath(config);
        var gaps = config.Gaps.ToHashSet();
        var leds = new StripLedPlacement?[ledCount];
        var k = 0;
        for (var led = 0; led < ledCount; led++)
        {
            if (gaps.Contains(led))
            {
                continue;
            }

            // LEDs past the end of the path continue around the loop: an overlap where the strip end runs past its start.
            if (path.Count > 0)
            {
                leds[led] = path[(int)(((long)k + config.Offset) % path.Count + path.Count) % path.Count];
            }

            k++;
        }

        Leds = leds;
    }

    public IReadOnlyList<StripLedPlacement?> Leds { get; }

    public int LedCount => Leds.Count;

    /// <summary>Positions for effects (the radial fallback rotates around the screen center); dark LEDs sit in the center.</summary>
    public LedPoint[] ToPoints() => [.. Leds.Select(l => l?.ToPoint() ?? LedPoint.Center)];

    /// <summary>Runs of consecutive lit LEDs as (start, count), so dark LEDs are never written by an effect.</summary>
    public IReadOnlyList<(int Start, int Count)> LitRuns()
    {
        var runs = new List<(int, int)>();
        var start = -1;
        for (var i = 0; i <= Leds.Count; i++)
        {
            var lit = i < Leds.Count && Leds[i] is not null;
            if (lit && start < 0)
            {
                start = i;
            }
            else if (!lit && start >= 0)
            {
                runs.Add((start, i - start));
                start = -1;
            }
        }

        return runs;
    }

    /// <summary>Every edge position in path order: from the start corner, around the screen in the configured direction.</summary>
    private static List<StripLedPlacement> BuildPath(StripLayoutConfig config)
    {
        // Clockwise from the top-left corner (viewed from the front): top left to right, right top to bottom,
        // bottom right to left, left bottom to top.
        ScreenEdge[] clockwise = [ScreenEdge.Top, ScreenEdge.Right, ScreenEdge.Bottom, ScreenEdge.Left];
        var first = config.Clockwise
            ? config.StartCorner switch
            {
                StripCorner.TopLeft => 0,
                StripCorner.TopRight => 1,
                StripCorner.BottomRight => 2,
                _ => 3,
            }
            : config.StartCorner switch
            {
                StripCorner.TopLeft => 3,
                StripCorner.BottomLeft => 2,
                StripCorner.BottomRight => 1,
                _ => 0,
            };

        var path = new List<StripLedPlacement>(config.EdgeLeds);
        for (var step = 0; step < 4; step++)
        {
            var edge = config.Clockwise ? clockwise[(first + step) % 4] : clockwise[(first - step + 4) % 4];
            var count = edge switch
            {
                ScreenEdge.Top => config.Top,
                ScreenEdge.Right => config.Right,
                ScreenEdge.Bottom => config.Bottom,
                _ => config.Left,
            };

            // Clockwise travel goes along the axis on the top and right edges and against it on the bottom and left.
            var forward = (edge is ScreenEdge.Top or ScreenEdge.Right) == config.Clockwise;
            for (var j = 0; j < count; j++)
            {
                var t = (j + 0.5f) / count;
                path.Add(new StripLedPlacement(edge, forward ? t : 1 - t, 1f / count));
            }
        }

        return path;
    }
}
