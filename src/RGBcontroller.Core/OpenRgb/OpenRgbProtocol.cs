namespace RGBcontroller.Core.OpenRgb;

/// <summary>
/// OpenRGB SDK packet ids as defined in NetworkProtocol.h of OpenRGB 1.0rc2 (commit 0fca93e).
/// See docs/spikes/R2-R3-openrgb-sdk.md.
/// </summary>
public enum OpenRgbPacketId : uint
{
    RequestControllerCount = 0,
    RequestControllerData = 1,
    RequestProtocolVersion = 40,
    SetClientName = 50,
    DeviceListUpdated = 100,
    RequestRescanDevices = 140,
    ResizeZone = 1000,
    UpdateLeds = 1050,
    UpdateZoneLeds = 1051,
    SetCustomMode = 1100,
}

public static class OpenRgbProtocol
{
    /// <summary>"ORGB" magic + dev_idx u32 + packet id u32 + payload size u32, little-endian.</summary>
    public const int HeaderSize = 16;

    /// <summary>Highest SDK protocol this client speaks (OpenRGB 1.0 = 5).</summary>
    public const uint MaxProtocolVersion = 5;

    /// <summary>Lowest protocol accepted: 4 (OpenRGB 0.9) is the first with zone segments.</summary>
    public const uint MinProtocolVersion = 4;

    /// <summary>Upper bound for any incoming payload; a full controller description is a few KB.</summary>
    public const int MaxPayloadSize = 4 * 1024 * 1024;

    public static ReadOnlySpan<byte> Magic => "ORGB"u8;
}

public sealed class OpenRgbProtocolException(string message) : Exception(message);
