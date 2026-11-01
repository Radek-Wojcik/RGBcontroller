namespace RGBcontroller.Core.Config;

/// <summary>
/// Effects and profiles (SPEC section 5). Three layers: the hardware description (<see cref="Layouts"/> gives every
/// zone a shape so LEDs get positions, <see cref="Groups"/> bundles zones), the <see cref="Palettes"/> library, and
/// the <see cref="Profiles"/> that assign an effect to each group. Defaults are pre-mapped to the owner's devices
/// (<see cref="DefaultLighting"/>); the mapping wizard (stage 5) will write the same structures.
/// </summary>
public sealed record LightingConfig
{
    public string ActiveProfile { get; set; } = DefaultLighting.RainbowProfile;

    /// <summary>Profile shown on the logon screen (no user in the console session).</summary>
    public string LogonProfile { get; set; } = DefaultLighting.LogonProfileName;

    /// <summary>Profile while the console session is locked; null means the active profile.</summary>
    public string? LockProfile { get; set; }

    /// <summary>What turns off while the display is off (SPEC 4).</summary>
    public DisplayOffConfig DisplayOff { get; set; } = new();

    /// <summary>Group that lights zones no group claims; null means unclaimed zones stay off.</summary>
    public string? DefaultGroup { get; set; }

    public IReadOnlyList<ZoneLayoutConfig> Layouts { get; set; } = DefaultLighting.Layouts;

    public IReadOnlyList<GroupConfig> Groups { get; set; } = DefaultLighting.Groups;

    public IReadOnlyList<PaletteConfig> Palettes { get; set; } = DefaultLighting.Palettes;

    public IReadOnlyList<ProfileConfig> Profiles { get; set; } = DefaultLighting.Profiles;

    public ProfileConfig? FindProfile(string? name) =>
        Profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    public bool Equals(LightingConfig? other) =>
        other is not null
        && ActiveProfile == other.ActiveProfile
        && LogonProfile == other.LogonProfile
        && LockProfile == other.LockProfile
        && DisplayOff == other.DisplayOff
        && DefaultGroup == other.DefaultGroup
        && ListEquality.Equal(Layouts, other.Layouts)
        && ListEquality.Equal(Groups, other.Groups)
        && ListEquality.Equal(Palettes, other.Palettes)
        && ListEquality.Equal(Profiles, other.Profiles);

    public override int GetHashCode() => HashCode.Combine(ActiveProfile, Groups.Count, Profiles.Count);
}

/// <summary>
/// The two per-device turn-off sets of SPEC 4. Everything not in the set keeps its profile while the display is off.
/// </summary>
public sealed record DisplayOffConfig
{
    /// <summary>Display off by the idle timeout while a user is logged in and the session is unlocked.</summary>
    public TurnOffSet Unlocked { get; set; } = new() { Strip = true };

    /// <summary>Display off while the session is locked or the logon screen is shown.</summary>
    public TurnOffSet Locked { get; set; } = new() { Strip = true, Devices = [DefaultLighting.Keyboard, DefaultLighting.Mouse] };
}

/// <summary>Outputs forced to black in one situation.</summary>
public sealed record TurnOffSet
{
    public bool Strip { get; set; }

    /// <summary>OpenRGB device names, exactly as <c>--probe</c> reports them.</summary>
    public IReadOnlyList<string> Devices { get; set; } = [];

    public bool Contains(string device) => Devices.Contains(device, StringComparer.OrdinalIgnoreCase);

    public bool Equals(TurnOffSet? other) =>
        other is not null && Strip == other.Strip && ListEquality.Equal(Devices, other.Devices);

    public override int GetHashCode() => HashCode.Combine(Strip, Devices.Count);
}

/// <summary>
/// Geometry of one OpenRGB zone, matched by device and zone name. The zone's LEDs are split into
/// <see cref="Segments"/> in LED order (e.g. a pump ring followed by a fan ring on one ARGB header).
/// Zones without an entry use <see cref="LedShape.Auto"/>.
/// </summary>
public sealed record ZoneLayoutConfig
{
    public string Device { get; set; } = "";

    public string Zone { get; set; } = "";

    public IReadOnlyList<SegmentLayoutConfig> Segments { get; set; } = [new()];

    public bool Equals(ZoneLayoutConfig? other) =>
        other is not null && Device == other.Device && Zone == other.Zone && ListEquality.Equal(Segments, other.Segments);

    public override int GetHashCode() => HashCode.Combine(Device, Zone, Segments.Count);
}

/// <summary>A run of LEDs inside a zone that forms one physical component.</summary>
public sealed record SegmentLayoutConfig
{
    /// <summary>LED count; null means all remaining LEDs of the zone.</summary>
    public int? Leds { get; set; }

    public LedShape Shape { get; set; } = LedShape.Auto;

    /// <summary>Line runs top to bottom instead of left to right (RAM sticks standing upright).</summary>
    public bool Vertical { get; set; }

    /// <summary>LED order is reversed (line: right to left or bottom to top; ring: counterclockwise).</summary>
    public bool Reverse { get; set; }

    /// <summary>Ring only: index of the LED at the top (12 o'clock).</summary>
    public int Offset { get; set; }
}

public enum LedShape
{
    /// <summary>Matrix when the zone has a matrix map, a point for one LED, otherwise a horizontal line.</summary>
    Auto,
    Line,
    Ring,
    Matrix,
    Point,
}

/// <summary>Zones driven together by one effect. A zone belongs to the first group that lists it.</summary>
public sealed record GroupConfig
{
    public string Name { get; set; } = "";

    public IReadOnlyList<ZoneRef> Members { get; set; } = [];

    /// <summary>Frame rate cap for the devices of this group (SPEC 5.1: RAM and GPU are slow I2C/SMBus buses).</summary>
    public int MaxFps { get; set; } = 30;

    public bool Equals(GroupConfig? other) =>
        other is not null && Name == other.Name && MaxFps == other.MaxFps && ListEquality.Equal(Members, other.Members);

    public override int GetHashCode() => HashCode.Combine(Name, MaxFps, Members.Count);
}

/// <summary>An OpenRGB device by name, optionally narrowed to one zone (null = every zone of the device).</summary>
public sealed record ZoneRef(string Device, string? Zone = null);

/// <summary>A cyclic gradient: the last stop blends back into the first.</summary>
public sealed record PaletteConfig
{
    public string Name { get; set; } = "";

    public IReadOnlyList<PaletteStop> Stops { get; set; } = [];

    public bool Equals(PaletteConfig? other) =>
        other is not null && Name == other.Name && ListEquality.Equal(Stops, other.Stops);

    public override int GetHashCode() => HashCode.Combine(Name, Stops.Count);
}

/// <param name="Position">0..1 along the gradient.</param>
/// <param name="Color">"#RRGGBB".</param>
public sealed record PaletteStop(double Position, string Color);

/// <summary>Complete lighting state (SPEC 5.4): one effect per group, the ambilight and the master brightness.</summary>
public sealed record ProfileConfig
{
    public string Name { get; set; } = "";

    /// <summary>0-100, applied last on top of every group and the strip; 100 means full diode power (255).</summary>
    public int MasterBrightnessPercent { get; set; } = 100;

    /// <summary>Effect per group; groups not listed here are off.</summary>
    public IReadOnlyList<GroupEffectConfig> Groups { get; set; } = [];

    public AmbilightConfig Ambilight { get; set; } = new();

    public GroupEffectConfig? FindGroup(string name) =>
        Groups.FirstOrDefault(g => string.Equals(g.Group, name, StringComparison.OrdinalIgnoreCase));

    public bool Equals(ProfileConfig? other) =>
        other is not null
        && Name == other.Name
        && MasterBrightnessPercent == other.MasterBrightnessPercent
        && Ambilight == other.Ambilight
        && ListEquality.Equal(Groups, other.Groups);

    public override int GetHashCode() => HashCode.Combine(Name, MasterBrightnessPercent, Groups.Count);
}

/// <summary>Effect settings for one group. Which fields matter depends on <see cref="Effect"/>.</summary>
public sealed record GroupEffectConfig
{
    public string Group { get; set; } = "";

    public EffectKind Effect { get; set; } = EffectKind.Static;

    /// <summary>Palette name from <see cref="LightingConfig.Palettes"/>, or one color as "#RRGGBB".</summary>
    public string Palette { get; set; } = DefaultLighting.RainbowPalette;

    /// <summary>Palette cycles per second (Breathing: breaths per second). 0 freezes the pattern.</summary>
    public double Speed { get; set; } = 0.2;

    /// <summary>Degrees. Wave: direction of travel (0 = to the right, 90 = down, 180 = to the left).
    /// Mirror: axis the pattern spreads along (90 = up and down). Static, Breathing, Aurora: gradient direction.</summary>
    public double Angle { get; set; }

    /// <summary>Radial: counterclockwise or inward. Mirror: inward instead of outward.</summary>
    public bool Reverse { get; set; }

    /// <summary>Palette repetitions across one component (keyboard, stick, ring).</summary>
    public double Scale { get; set; } = 1.0;

    public RadialMode RadialMode { get; set; } = RadialMode.Rotate;

    /// <summary>0-100, applied after the effect; 100 means full diode power (255).</summary>
    public int BrightnessPercent { get; set; } = 100;

    /// <summary><see cref="EffectKind.Hardware"/> only: the device's own mode name (e.g. "Spectrum Cycle"), 0% CPU but not synchronized.</summary>
    public string? HardwareMode { get; set; }
}

public enum EffectKind
{
    Off,
    Static,
    Breathing,
    Wave,
    Radial,
    Mirror,
    ColorCycle,
    Aurora,

    /// <summary>The device runs one of its own modes; whole devices only.</summary>
    Hardware,
}

public enum RadialMode
{
    /// <summary>Hue by angle around the component center, rotating.</summary>
    Rotate,

    /// <summary>Hue by distance from the center, expanding.</summary>
    Expand,
}

/// <summary>Strip behaviour inside a profile (SPEC 6.1).</summary>
public sealed record AmbilightConfig
{
    public AmbilightMode Mode { get; set; } = AmbilightMode.Screen;

    /// <summary>Used while screen capture is unavailable (logon screen, lock, no Agent; until stage 4 always).</summary>
    public AmbilightMode Fallback { get; set; } = AmbilightMode.Radial;

    /// <summary>0-100 for Screen and Radial; 100 means full diode power (255).</summary>
    public int BrightnessPercent { get; set; } = 50;

    public string SolidColor { get; set; } = "#FFFFFF";

    /// <summary>0-100, the power of Solid mode (replaces <see cref="BrightnessPercent"/> there).</summary>
    public int SolidBrightnessPercent { get; set; } = 100;

    /// <summary>Radial mode palette (name or "#RRGGBB").</summary>
    public string Palette { get; set; } = DefaultLighting.RainbowPalette;

    /// <summary>Radial mode rotations per second.</summary>
    public double Speed { get; set; } = 0.1;
}

public enum AmbilightMode
{
    Screen,
    Radial,
    Solid,
    Off,
}

/// <summary>Value equality for list-valued record properties (records compare lists by reference).</summary>
internal static class ListEquality
{
    public static bool Equal<T>(IReadOnlyList<T> left, IReadOnlyList<T> right) =>
        ReferenceEquals(left, right) || left.SequenceEqual(right);
}
