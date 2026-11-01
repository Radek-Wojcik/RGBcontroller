using RGBcontroller.Core.Config;

namespace RGBcontroller.Core.Effects;

/// <summary>
/// Cyclic gradient baked into a lookup table, so sampling in the render loop is one multiply and one array read.
/// The last stop blends back into the first, which makes every palette seamless when it scrolls.
/// </summary>
public sealed class Palette
{
    public const int Resolution = 1024;

    private readonly RgbColor[] _lut = new RgbColor[Resolution];

    /// <param name="stops">Positions 0..1 with colors; at least one. Order does not matter.</param>
    public Palette(IReadOnlyList<(double Position, RgbColor Color)> stops)
    {
        ArgumentNullException.ThrowIfNull(stops);
        if (stops.Count == 0)
        {
            throw new ArgumentException("A palette needs at least one stop.", nameof(stops));
        }

        var sorted = stops.OrderBy(s => s.Position).ToArray();
        IsSolid = sorted.All(s => s.Color == sorted[0].Color);
        for (var i = 0; i < Resolution; i++)
        {
            _lut[i] = Interpolate(sorted, (double)i / Resolution);
        }
    }

    /// <summary>All stops have the same color; effects that only change the hue are then static.</summary>
    public bool IsSolid { get; }

    public static Palette Solid(RgbColor color) => new([(0, color)]);

    /// <summary>Resolves a profile reference: "#RRGGBB" or a palette name from <paramref name="library"/>.</summary>
    /// <returns>Null when the reference matches nothing (the validator rejects that for saved configs).</returns>
    public static Palette? Resolve(string reference, IReadOnlyList<PaletteConfig> library)
    {
        ArgumentNullException.ThrowIfNull(library);
        if (RgbColor.TryParseHex(reference, out var color))
        {
            return Solid(color);
        }

        var config = library.FirstOrDefault(p => string.Equals(p.Name, reference, StringComparison.OrdinalIgnoreCase));
        if (config is null || config.Stops.Count == 0)
        {
            return null;
        }

        var stops = new List<(double, RgbColor)>(config.Stops.Count);
        foreach (var stop in config.Stops)
        {
            if (!RgbColor.TryParseHex(stop.Color, out var stopColor))
            {
                return null;
            }

            stops.Add((stop.Position, stopColor));
        }

        return new Palette(stops);
    }

    /// <summary>Color at <paramref name="t"/>; only the fractional part matters (1.25 samples like 0.25).</summary>
    public RgbColor Sample(double t)
    {
        var index = (int)((t - Math.Floor(t)) * Resolution);
        return _lut[index < Resolution ? index : Resolution - 1];
    }

    private static RgbColor Interpolate((double Position, RgbColor Color)[] stops, double x)
    {
        // Find the stop pair around x, treating the first stop as repeated at position + 1.
        var last = stops[^1];
        var first = stops[0];
        if (x < first.Position)
        {
            return Lerp(last.Color, first.Color, Fraction(last.Position - 1, first.Position, x));
        }

        for (var i = 0; i < stops.Length - 1; i++)
        {
            if (x < stops[i + 1].Position)
            {
                return Lerp(stops[i].Color, stops[i + 1].Color, Fraction(stops[i].Position, stops[i + 1].Position, x));
            }
        }

        return Lerp(last.Color, first.Color, Fraction(last.Position, first.Position + 1, x));
    }

    private static double Fraction(double from, double to, double x) => to - from <= 0 ? 0 : (x - from) / (to - from);

    private static RgbColor Lerp(RgbColor a, RgbColor b, double f) => new(
        (byte)Math.Round(a.R + (b.R - a.R) * f),
        (byte)Math.Round(a.G + (b.G - a.G) * f),
        (byte)Math.Round(a.B + (b.B - a.B) * f));
}
