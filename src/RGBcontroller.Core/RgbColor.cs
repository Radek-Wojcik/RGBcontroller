namespace RGBcontroller.Core;

/// <summary>8-bit sRGB color as sent to hardware.</summary>
public readonly record struct RgbColor(byte R, byte G, byte B)
{
    public static RgbColor Black => default;
}
