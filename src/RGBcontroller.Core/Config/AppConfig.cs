namespace RGBcontroller.Core.Config;

/// <summary>
/// Root of the persisted configuration (<c>config.json</c>). Treat instances as immutable: change them with
/// <c>with</c> expressions so the Service can swap snapshots atomically. Setters (not init) are required because
/// the System.Text.Json source generator resets missing init-only properties to default instead of keeping initializers.
/// New sections may be added with defaults without a migration; renames and restructuring need an
/// <see cref="IConfigMigration"/> and a bump of <see cref="CurrentSchemaVersion"/>.
/// </summary>
public sealed record AppConfig
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public StripConfig Strip { get; set; } = new();

    public OpenRgbConfig OpenRgb { get; set; } = new();

    public LightingConfig Lighting { get; set; } = new();

    public LoggingConfig Logging { get; set; } = new();

    public static AppConfig CreateDefault() => new();
}

/// <summary>Adalight strip hardware (defaults are the owner's verified values, SPEC section 2).</summary>
public sealed record StripConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>Explicit port override, e.g. "COM3". Null means discover by USB VID/PID.</summary>
    public string? PortName { get; set; }

    public string UsbVendorId { get; set; } = "1A86";

    public string UsbProductId { get; set; } = "7523";

    public int LedCount { get; set; } = 113;

    public int BaudRate { get; set; } = 115200;

    public ColorOrder ColorOrder { get; set; } = ColorOrder.Rgb;

    /// <summary>Upper bound for frames per second; the runtime also clamps to the serial link limit.</summary>
    public int MaxFps { get; set; } = 30;

    /// <summary>Time the board needs after the port opens before frames are reliable.</summary>
    public int OpenSettleMilliseconds { get; set; } = 800;

    /// <summary>Safety fuse for the estimated strip current (estimate = sum of channel values / 255 x 6.5 mA).</summary>
    public int CurrentLimitMilliamps { get; set; } = 4500;

    /// <summary>A static frame is re-sent at this interval so the strip never depends on firmware idle behaviour.</summary>
    public int KeepAliveMilliseconds { get; set; } = 1000;
}

/// <summary>The supervised, pinned OpenRGB instance.</summary>
public sealed record OpenRgbConfig
{
    /// <summary>Path to OpenRGB.exe; relative paths are resolved against the install folder.</summary>
    public string ExecutablePath { get; set; } = @"openrgb\OpenRGB.exe";

    /// <summary>SDK server host. Must be a loopback address (SPEC section 9: no network access).</summary>
    public string Host { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 6742;

    /// <summary>How long to wait for the SDK server of a freshly started OpenRGB before restarting it.</summary>
    public int StartupTimeoutSeconds { get; set; } = 30;

    /// <summary>Quiet period after a DEVICE_LIST_UPDATED notice before devices are re-read and lit.</summary>
    public int DeviceSettleMilliseconds { get; set; } = 500;

    /// <summary>LED counts for resizable zones (ARGB headers). Applied through the SDK after detection.</summary>
    public IReadOnlyList<ZoneSizeConfig> ZoneSizes { get; set; } =
    [
        new("ASUS TUF GAMING H670-PRO WIFI D4", "Aura Addressable 1", 20),
        new("ASUS TUF GAMING H670-PRO WIFI D4", "Aura Addressable 2", 20),
        new("ASUS TUF GAMING H670-PRO WIFI D4", "Aura Addressable 3", 20),
    ];

    public bool Equals(OpenRgbConfig? other) =>
        other is not null
        && ExecutablePath == other.ExecutablePath
        && Host == other.Host
        && Port == other.Port
        && StartupTimeoutSeconds == other.StartupTimeoutSeconds
        && DeviceSettleMilliseconds == other.DeviceSettleMilliseconds
        && ZoneSizes.SequenceEqual(other.ZoneSizes);

    public override int GetHashCode() => HashCode.Combine(ExecutablePath, Host, Port, ZoneSizes.Count);
}

/// <summary>Configured LED count of one resizable zone, matched by OpenRGB device name and zone name.</summary>
public sealed record ZoneSizeConfig(string Device, string Zone, int Leds);

/// <summary>Stage 1 lighting: one static color everywhere (effects and profiles arrive in stage 2).</summary>
public sealed record LightingConfig
{
    /// <summary>"#RRGGBB".</summary>
    public string StaticColor { get; set; } = "#00E5FF";

    /// <summary>0-100; 100 means full diode power (255).</summary>
    public int DeviceBrightnessPercent { get; set; } = 100;

    /// <summary>0-100; 100 means full diode power (255).</summary>
    public int StripBrightnessPercent { get; set; } = 50;
}

public sealed record LoggingConfig
{
    public LogLevelSetting MinimumLevel { get; set; } = LogLevelSetting.Information;
}

public enum ColorOrder
{
    Rgb,
    Rbg,
    Grb,
    Gbr,
    Brg,
    Bgr,
}

public enum LogLevelSetting
{
    Verbose,
    Debug,
    Information,
    Warning,
    Error,
}
