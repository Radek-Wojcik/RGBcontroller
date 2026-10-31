using RGBcontroller.Core.Config;

namespace RGBcontroller.Core.Effects;

/// <summary>
/// A stateless function of LED position and time. Every group of a profile shares one clock, so devices that run
/// the same effect stay in phase. Implementations must not allocate in <see cref="Render"/> (SPEC 5.1).
/// Brightness is not applied here; the engine scales the result last.
/// </summary>
public abstract class Effect
{
    /// <summary>False when the output never changes; the engine then renders once and goes idle (0% CPU).</summary>
    public abstract bool IsAnimated { get; }

    public abstract void Render(ReadOnlySpan<LedPoint> points, Span<RgbColor> destination, double seconds);

    /// <summary>Builds the effect for one group. Unknown palettes fall back to white (the validator rejects them earlier).</summary>
    public static Effect Create(GroupEffectConfig config, IReadOnlyList<PaletteConfig> palettes)
    {
        ArgumentNullException.ThrowIfNull(config);
        var palette = Palette.Resolve(config.Palette, palettes) ?? Palette.Solid(new RgbColor(255, 255, 255));
        return config.Effect switch
        {
            EffectKind.Static => new StaticEffect(palette, config.Angle, config.Scale),
            EffectKind.Breathing => new BreathingEffect(palette, config.Angle, config.Scale, config.Speed),
            EffectKind.Wave => new WaveEffect(palette, config.Angle, config.Scale, config.Speed),
            EffectKind.Radial => new RadialEffect(palette, config.RadialMode, config.Reverse, config.Scale, config.Speed),
            EffectKind.Mirror => new MirrorEffect(palette, config.Angle, config.Reverse, config.Scale, config.Speed),
            EffectKind.ColorCycle => new ColorCycleEffect(palette, config.Speed),
            EffectKind.Aurora => new AuroraEffect(palette, config.Angle, config.Scale, config.Speed),
            _ => OffEffect.Instance,
        };
    }
}

/// <summary>Projection of a point onto a direction through the component center, -0.5..0.5 for axis-aligned angles.</summary>
internal readonly struct Direction(double degrees)
{
    private readonly double _dx = Math.Cos(degrees * Math.PI / 180);
    private readonly double _dy = Math.Sin(degrees * Math.PI / 180);

    public double Project(in LedPoint point) => (point.X - 0.5) * _dx + (point.Y - 0.5) * _dy;
}

public sealed class OffEffect : Effect
{
    public static OffEffect Instance { get; } = new();

    public override bool IsAnimated => false;

    public override void Render(ReadOnlySpan<LedPoint> points, Span<RgbColor> destination, double seconds) => destination.Clear();
}

/// <summary>One color, or the palette laid out once along <c>angle</c> (a still gradient). Sent once.</summary>
public sealed class StaticEffect(Palette palette, double angle, double scale) : Effect
{
    private readonly Direction _direction = new(angle);

    public override bool IsAnimated => false;

    public override void Render(ReadOnlySpan<LedPoint> points, Span<RgbColor> destination, double seconds)
    {
        for (var i = 0; i < points.Length; i++)
        {
            destination[i] = palette.Sample((_direction.Project(points[i]) + 0.5) * scale);
        }
    }
}

/// <summary>The static gradient fading smoothly to black and back, <c>speed</c> breaths per second.</summary>
public sealed class BreathingEffect(Palette palette, double angle, double scale, double speed) : Effect
{
    private readonly Direction _direction = new(angle);

    public override bool IsAnimated => speed > 0;

    public override void Render(ReadOnlySpan<LedPoint> points, Span<RgbColor> destination, double seconds)
    {
        var level = 0.5 - 0.5 * Math.Cos(2 * Math.PI * seconds * speed);
        var factor = (int)Math.Round(level * level * 255); // squared: the eye sees the dim half of the cycle longer
        for (var i = 0; i < points.Length; i++)
        {
            destination[i] = Scale(palette.Sample((_direction.Project(points[i]) + 0.5) * scale), factor);
        }
    }

    internal static RgbColor Scale(RgbColor color, int factor) =>
        new((byte)(color.R * factor / 255), (byte)(color.G * factor / 255), (byte)(color.B * factor / 255));
}

/// <summary>Linear gradient travelling towards <c>angle</c> (0 = to the right, 180 = to the left).</summary>
public sealed class WaveEffect(Palette palette, double angle, double scale, double speed) : Effect
{
    private readonly Direction _direction = new(angle);

    public override bool IsAnimated => speed > 0 && !palette.IsSolid;

    public override void Render(ReadOnlySpan<LedPoint> points, Span<RgbColor> destination, double seconds)
    {
        var shift = seconds * speed;
        for (var i = 0; i < points.Length; i++)
        {
            destination[i] = palette.Sample((_direction.Project(points[i]) + 0.5) * scale - shift);
        }
    }
}

/// <summary>Gradient by angle around the component center, rotating clockwise (Rotate), or by distance, moving outward (Expand).</summary>
public sealed class RadialEffect(Palette palette, RadialMode mode, bool reverse, double scale, double speed) : Effect
{
    private readonly double _signedScale = reverse ? -scale : scale;

    public override bool IsAnimated => speed > 0 && !palette.IsSolid;

    public override void Render(ReadOnlySpan<LedPoint> points, Span<RgbColor> destination, double seconds)
    {
        var shift = seconds * speed;
        var byAngle = mode == RadialMode.Rotate;
        for (var i = 0; i < points.Length; i++)
        {
            var coordinate = byAngle ? points[i].Angle : points[i].Radius;
            destination[i] = palette.Sample(coordinate * _signedScale - shift);
        }
    }
}

/// <summary>Gradient by distance from the center line across <c>angle</c>, moving outward symmetrically (RAM: up and down from the middle).</summary>
public sealed class MirrorEffect(Palette palette, double angle, bool reverse, double scale, double speed) : Effect
{
    private readonly Direction _direction = new(angle);
    private readonly double _signedScale = reverse ? -scale : scale;

    public override bool IsAnimated => speed > 0 && !palette.IsSolid;

    public override void Render(ReadOnlySpan<LedPoint> points, Span<RgbColor> destination, double seconds)
    {
        var shift = seconds * speed;
        for (var i = 0; i < points.Length; i++)
        {
            var distance = Math.Abs(_direction.Project(points[i])) * 2;
            destination[i] = palette.Sample(distance * _signedScale - shift);
        }
    }
}

/// <summary>The whole component shows one color that moves through the palette.</summary>
public sealed class ColorCycleEffect(Palette palette, double speed) : Effect
{
    public override bool IsAnimated => speed > 0 && !palette.IsSolid;

    public override void Render(ReadOnlySpan<LedPoint> points, Span<RgbColor> destination, double seconds) =>
        destination[..points.Length].Fill(palette.Sample(seconds * speed));
}

/// <summary>
/// Slow flowing gradient with soft brightness swells, like the northern lights. Cheap: a few sines per LED instead
/// of a noise texture, so the pattern stays smooth at low frame rates and never repeats visibly.
/// </summary>
public sealed class AuroraEffect(Palette palette, double angle, double scale, double speed) : Effect
{
    private readonly Direction _direction = new(angle);

    public override bool IsAnimated => speed > 0;

    public override void Render(ReadOnlySpan<LedPoint> points, Span<RgbColor> destination, double seconds)
    {
        var t = seconds * speed;
        const double Tau = 2 * Math.PI;
        for (var i = 0; i < points.Length; i++)
        {
            var p = points[i];
            var along = _direction.Project(p) + 0.5;
            var drift = 0.12 * Math.Sin(Tau * (p.X * 0.9 + p.Y * 0.4 + t * 0.7))
                + 0.08 * Math.Sin(Tau * (p.Y * 1.3 - t * 0.45));
            var swell = 0.5 + 0.25 * Math.Sin(Tau * (along * 1.1 - t * 1.3)) + 0.25 * Math.Sin(Tau * (p.Y * 0.7 + p.X * 0.5 + t * 0.9));
            var level = 0.35 + 0.65 * swell;
            destination[i] = BreathingEffect.Scale(palette.Sample(along * scale * 0.5 + drift - t), (int)(level * 255));
        }
    }
}
