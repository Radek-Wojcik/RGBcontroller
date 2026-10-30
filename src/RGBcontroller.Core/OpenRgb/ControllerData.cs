namespace RGBcontroller.Core.OpenRgb;

/// <summary>Device description returned by REQUEST_CONTROLLER_DATA.</summary>
public sealed record ControllerData(
    int Type,
    string Name,
    string Vendor,
    string Description,
    string Version,
    string Serial,
    string Location,
    int ActiveMode,
    IReadOnlyList<ControllerMode> Modes,
    IReadOnlyList<ControllerZone> Zones,
    IReadOnlyList<ControllerLed> Leds,
    int ColorCount,
    IReadOnlyList<string> LedAlternateNames,
    uint Flags);

public sealed record ControllerMode(
    string Name,
    int Value,
    uint Flags,
    uint SpeedMin,
    uint SpeedMax,
    uint BrightnessMin,
    uint BrightnessMax,
    uint ColorsMin,
    uint ColorsMax,
    uint Speed,
    uint Brightness,
    uint Direction,
    uint ColorMode,
    IReadOnlyList<uint> Colors);

public sealed record ControllerZone(
    string Name,
    int Type,
    uint LedsMin,
    uint LedsMax,
    uint LedsCount,
    uint MatrixHeight,
    uint MatrixWidth,
    IReadOnlyList<uint> Matrix,
    IReadOnlyList<ControllerSegment> Segments,
    uint Flags);

public sealed record ControllerSegment(string Name, int Type, uint StartIndex, uint LedsCount);

public sealed record ControllerLed(string Name, uint Value);
