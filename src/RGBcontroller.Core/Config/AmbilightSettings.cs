namespace RGBcontroller.Core.Config;

/// <summary>
/// Where the strip LEDs sit around the monitor (SPEC 6.3), viewed from the front. LEDs are numbered from the data
/// input. The path starts at <see cref="StartCorner"/> and runs <see cref="Clockwise"/> over the four edges, each
/// edge's LEDs spread evenly along it. LEDs listed in <see cref="Gaps"/> stay dark and take no place on the path;
/// LEDs beyond the edge total stay dark too, and edge positions beyond the strip's LED count stay unused. The layout wizard (stage 5) writes the same structure.
/// </summary>
public sealed record StripLayoutConfig
{
    public StripCorner StartCorner { get; set; } = StripCorner.BottomLeft;

    /// <summary>Viewed from the front: clockwise runs left to right along the top edge.</summary>
    public bool Clockwise { get; set; } = true;

    public int Top { get; set; } = 40;

    public int Right { get; set; } = 21;

    public int Bottom { get; set; } = 30;

    public int Left { get; set; } = 21;

    /// <summary>Path positions the first LED is shifted along the direction (e.g. a strip that starts at the middle of the bottom edge).</summary>
    public int Offset { get; set; }

    /// <summary>LED indices (0-based, data order) that stay dark, e.g. LEDs hidden behind the monitor stand.</summary>
    public IReadOnlyList<int> Gaps { get; set; } = [];

    public int EdgeLeds => Top + Right + Bottom + Left;

    public bool Equals(StripLayoutConfig? other) =>
        other is not null
        && StartCorner == other.StartCorner
        && Clockwise == other.Clockwise
        && Top == other.Top
        && Right == other.Right
        && Bottom == other.Bottom
        && Left == other.Left
        && Offset == other.Offset
        && ListEquality.Equal(Gaps, other.Gaps);

    public override int GetHashCode() => HashCode.Combine(StartCorner, Clockwise, Top, Right, Bottom, Left, Offset, Gaps.Count);
}

public enum StripCorner
{
    TopLeft,
    TopRight,
    BottomRight,
    BottomLeft,
}

/// <summary>
/// Screen mode pipeline of the Agent (SPEC 6.2). Every value is a setting; the defaults are the agreed ones.
/// Color order: average, black threshold, gamma, saturation, then the Service applies brightness and the current limiter.
/// </summary>
public sealed record ScreenCaptureConfig
{
    /// <summary>Monitor to mirror: a GDI device name such as <c>\\.\DISPLAY1</c>; null means the primary monitor.</summary>
    public string? Monitor { get; set; }

    /// <summary>Desktop frames processed per second at most.</summary>
    public int CaptureFps { get; set; } = 20;

    /// <summary>Smoothed frames sent to the strip per second at most (the Service also clamps to the serial link).</summary>
    public int OutputFps { get; set; } = 30;

    /// <summary>The GPU downscales until the frame is no narrower than this (3840 px becomes 480 px).</summary>
    public int MinimumSampleWidth { get; set; } = 320;

    /// <summary>Depth of the sampled band along each screen edge.</summary>
    public double EdgeDepthCm { get; set; } = 2.5;

    /// <summary>Physical width of the visible screen; 0 reads it from the monitor's EDID.</summary>
    public double ScreenWidthCm { get; set; }

    /// <summary>Each LED averages its own part of the edge widened by this much, so neighbours blend.</summary>
    public int SegmentOverlapPercent { get; set; } = 30;

    /// <summary>0-255. A color whose brightest channel is at or below this is black (noise, dithering, film banding).</summary>
    public int BlackThreshold { get; set; } = 10;

    /// <summary>0-255. Above the threshold the color fades in over this range (soft knee) instead of jumping on.</summary>
    public int BlackKnee { get; set; } = 10;

    /// <summary>Output = input ^ gamma (0..1); above 1 keeps dark scenes dark.</summary>
    public double Gamma { get; set; } = 2.0;

    /// <summary>1 keeps the colors; 0 is grey, above 1 more saturated.</summary>
    public double Saturation { get; set; } = 1.0;

    /// <summary>Time constant towards a brighter color.</summary>
    public int AttackMilliseconds { get; set; } = 80;

    /// <summary>Time constant towards a darker color.</summary>
    public int ReleaseMilliseconds { get; set; } = 300;

    /// <summary>Time constant towards black; short, so a fade to black leaves no trailing glow.</summary>
    public int BlackReleaseMilliseconds { get; set; } = 60;

    /// <summary>Shift the sampled bands inside letterbox or pillarbox bars.</summary>
    public bool LetterboxDetection { get; set; } = true;

    /// <summary>Bars must measure the same for this long before the sampled bands move (hysteresis).</summary>
    public int LetterboxStableMilliseconds { get; set; } = 1500;

    /// <summary>HDR only: brightness of SDR white in nits; 0 reads the Windows "SDR content brightness" setting.</summary>
    public int HdrSdrWhiteNits { get; set; }

    /// <summary>A capture outage shorter than this keeps the last colors; after it the Service shows the fallback mode.</summary>
    public int FallbackGraceMilliseconds { get; set; } = 1500;
}

/// <summary>Global hotkeys of the Agent (SPEC 7.2): each one is a modifier plus one key, registered with RegisterHotKey.</summary>
public sealed record HotkeysConfig
{
    public IReadOnlyList<HotkeyBinding> Bindings { get; set; } =
    [
        new("Ctrl+F9", HotkeyAction.StripSolidToggle),
        new("Ctrl+F10", HotkeyAction.CycleStripMode),
        new("Ctrl+F11", HotkeyAction.StripBrightnessDown),
        new("Ctrl+F12", HotkeyAction.StripBrightnessUp),
    ];

    /// <summary>Percentage points per brightness hotkey press.</summary>
    public int BrightnessStepPercent { get; set; } = 10;

    public bool Equals(HotkeysConfig? other) =>
        other is not null && BrightnessStepPercent == other.BrightnessStepPercent && ListEquality.Equal(Bindings, other.Bindings);

    public override int GetHashCode() => HashCode.Combine(BrightnessStepPercent, Bindings.Count);
}

/// <param name="Keys">"Ctrl+F9": one of Ctrl, Alt, Shift, Win, then one key.</param>
/// <param name="Profile">Profile name for <see cref="HotkeyAction.Profile"/>.</param>
public sealed record HotkeyBinding(string Keys, HotkeyAction Action, string? Profile = null);

public enum HotkeyAction
{
    /// <summary>Strip to Solid (color and power from the profile) and back to the previous mode.</summary>
    StripSolidToggle,
    StripBrightnessUp,
    StripBrightnessDown,

    /// <summary>Screen, Radial, Solid, Off, Screen...</summary>
    CycleStripMode,
    NextProfile,
    PreviousProfile,

    /// <summary>Switch to <see cref="HotkeyBinding.Profile"/>.</summary>
    Profile,

    /// <summary>Everything off, or back on.</summary>
    AllOffToggle,
}
