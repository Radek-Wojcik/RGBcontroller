using RGBcontroller.Core.Config;

namespace RGBcontroller.Core.Strip;

/// <summary>
/// Builds Adalight frames into one pre-allocated buffer: 'A' 'd' 'a' hi lo (hi^lo^0x55) then 3 bytes per LED.
/// Processing order (SPEC 6.2): brightness, then the current limiter, then color order. Never allocates after construction.
/// </summary>
public sealed class AdalightFrame
{
    public const int HeaderSize = 6;

    /// <summary>Measured 19.5 mA per LED at full white, i.e. 6.5 mA per fully lit channel.</summary>
    public const double MilliampsPerChannel = 6.5;

    private readonly byte[] _buffer;
    private readonly ColorOrder _order;
    private readonly int _currentLimitMilliamps;

    public AdalightFrame(int ledCount, ColorOrder order, int currentLimitMilliamps)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(ledCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ledCount, 65536);
        LedCount = ledCount;
        _order = order;
        _currentLimitMilliamps = currentLimitMilliamps;
        _buffer = new byte[FrameSize(ledCount)];
        var n = ledCount - 1;
        _buffer[0] = (byte)'A';
        _buffer[1] = (byte)'d';
        _buffer[2] = (byte)'a';
        _buffer[3] = (byte)(n >> 8);
        _buffer[4] = (byte)n;
        _buffer[5] = (byte)(_buffer[3] ^ _buffer[4] ^ 0x55);
    }

    public int LedCount { get; }

    public ReadOnlySpan<byte> Bytes => _buffer;

    /// <summary>Estimated strip current of the last rendered frame, after limiting.</summary>
    public double EstimatedMilliamps { get; private set; }

    /// <summary>True when the last frame had to be dimmed by the current limiter.</summary>
    public bool WasLimited { get; private set; }

    public static int FrameSize(int ledCount) => HeaderSize + 3 * ledCount;

    /// <summary>Frames per second the serial link can carry (8N1 = 10 bits per byte).</summary>
    public static double MaxFramesPerSecond(int baudRate, int ledCount) => baudRate / 10.0 / FrameSize(ledCount);

    /// <param name="colors">Exactly <see cref="LedCount"/> colors; missing LEDs are black, extra ones ignored.</param>
    /// <param name="brightnessPercent">0-100; 100 = full diode power.</param>
    public void Render(ReadOnlySpan<RgbColor> colors, int brightnessPercent)
    {
        var scale = Math.Clamp(brightnessPercent, 0, 100);
        long sum = 0;
        var payload = _buffer.AsSpan(HeaderSize);
        for (var i = 0; i < LedCount; i++)
        {
            var c = i < colors.Length ? colors[i] : default;
            var r = c.R * scale / 100;
            var g = c.G * scale / 100;
            var b = c.B * scale / 100;
            sum += r + g + b;
            Write(payload.Slice(i * 3, 3), r, g, b);
        }

        var estimate = sum * MilliampsPerChannel / 255.0;
        WasLimited = estimate > _currentLimitMilliamps;
        if (WasLimited)
        {
            // Dim uniformly so the estimate lands at or below the limit; color order does not matter for scaling.
            var factor = _currentLimitMilliamps / estimate;
            sum = 0;
            for (var i = 0; i < payload.Length; i++)
            {
                payload[i] = (byte)(payload[i] * factor);
                sum += payload[i];
            }

            estimate = sum * MilliampsPerChannel / 255.0;
        }

        EstimatedMilliamps = estimate;
    }

    private void Write(Span<byte> led, int r, int g, int b)
    {
        switch (_order)
        {
            case ColorOrder.Rgb: led[0] = (byte)r; led[1] = (byte)g; led[2] = (byte)b; break;
            case ColorOrder.Rbg: led[0] = (byte)r; led[1] = (byte)b; led[2] = (byte)g; break;
            case ColorOrder.Grb: led[0] = (byte)g; led[1] = (byte)r; led[2] = (byte)b; break;
            case ColorOrder.Gbr: led[0] = (byte)g; led[1] = (byte)b; led[2] = (byte)r; break;
            case ColorOrder.Brg: led[0] = (byte)b; led[1] = (byte)r; led[2] = (byte)g; break;
            default: led[0] = (byte)b; led[1] = (byte)g; led[2] = (byte)r; break;
        }
    }
}
