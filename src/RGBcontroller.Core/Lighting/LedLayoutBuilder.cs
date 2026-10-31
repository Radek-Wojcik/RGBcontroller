using RGBcontroller.Core.Config;
using RGBcontroller.Core.Effects;
using RGBcontroller.Core.OpenRgb;

namespace RGBcontroller.Core.Lighting;

/// <summary>
/// Gives every LED of an OpenRGB device a position inside its component, from the zone layouts in the config and
/// the device's own description (matrix maps, LED counts). Runs once per device enumeration, never per frame.
/// </summary>
public static class LedLayoutBuilder
{
    private const uint NoLed = uint.MaxValue;

    /// <summary>Positions indexed like <see cref="ControllerData.Leds"/>.</summary>
    public static LedPoint[] Build(ControllerData device, IReadOnlyList<ZoneLayoutConfig> layouts)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(layouts);
        var points = new LedPoint[device.Leds.Count];
        points.AsSpan().Fill(LedPoint.Center);
        var start = 0;
        foreach (var zone in device.Zones)
        {
            var count = (int)Math.Min(zone.LedsCount, (uint)Math.Max(0, points.Length - start));
            var layout = layouts.FirstOrDefault(l =>
                string.Equals(l.Device, device.Name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(l.Zone, zone.Name, StringComparison.OrdinalIgnoreCase));
            BuildZone(zone, layout?.Segments ?? [], points.AsSpan(start, count));
            start += count;
        }

        return points;
    }

    /// <summary>Positions for LEDs in a closed loop (the strip around the monitor until stage 4 adds the real layout).</summary>
    public static LedPoint[] Ring(int count, bool reverse = false, int offset = 0)
    {
        var points = new LedPoint[count];
        FillRing(points, reverse, offset);
        return points;
    }

    private static void BuildZone(ControllerZone zone, IReadOnlyList<SegmentLayoutConfig> segments, Span<LedPoint> zonePoints)
    {
        var start = 0;
        foreach (var segment in segments)
        {
            if (start >= zonePoints.Length)
            {
                break;
            }

            var count = Math.Min(segment.Leds ?? zonePoints.Length - start, zonePoints.Length - start);
            var wholeZone = start == 0 && count == zonePoints.Length;
            FillSegment(zone, segment, wholeZone, zonePoints.Slice(start, count));
            start += count;
        }

        if (start < zonePoints.Length)
        {
            FillSegment(zone, new SegmentLayoutConfig(), start == 0, zonePoints[start..]);
        }
    }

    private static void FillSegment(ControllerZone zone, SegmentLayoutConfig segment, bool wholeZone, Span<LedPoint> points)
    {
        var shape = segment.Shape;
        if (shape == LedShape.Auto)
        {
            shape = wholeZone && zone.Matrix.Count > 0 ? LedShape.Matrix
                : points.Length == 1 ? LedShape.Point
                : LedShape.Line;
        }

        switch (shape)
        {
            case LedShape.Matrix when wholeZone && zone.Matrix.Count > 0:
                FillMatrix(zone, points);
                break;
            case LedShape.Ring:
                FillRing(points, segment.Reverse, segment.Offset);
                break;
            case LedShape.Point:
                points.Fill(LedPoint.Center);
                break;
            default:
                FillLine(points, segment.Vertical, segment.Reverse);
                break;
        }
    }

    private static void FillLine(Span<LedPoint> points, bool vertical, bool reverse)
    {
        for (var i = 0; i < points.Length; i++)
        {
            var step = reverse ? points.Length - 1 - i : i;
            var along = points.Length == 1 ? 0.5f : (float)step / (points.Length - 1);
            points[i] = vertical ? new LedPoint(0.5f, along) : new LedPoint(along, 0.5f);
        }
    }

    /// <summary>LED <paramref name="offset"/> sits at 12 o'clock; the order runs clockwise unless reversed.</summary>
    private static void FillRing(Span<LedPoint> points, bool reverse, int offset)
    {
        var n = points.Length;
        for (var i = 0; i < n; i++)
        {
            var step = ((i - offset) % n + n) % n;
            var fraction = (double)(reverse ? (n - step) % n : step) / n;
            var radians = fraction * 2 * Math.PI;
            points[i] = new LedPoint((float)(0.5 + 0.5 * Math.Sin(radians)), (float)(0.5 - 0.5 * Math.Cos(radians)));
        }
    }

    /// <summary>OpenRGB matrix maps hold zone-relative LED indices row by row, <see cref="NoLed"/> for gaps.</summary>
    private static void FillMatrix(ControllerZone zone, Span<LedPoint> points)
    {
        var width = (int)zone.MatrixWidth;
        var height = (int)zone.MatrixHeight;
        for (var row = 0; row < height; row++)
        {
            for (var column = 0; column < width; column++)
            {
                var cell = row * width + column;
                if (cell >= zone.Matrix.Count || zone.Matrix[cell] == NoLed || zone.Matrix[cell] >= (uint)points.Length)
                {
                    continue;
                }

                points[(int)zone.Matrix[cell]] = new LedPoint(
                    width == 1 ? 0.5f : (float)column / (width - 1),
                    height == 1 ? 0.5f : (float)row / (height - 1));
            }
        }
    }
}
