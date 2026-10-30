using System.Buffers.Binary;
using System.Text;

namespace RGBcontroller.Core.OpenRgb;

/// <summary>
/// Parses the controller description block (RGBController::GetDeviceDescription) for protocol 4 and 5.
/// The block must be consumed exactly; any mismatch means our layout is wrong and is reported as an error.
/// </summary>
public static class ControllerDataReader
{
    public static ControllerData Parse(ReadOnlySpan<byte> payload, uint protocolVersion)
    {
        if (protocolVersion is < OpenRgbProtocol.MinProtocolVersion or > OpenRgbProtocol.MaxProtocolVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(protocolVersion), protocolVersion, null);
        }

        var reader = new Reader(payload);
        var dataSize = reader.U32();
        if (dataSize != payload.Length)
        {
            throw new OpenRgbProtocolException($"Controller data size field {dataSize} does not match payload length {payload.Length}.");
        }

        var type = reader.I32();
        var name = reader.String();
        var vendor = reader.String();
        var description = reader.String();
        var version = reader.String();
        var serial = reader.String();
        var location = reader.String();

        var modeCount = reader.U16();
        var activeMode = reader.I32();
        var modes = new ControllerMode[modeCount];
        for (var i = 0; i < modes.Length; i++)
        {
            modes[i] = ReadMode(ref reader);
        }

        var zones = new ControllerZone[reader.U16()];
        for (var i = 0; i < zones.Length; i++)
        {
            zones[i] = ReadZone(ref reader, protocolVersion);
        }

        var leds = new ControllerLed[reader.U16()];
        for (var i = 0; i < leds.Length; i++)
        {
            leds[i] = new ControllerLed(reader.String(), reader.U32());
        }

        var colorCount = reader.U16();
        reader.Skip(colorCount * 4);

        string[] altNames = [];
        uint flags = 0;
        if (protocolVersion >= 5)
        {
            altNames = new string[reader.U16()];
            for (var i = 0; i < altNames.Length; i++)
            {
                altNames[i] = reader.String();
            }

            flags = reader.U32();
        }

        if (reader.Remaining != 0)
        {
            throw new OpenRgbProtocolException($"Controller data has {reader.Remaining} unparsed trailing bytes.");
        }

        return new ControllerData(type, name, vendor, description, version, serial, location,
            activeMode, modes, zones, leds, colorCount, altNames, flags);
    }

    private static ControllerMode ReadMode(ref Reader reader)
    {
        var name = reader.String();
        var value = reader.I32();
        var flags = reader.U32();
        var speedMin = reader.U32();
        var speedMax = reader.U32();
        var brightnessMin = reader.U32();
        var brightnessMax = reader.U32();
        var colorsMin = reader.U32();
        var colorsMax = reader.U32();
        var speed = reader.U32();
        var brightness = reader.U32();
        var direction = reader.U32();
        var colorMode = reader.U32();
        var colors = new uint[reader.U16()];
        for (var i = 0; i < colors.Length; i++)
        {
            colors[i] = reader.U32();
        }

        return new ControllerMode(name, value, flags, speedMin, speedMax, brightnessMin, brightnessMax,
            colorsMin, colorsMax, speed, brightness, direction, colorMode, colors);
    }

    private static ControllerZone ReadZone(ref Reader reader, uint protocolVersion)
    {
        var name = reader.String();
        var type = reader.I32();
        var ledsMin = reader.U32();
        var ledsMax = reader.U32();
        var ledsCount = reader.U32();

        uint height = 0, width = 0;
        uint[] matrix = [];
        var matrixLength = reader.U16();
        if (matrixLength > 0)
        {
            height = reader.U32();
            width = reader.U32();
            if (matrixLength != 8 + (long)height * width * 4)
            {
                throw new OpenRgbProtocolException($"Zone '{name}' matrix length {matrixLength} does not match {height}x{width}.");
            }

            matrix = new uint[height * width];
            for (var i = 0; i < matrix.Length; i++)
            {
                matrix[i] = reader.U32();
            }
        }

        var segments = new ControllerSegment[reader.U16()];
        for (var i = 0; i < segments.Length; i++)
        {
            segments[i] = new ControllerSegment(reader.String(), reader.I32(), reader.U32(), reader.U32());
        }

        var flags = protocolVersion >= 5 ? reader.U32() : 0;
        return new ControllerZone(name, type, ledsMin, ledsMax, ledsCount, height, width, matrix, segments, flags);
    }

    private ref struct Reader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _offset;

        public readonly int Remaining => _data.Length - _offset;

        public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));

        public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

        public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));

        public void Skip(int count) => Take(count);

        /// <summary>u16 length (including the NUL terminator) followed by the bytes.</summary>
        public string String()
        {
            var length = U16();
            var bytes = Take(length);
            if (length > 0 && bytes[^1] == 0)
            {
                bytes = bytes[..^1];
            }

            return Encoding.UTF8.GetString(bytes);
        }

        private ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || count > Remaining)
            {
                throw new OpenRgbProtocolException($"Controller data truncated at offset {_offset} (needed {count} bytes, {Remaining} left).");
            }

            var slice = _data.Slice(_offset, count);
            _offset += count;
            return slice;
        }
    }
}
