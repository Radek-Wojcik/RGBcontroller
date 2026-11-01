using System.Buffers.Binary;

namespace RGBcontroller.Ipc;

public enum FrameType : byte
{
    /// <summary>Agent to Service: strip colors, payload = LED count x 3 bytes (R, G, B).</summary>
    StripColors = 1,

    /// <summary>Service to UI: live preview of all device LEDs.</summary>
    Preview = 2,

    /// <summary>Agent to Service: the screen cannot be captured, show the fallback mode until colors arrive again. No payload.</summary>
    CaptureUnavailable = 3,
}

public readonly record struct FrameHeader(FrameType Type, uint Sequence, int PayloadLength);

/// <summary>
/// Binary framing for <see cref="IpcProtocol.FramePipeName"/>. Little-endian, 16-byte header:
/// magic "RGBF" | version u8 | type u8 | reserved u16 | sequence u32 | payload length u32.
/// Encoding and decoding work on caller-provided spans and never allocate.
/// </summary>
public static class FrameCodec
{
    public const int HeaderSize = 16;
    public const byte Version = 1;
    public const int MaxPayloadLength = 64 * 1024;

    /// <summary>"RGBF" read as a little-endian u32.</summary>
    public const uint Magic = 'R' | ('G' << 8) | ('B' << 16) | ((uint)'F' << 24);

    public static void WriteHeader(Span<byte> destination, in FrameHeader header)
    {
        if (destination.Length < HeaderSize)
        {
            throw new ArgumentException($"Need {HeaderSize} bytes.", nameof(destination));
        }

        if ((uint)header.PayloadLength > MaxPayloadLength)
        {
            throw new ArgumentOutOfRangeException(nameof(header), header.PayloadLength, $"Payload must be 0..{MaxPayloadLength} bytes.");
        }

        BinaryPrimitives.WriteUInt32LittleEndian(destination, Magic);
        destination[4] = Version;
        destination[5] = (byte)header.Type;
        BinaryPrimitives.WriteUInt16LittleEndian(destination[6..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], header.Sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[12..], (uint)header.PayloadLength);
    }

    /// <summary>Validates and parses a header. Returns false for anything malformed (the caller drops the connection).</summary>
    public static bool TryReadHeader(ReadOnlySpan<byte> source, out FrameHeader header)
    {
        header = default;
        if (source.Length < HeaderSize
            || BinaryPrimitives.ReadUInt32LittleEndian(source) != Magic
            || source[4] != Version)
        {
            return false;
        }

        var type = (FrameType)source[5];
        if (type is not (FrameType.StripColors or FrameType.Preview or FrameType.CaptureUnavailable))
        {
            return false;
        }

        var length = BinaryPrimitives.ReadUInt32LittleEndian(source[12..]);
        if (length > MaxPayloadLength)
        {
            return false;
        }

        header = new FrameHeader(type, BinaryPrimitives.ReadUInt32LittleEndian(source[8..]), (int)length);
        return true;
    }
}
