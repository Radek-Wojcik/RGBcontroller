namespace RGBcontroller.Core.Config;

/// <summary>
/// Shipped layouts, groups, palettes and profiles, pre-mapped to the owner's devices (SPEC 2 and 5.3-5.4).
/// Device and zone names are exactly what OpenRGB 1.0rc2 reports on that PC (<c>--probe</c>).
/// </summary>
public static class DefaultLighting
{
    public const string RainbowPalette = "Rainbow";
    public const string RainbowProfile = "Rainbow";
    public const string LogonProfileName = "Logon";

    public const string Motherboard = "ASUS TUF GAMING H670-PRO WIFI D4";
    public const string Ram = "HyperX Predator RGB";
    public const string Gpu = "Gigabyte GeForce RTX 3080 12G Gaming OC";
    public const string Keyboard = "Razer Ornata Chroma V2";
    public const string Mouse = "G502 HERO Gaming Mouse";

    public const string PumpAndFansGroup = "Pump + Fans";
    public const string BoardGroup = "Board";
    public const string RamGroup = "RAM";
    public const string KeyboardGroup = "Keyboard";
    public const string MouseGroup = "Mouse";
    public const string GpuGroup = "GPU";

    /// <summary>
    /// The ARGB headers start as one clockwise 20-LED ring each (which header drives the pump and which the fans is
    /// unknown until the mapping wizard). RAM sticks stand upright, so each slot is a vertical line.
    /// </summary>
    public static IReadOnlyList<ZoneLayoutConfig> Layouts { get; } =
    [
        Zone(Motherboard, "Aura Addressable 1", LedShape.Ring),
        Zone(Motherboard, "Aura Addressable 2", LedShape.Ring),
        Zone(Motherboard, "Aura Addressable 3", LedShape.Ring),
        Zone(Ram, "HyperX Slot 1", LedShape.Line, vertical: true),
        Zone(Ram, "HyperX Slot 2", LedShape.Line, vertical: true),
        Zone(Ram, "HyperX Slot 3", LedShape.Line, vertical: true),
        Zone(Ram, "HyperX Slot 4", LedShape.Line, vertical: true),
    ];

    /// <summary>Frame rate caps follow SPEC 5.1: 30 for USB devices, mouse 15, GPU 10. RAM runs at 5: every RAM frame is
    /// many SMBus writes that OpenRGB busy-waits on in the kernel (1.74% CPU at 12 FPS, measured at the stage 2 gate).</summary>
    public static IReadOnlyList<GroupConfig> Groups { get; } =
    [
        new()
        {
            Name = PumpAndFansGroup,
            Members = [new(Motherboard, "Aura Addressable 1"), new(Motherboard, "Aura Addressable 2"), new(Motherboard, "Aura Addressable 3")],
        },
        new() { Name = BoardGroup, Members = [new(Motherboard, "Aura Mainboard")] },
        new() { Name = RamGroup, Members = [new(Ram)], MaxFps = 5 },
        new() { Name = KeyboardGroup, Members = [new(Keyboard)] },
        new() { Name = MouseGroup, Members = [new(Mouse)], MaxFps = 15 },
        new() { Name = GpuGroup, Members = [new(Gpu)], MaxFps = 10 },
    ];

    public static IReadOnlyList<PaletteConfig> Palettes { get; } =
    [
        Palette(RainbowPalette, "#FF0000", "#FFFF00", "#00FF00", "#00FFFF", "#0000FF", "#FF00FF"),
        Palette("Cyberpunk", "#FF00A0", "#00E5FF", "#8A2BE2"),
        Palette("Aurora", "#00C9A7", "#2BFF88", "#00A3FF", "#7B2FF7"),
        Palette("Ember", "#FF7A1A", "#FF4A10"),
    ];

    public static IReadOnlyList<ProfileConfig> Profiles { get; } =
    [
        Rainbow(RainbowProfile, speedFactor: 1.0, AmbilightMode.Screen),
        Synced("Cyberpunk", "Cyberpunk", speedFactor: 0.6) with
        {
            // The strip matches the theme when it cannot mirror the screen (lock screen) or runs Radial.
            Ambilight = new() { Mode = AmbilightMode.Screen, Palette = "Cyberpunk", Speed = 0.06 },
        },
        Aurora(),
        Night(),
        SolidWhite(),
        Rainbow(LogonProfileName, speedFactor: 0.5, AmbilightMode.Radial),
    ];

    /// <summary>SPEC 5.3, the owner's current look: radial pump and fans, keyboard wave right to left, mouse and GPU
    /// color cycle, RAM mirrored from the middle of each stick.</summary>
    private static ProfileConfig Rainbow(string name, double speedFactor, AmbilightMode strip) =>
        Synced(name, RainbowPalette, speedFactor) with { Ambilight = new() { Mode = strip } };

    private static ProfileConfig Synced(string name, string palette, double speedFactor) => new()
    {
        Name = name,
        Groups =
        [
            new() { Group = PumpAndFansGroup, Effect = EffectKind.Radial, Palette = palette, Speed = 0.25 * speedFactor },
            new() { Group = BoardGroup, Effect = EffectKind.ColorCycle, Palette = palette, Speed = 0.1 * speedFactor },
            new() { Group = RamGroup, Effect = EffectKind.Mirror, Palette = palette, Speed = 0.3 * speedFactor, Angle = 90, Scale = 0.5 },
            new() { Group = KeyboardGroup, Effect = EffectKind.Wave, Palette = palette, Speed = 0.25 * speedFactor, Angle = 180 },
            new() { Group = MouseGroup, Effect = EffectKind.ColorCycle, Palette = palette, Speed = 0.1 * speedFactor },
            new() { Group = GpuGroup, Effect = EffectKind.ColorCycle, Palette = palette, Speed = 0.1 * speedFactor },
        ],
    };

    private static ProfileConfig Aurora() => new()
    {
        Name = "Aurora",
        Groups =
        [
            new() { Group = PumpAndFansGroup, Effect = EffectKind.Aurora, Palette = "Aurora", Speed = 0.05 },
            new() { Group = BoardGroup, Effect = EffectKind.Aurora, Palette = "Aurora", Speed = 0.05 },
            new() { Group = RamGroup, Effect = EffectKind.Aurora, Palette = "Aurora", Speed = 0.05, Angle = 90 },
            new() { Group = KeyboardGroup, Effect = EffectKind.Aurora, Palette = "Aurora", Speed = 0.05, Angle = 180 },
            new() { Group = MouseGroup, Effect = EffectKind.Aurora, Palette = "Aurora", Speed = 0.05 },
            new() { Group = GpuGroup, Effect = EffectKind.Aurora, Palette = "Aurora", Speed = 0.05 },
        ],
        Ambilight = new() { Mode = AmbilightMode.Screen, Palette = "Aurora", Speed = 0.03 },
    };

    private static ProfileConfig Night() => new()
    {
        Name = "Night",
        Groups =
        [
            new() { Group = PumpAndFansGroup, Effect = EffectKind.Static, Palette = "#FF5A14", BrightnessPercent = 20 },
            new() { Group = BoardGroup, Effect = EffectKind.Off },
            new() { Group = RamGroup, Effect = EffectKind.Static, Palette = "#FF5A14", BrightnessPercent = 15 },
            new() { Group = KeyboardGroup, Effect = EffectKind.Static, Palette = "#FF7A1A", BrightnessPercent = 25 },
            new() { Group = MouseGroup, Effect = EffectKind.Off },
            new() { Group = GpuGroup, Effect = EffectKind.Off },
        ],
        Ambilight = new()
        {
            Mode = AmbilightMode.Solid, Fallback = AmbilightMode.Solid, SolidColor = "#FF8C3A", SolidBrightnessPercent = 15, Palette = "Ember",
        },
    };

    private static ProfileConfig SolidWhite() => new()
    {
        Name = "Solid White",
        Groups =
        [
            new() { Group = PumpAndFansGroup, Effect = EffectKind.Static, Palette = "#FFFFFF" },
            new() { Group = BoardGroup, Effect = EffectKind.Static, Palette = "#FFFFFF" },
            new() { Group = RamGroup, Effect = EffectKind.Static, Palette = "#FFFFFF" },
            new() { Group = KeyboardGroup, Effect = EffectKind.Static, Palette = "#FFFFFF" },
            new() { Group = MouseGroup, Effect = EffectKind.Static, Palette = "#FFFFFF" },
            new() { Group = GpuGroup, Effect = EffectKind.Static, Palette = "#FFFFFF" },
        ],
        Ambilight = new() { Mode = AmbilightMode.Solid, Fallback = AmbilightMode.Solid, SolidColor = "#FFFFFF", SolidBrightnessPercent = 100 },
    };

    private static ZoneLayoutConfig Zone(string device, string zone, LedShape shape, bool vertical = false) =>
        new() { Device = device, Zone = zone, Segments = [new() { Shape = shape, Vertical = vertical }] };

    /// <summary>Evenly spaced stops; the gradient wraps from the last color back to the first.</summary>
    private static PaletteConfig Palette(string name, params string[] colors) => new()
    {
        Name = name,
        Stops = [.. colors.Select((color, i) => new PaletteStop(Math.Round((double)i / colors.Length, 4), color))],
    };
}
