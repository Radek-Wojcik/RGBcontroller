using System.Numerics;
using System.Runtime.InteropServices;

namespace RGBcontroller.Core.Ambilight;

public enum ScreenPixelFormat
{
    /// <summary>8-bit sRGB desktop (SDR), DXGI_FORMAT_B8G8R8A8_UNORM.</summary>
    Bgra8,

    /// <summary>scRGB linear half floats (HDR desktop), DXGI_FORMAT_R16G16B16A16_FLOAT; 1.0 = 80 nits.</summary>
    Rgba16Float,
}

/// <summary>A downscaled desktop frame mapped from the GPU. Valid only while the Agent holds the mapping.</summary>
public readonly ref struct ScreenImage
{
    private readonly ReadOnlySpan<byte> _data;

    public ScreenImage(ReadOnlySpan<byte> data, int width, int height, int rowPitch, ScreenPixelFormat format)
    {
        var bytesPerPixel = format == ScreenPixelFormat.Bgra8 ? 4 : 8;
        if (width <= 0 || height <= 0 || rowPitch < width * bytesPerPixel || data.Length < rowPitch * (height - 1) + width * bytesPerPixel)
        {
            throw new ArgumentException("Image size, pitch and data length do not match.");
        }

        _data = data;
        Width = width;
        Height = height;
        RowPitch = rowPitch;
        Format = format;
    }

    public int Width { get; }

    public int Height { get; }

    public int RowPitch { get; }

    public ScreenPixelFormat Format { get; }

    /// <summary>
    /// The pixel in the image's own encoding: sRGB 0..255 for <see cref="ScreenPixelFormat.Bgra8"/>, linear scRGB
    /// (negative values clamped) for <see cref="ScreenPixelFormat.Rgba16Float"/>.
    /// </summary>
    public Vector3 Raw(int x, int y)
    {
        if (Format == ScreenPixelFormat.Bgra8)
        {
            var p = _data.Slice(y * RowPitch + x * 4, 4);
            return new Vector3(p[2], p[1], p[0]);
        }

        var h = MemoryMarshal.Cast<byte, Half>(_data.Slice(y * RowPitch + x * 8, 8));
        return Vector3.Max(new Vector3((float)h[0], (float)h[1], (float)h[2]), Vector3.Zero);
    }

    /// <summary>Sum of the pixels of one row from <paramref name="x0"/> (inclusive) to <paramref name="x1"/> (exclusive), stepping by <paramref name="step"/>.</summary>
    public Vector3 SumRow(int y, int x0, int x1, int step, out int count)
    {
        var sum = Vector3.Zero;
        count = 0;
        if (Format == ScreenPixelFormat.Bgra8)
        {
            var row = _data.Slice(y * RowPitch, Width * 4);
            for (var x = x0; x < x1; x += step)
            {
                var o = x * 4;
                sum += new Vector3(row[o + 2], row[o + 1], row[o]);
                count++;
            }

            return sum;
        }

        var halves = MemoryMarshal.Cast<byte, Half>(_data.Slice(y * RowPitch, Width * 8));
        for (var x = x0; x < x1; x += step)
        {
            var o = x * 4;
            sum += Vector3.Max(new Vector3((float)halves[o], (float)halves[o + 1], (float)halves[o + 2]), Vector3.Zero);
            count++;
        }

        return sum;
    }
}

/// <summary>The picture inside letterbox or pillarbox bars, in image pixels (end exclusive).</summary>
public readonly record struct ContentRect(int X0, int Y0, int X1, int Y1)
{
    public static ContentRect Full(int width, int height) => new(0, 0, width, height);

    public int Width => X1 - X0;

    public int Height => Y1 - Y0;
}

/// <summary>Conversions between the capture encodings and the 0..255 sRGB scale the rest of the pipeline uses.</summary>
public static class ColorMath
{
    /// <summary>scRGB value of SDR white for a given "SDR content brightness" (scRGB 1.0 = 80 nits).</summary>
    public static float HdrScale(double sdrWhiteNits) => (float)Math.Max(1, sdrWhiteNits / 80.0);

    public static float LinearToSrgb(float linear)
    {
        if (linear <= 0.0031308f)
        {
            return Math.Max(0, linear * 12.92f);
        }

        return 1.055f * MathF.Pow(linear, 1 / 2.4f) - 0.055f;
    }

    public static float SrgbToLinear(float srgb) =>
        srgb <= 0.04045f ? srgb / 12.92f : MathF.Pow((srgb + 0.055f) / 1.055f, 2.4f);

    /// <summary>
    /// HDR highlights above SDR white are compressed into the top of the SDR range (a soft shoulder from 0.8 that
    /// approaches 1.0), so bright HDR content saturates gracefully instead of clipping every channel at once.
    /// </summary>
    public static float ToneMap(float sdrRelative)
    {
        const float Knee = 0.8f;
        if (sdrRelative <= Knee)
        {
            return sdrRelative;
        }

        return Knee + (1 - Knee) * (1 - MathF.Exp(-(sdrRelative - Knee) / (1 - Knee)));
    }

    /// <summary>A linear scRGB color (HDR capture) on the 0..255 sRGB scale.</summary>
    public static Vector3 HdrToSrgb255(Vector3 linear, float hdrScale)
    {
        var v = linear / hdrScale;
        return new Vector3(
            LinearToSrgb(ToneMap(v.X)) * 255,
            LinearToSrgb(ToneMap(v.Y)) * 255,
            LinearToSrgb(ToneMap(v.Z)) * 255);
    }
}
