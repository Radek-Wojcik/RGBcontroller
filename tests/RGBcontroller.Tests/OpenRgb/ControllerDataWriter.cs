using System.Buffers.Binary;
using System.Text;
using RGBcontroller.Core.OpenRgb;

namespace RGBcontroller.Tests.OpenRgb;

/// <summary>
/// Test-side serializer mirroring RGBController::GetDeviceDescription of OpenRGB 1.0rc2, written
/// independently of the reader so the two check each other. Field order per docs/spikes/R2-R3-openrgb-sdk.md.
/// </summary>
internal static class ControllerDataWriter
{
    public static byte[] Write(ControllerData c, uint protocolVersion)
    {
        var body = new List<byte>();
        I32(body, c.Type);
        Str(body, c.Name);
        Str(body, c.Vendor);
        Str(body, c.Description);
        Str(body, c.Version);
        Str(body, c.Serial);
        Str(body, c.Location);
        U16(body, c.Modes.Count);
        I32(body, c.ActiveMode);
        foreach (var m in c.Modes)
        {
            Str(body, m.Name);
            I32(body, m.Value);
            U32(body, m.Flags);
            U32(body, m.SpeedMin);
            U32(body, m.SpeedMax);
            U32(body, m.BrightnessMin);
            U32(body, m.BrightnessMax);
            U32(body, m.ColorsMin);
            U32(body, m.ColorsMax);
            U32(body, m.Speed);
            U32(body, m.Brightness);
            U32(body, m.Direction);
            U32(body, m.ColorMode);
            U16(body, m.Colors.Count);
            m.Colors.ToList().ForEach(x => U32(body, x));
        }

        U16(body, c.Zones.Count);
        foreach (var z in c.Zones)
        {
            Str(body, z.Name);
            I32(body, z.Type);
            U32(body, z.LedsMin);
            U32(body, z.LedsMax);
            U32(body, z.LedsCount);
            if (z.Matrix.Count == 0)
            {
                U16(body, 0);
            }
            else
            {
                U16(body, 8 + z.Matrix.Count * 4);
                U32(body, z.MatrixHeight);
                U32(body, z.MatrixWidth);
                z.Matrix.ToList().ForEach(x => U32(body, x));
            }

            U16(body, z.Segments.Count);
            foreach (var s in z.Segments)
            {
                Str(body, s.Name);
                I32(body, s.Type);
                U32(body, s.StartIndex);
                U32(body, s.LedsCount);
            }

            if (protocolVersion >= 5)
            {
                U32(body, z.Flags);
            }
        }

        U16(body, c.Leds.Count);
        foreach (var led in c.Leds)
        {
            Str(body, led.Name);
            U32(body, led.Value);
        }

        U16(body, c.ColorCount);
        for (var i = 0; i < c.ColorCount; i++)
        {
            U32(body, 0);
        }

        if (protocolVersion >= 5)
        {
            U16(body, c.LedAlternateNames.Count);
            c.LedAlternateNames.ToList().ForEach(n => Str(body, n));
            U32(body, c.Flags);
        }

        var result = new List<byte>();
        U32(result, (uint)(body.Count + 4));
        result.AddRange(body);
        return [.. result];
    }

    public static ControllerData Keyboard(uint protocolVersion = 5) => new(
        Type: 5,
        Name: "Razer Ornata Chroma V2",
        Vendor: "Razer",
        Description: "Razer Keyboard Device",
        Version: "",
        Serial: "",
        Location: "HID: \\\\?\\hid#vid_1532&pid_025d",
        ActiveMode: 0,
        Modes:
        [
            new("Direct", 0, 0x20, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, []),
            new("Static", 6, 0x80, 0, 0, 0, 100, 1, 1, 0, 100, 0, 2, [0x0000FF]),
        ],
        Zones:
        [
            new("Keyboard", 2, 126, 126, 126, 6, 21, Enumerable.Range(0, 126).Select(i => (uint)i).ToArray(),
                [new("Main", 1, 0, 100)], protocolVersion >= 5 ? 4u : 0u),
        ],
        Leds: Enumerable.Range(0, 126).Select(i => new ControllerLed($"Key: {i}", (uint)i)).ToArray(),
        ColorCount: 126,
        LedAlternateNames: protocolVersion >= 5 ? ["Esc", "F1"] : [],
        Flags: protocolVersion >= 5 ? 1u : 0u);

    private static void Str(List<byte> buffer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value + '\0');
        U16(buffer, bytes.Length);
        buffer.AddRange(bytes);
    }

    private static void U16(List<byte> buffer, int value)
    {
        Span<byte> tmp = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(tmp, checked((ushort)value));
        buffer.AddRange(tmp);
    }

    private static void U32(List<byte> buffer, uint value)
    {
        Span<byte> tmp = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(tmp, value);
        buffer.AddRange(tmp);
    }

    private static void I32(List<byte> buffer, int value) => U32(buffer, unchecked((uint)value));
}
