namespace RGBcontroller.Core.Effects;

/// <summary>
/// Position of one LED inside its component (keyboard, RAM stick, fan ring), normalized to 0..1 with y pointing
/// down. <see cref="Angle"/> and <see cref="Radius"/> are precomputed for radial effects.
/// </summary>
public readonly record struct LedPoint
{
    public LedPoint(float x, float y)
    {
        X = x;
        Y = y;
        var dx = x - 0.5;
        var dy = y - 0.5;
        var angle = Math.Atan2(dx, -dy) / (2 * Math.PI);
        Angle = (float)(angle < 0 ? angle + 1 : angle);
        Radius = (float)(Math.Sqrt(dx * dx + dy * dy) * 2);
    }

    public static LedPoint Center { get; } = new(0.5f, 0.5f);

    public float X { get; }

    public float Y { get; }

    /// <summary>0..1 around the center, 0 at 12 o'clock, increasing clockwise.</summary>
    public float Angle { get; }

    /// <summary>Distance from the center; 1 on the inscribed circle (ring LEDs).</summary>
    public float Radius { get; }
}
