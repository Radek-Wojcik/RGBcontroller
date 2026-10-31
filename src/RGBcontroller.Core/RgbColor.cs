using System.Globalization;

namespace RGBcontroller.Core;

/// <summary>8-bit sRGB color as sent to hardware.</summary>
public readonly record struct RgbColor(byte R, byte G, byte B)
{
    public static RgbColor Black => default;

    /// <summary>Parses "#RRGGBB".</summary>
    public static bool TryParseHex(string? text, out RgbColor color)
    {
        color = default;
        if (text is not { Length: 7 } || text[0] != '#'
            || !uint.TryParse(text.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        color = new RgbColor((byte)(value >> 16), (byte)(value >> 8), (byte)value);
        return true;
    }

    /// <summary>Scales by a 0-100 percentage; 100 keeps the color unchanged (full diode power).</summary>
    public RgbColor Scale(int percent)
    {
        var p = Math.Clamp(percent, 0, 100);
        return new RgbColor((byte)(R * p / 100), (byte)(G * p / 100), (byte)(B * p / 100));
    }

    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";
}
