using System.Numerics;

namespace RGBcontroller.Core.Ambilight;

/// <summary>
/// Averages, for every strip LED, its band along the screen edge (SPEC 6.2): <c>depth</c> deep, as wide as the LED's
/// share of the edge widened by the overlap so neighbours blend. Rectangles are recomputed only when the image size or
/// the letterbox content area changes; sampling itself does not allocate.
/// </summary>
public sealed class EdgeSampler
{
    /// <summary>Pixels read per LED at most; larger bands are read with a stride.</summary>
    private const int MaxPixelsPerLed = 512;

    private readonly StripLayout _layout;
    private readonly double _depthFraction;
    private readonly double _overlap;
    private readonly Band[] _bands;
    private int _width;
    private int _height;
    private ContentRect _content;

    /// <param name="depthFraction">Band depth as a share of the screen width (edge depth cm / screen width cm).</param>
    /// <param name="overlap">0.3 widens each LED's band by 30% of its share.</param>
    public EdgeSampler(StripLayout layout, double depthFraction, double overlap)
    {
        ArgumentNullException.ThrowIfNull(layout);
        _layout = layout;
        _depthFraction = depthFraction;
        _overlap = overlap;
        _bands = new Band[layout.LedCount];
    }

    /// <summary>Band rectangles in image pixels, indexed like the strip LEDs (empty for dark LEDs).</summary>
    public ReadOnlySpan<Band> Bands => _bands;

    /// <summary>Recomputes the bands when the image size or the content area changed.</summary>
    public void Configure(int width, int height, ContentRect content)
    {
        if (width == _width && height == _height && content == _content)
        {
            return;
        }

        _width = width;
        _height = height;
        _content = content;
        var depth = Math.Max(1, (int)Math.Round(_depthFraction * width));
        for (var i = 0; i < _bands.Length; i++)
        {
            _bands[i] = _layout.Leds[i] is { } led ? BandFor(led, content, depth) : default;
        }
    }

    /// <summary>Average color per LED on the 0..255 sRGB scale; dark LEDs get black.</summary>
    /// <param name="hdrScale">scRGB value of SDR white (HDR images only).</param>
    public void Sample(in ScreenImage image, Span<Vector3> destination, float hdrScale)
    {
        if (image.Width != _width || image.Height != _height)
        {
            Configure(image.Width, image.Height, ContentRect.Full(image.Width, image.Height));
        }

        for (var i = 0; i < _bands.Length; i++)
        {
            var band = _bands[i];
            if (band.IsEmpty)
            {
                destination[i] = Vector3.Zero;
                continue;
            }

            var sum = Vector3.Zero;
            var count = 0;
            for (var y = band.Y0; y < band.Y1; y += band.Step)
            {
                sum += image.SumRow(y, band.X0, band.X1, band.Step, out var n);
                count += n;
            }

            var average = count > 0 ? sum / count : Vector3.Zero;
            destination[i] = image.Format == ScreenPixelFormat.Bgra8 ? average : ColorMath.HdrToSrgb255(average, hdrScale);
        }
    }

    private Band BandFor(StripLedPlacement led, ContentRect content, int depth)
    {
        var horizontal = led.Edge is ScreenEdge.Top or ScreenEdge.Bottom;
        var length = horizontal ? content.Width : content.Height;
        var origin = horizontal ? content.X0 : content.Y0;
        var center = origin + led.Along * length;
        var half = Math.Max(0.5, led.Extent * length * (1 + _overlap) / 2);
        var a0 = Math.Clamp((int)Math.Floor(center - half), origin, origin + length - 1);
        var a1 = Math.Clamp((int)Math.Ceiling(center + half), a0 + 1, origin + length);
        int x0, y0, x1, y1;
        switch (led.Edge)
        {
            case ScreenEdge.Top:
                (x0, x1, y0, y1) = (a0, a1, content.Y0, Math.Min(content.Y1, content.Y0 + depth));
                break;
            case ScreenEdge.Bottom:
                (x0, x1, y0, y1) = (a0, a1, Math.Max(content.Y0, content.Y1 - depth), content.Y1);
                break;
            case ScreenEdge.Left:
                (x0, x1, y0, y1) = (content.X0, Math.Min(content.X1, content.X0 + depth), a0, a1);
                break;
            default:
                (x0, x1, y0, y1) = (Math.Max(content.X0, content.X1 - depth), content.X1, a0, a1);
                break;
        }

        var area = Math.Max(1, (x1 - x0) * (y1 - y0));
        var step = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(area / (double)MaxPixelsPerLed)));
        return new Band(x0, y0, x1, y1, step);
    }

    /// <summary>A rectangle in image pixels (end exclusive) read every <see cref="Step"/> pixels.</summary>
    public readonly record struct Band(int X0, int Y0, int X1, int Y1, int Step)
    {
        public bool IsEmpty => X1 <= X0 || Y1 <= Y0;
    }
}
