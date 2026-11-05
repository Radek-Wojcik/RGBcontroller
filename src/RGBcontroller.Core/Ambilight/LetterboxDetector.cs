namespace RGBcontroller.Core.Ambilight;

/// <summary>
/// Finds black bars (letterbox above and below, pillarbox at the sides) on the downscaled frame so the sampled bands
/// move into the picture (SPEC 6.2). Cheap: a few sparse rows and columns per frame. Bars are taken symmetric (the
/// smaller of both sides). A row or column belongs to a bar when at least <see cref="BarShare"/> of its samples are
/// black, so things drawn on a bar do not count as picture: subtitles, a channel logo in the bottom bar, the video
/// title and the progress bar of a fullscreen player over a pillarbox (stage 4 gate: a 4:3 film kept the sides dark,
/// a 2.39:1 clip with a logo in its bottom bar kept the top dark). A new result must hold for the stable time before
/// it is used (hysteresis). A nearly black frame says nothing about bars and keeps the current result.
/// </summary>
public sealed class LetterboxDetector(TimeSpan stableTime)
{
    /// <summary>0..255 sRGB: a pixel whose brightest channel is at or below this counts as bar black.</summary>
    public const float BarBlack = 20;

    /// <summary>Bars thinner than this share of the height (width) are ignored.</summary>
    private const double MinimumBar = 0.02;

    /// <summary>Bars never cover more than this share of the height (width) per side.</summary>
    private const double MaximumBar = 0.3;

    /// <summary>Share of black samples that makes a row or column part of a bar.</summary>
    public const double BarShare = 0.75;

    private static readonly float LinearBarBlack = ColorMath.SrgbToLinear(BarBlack / 255);

    private ContentRect _candidate;
    private TimeSpan _candidateSince;

    public ContentRect Current { get; private set; }

    public void Reset() => Current = _candidate = default;

    /// <returns>The content area to sample: the picture inside the bars, or the full image.</returns>
    public ContentRect Update(in ScreenImage image, float hdrScale, TimeSpan now)
    {
        var w = image.Width;
        var h = image.Height;
        if (Current.Width == 0 || Current.X1 > w || Current.Y1 > h)
        {
            Current = _candidate = ContentRect.Full(w, h);
        }

        var threshold = image.Format == ScreenPixelFormat.Bgra8 ? BarBlack : LinearBarBlack * hdrScale;
        var maxRows = (int)(h * MaximumBar);
        var maxColumns = (int)(w * MaximumBar);
        var top = CountDarkRows(image, 0, 1, maxRows, threshold);
        if (top == maxRows && CountDarkRows(image, h / 2, 1, 1, threshold) == 1 && IsDarkColumn(image, w / 2, threshold))
        {
            // Black across the middle too: a dark scene, not bars.
            return Current;
        }

        var bottom = CountDarkRows(image, h - 1, -1, maxRows, threshold);
        var left = CountDarkColumns(image, 0, 1, maxColumns, threshold);
        var right = CountDarkColumns(image, w - 1, -1, maxColumns, threshold);
        var vertical = Bar(Math.Min(top, bottom), h);
        var horizontal = Bar(Math.Min(left, right), w);
        var detected = new ContentRect(horizontal, vertical, w - horizontal, h - vertical);

        if (detected == Current)
        {
            _candidate = detected;
            return Current;
        }

        if (detected != _candidate)
        {
            _candidate = detected;
            _candidateSince = now;
        }
        else if (now - _candidateSince >= stableTime)
        {
            Current = detected;
        }

        return Current;
    }

    /// <summary>Bars are rounded down to even pixels and dropped when too thin.</summary>
    private static int Bar(int pixels, int size) => pixels < size * MinimumBar ? 0 : pixels & ~1;

    private static int CountDarkRows(in ScreenImage image, int start, int direction, int max, float threshold)
    {
        for (var i = 0; i < max; i++)
        {
            if (!IsDarkRow(image, start + i * direction, threshold))
            {
                return i;
            }
        }

        return max;
    }

    private static int CountDarkColumns(in ScreenImage image, int start, int direction, int max, float threshold)
    {
        for (var i = 0; i < max; i++)
        {
            if (!IsDarkColumn(image, start + i * direction, threshold))
            {
                return i;
            }
        }

        return max;
    }

    private static bool IsDarkRow(in ScreenImage image, int y, float threshold)
    {
        int samples = 0, dark = 0;
        for (var x = 0; x < image.Width; x += 2, samples++)
        {
            if (Max(image.Raw(x, y)) <= threshold)
            {
                dark++;
            }
        }

        return dark >= samples * BarShare;
    }

    private static bool IsDarkColumn(in ScreenImage image, int x, float threshold)
    {
        int samples = 0, dark = 0;
        for (var y = 0; y < image.Height; y += 2, samples++)
        {
            if (Max(image.Raw(x, y)) <= threshold)
            {
                dark++;
            }
        }

        return dark >= samples * BarShare;
    }

    private static float Max(System.Numerics.Vector3 c) => Math.Max(c.X, Math.Max(c.Y, c.Z));
}
