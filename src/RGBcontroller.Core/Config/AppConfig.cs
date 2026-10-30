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
}

/// <summary>The supervised, pinned OpenRGB instance.</summary>
public sealed record OpenRgbConfig
{
    /// <summary>Path to OpenRGB.exe; relative paths are resolved against the install folder.</summary>
    public string ExecutablePath { get; set; } = @"openrgb\OpenRGB.exe";

    /// <summary>SDK server host. Must be a loopback address (SPEC section 9: no network access).</summary>
    public string Host { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 6742;
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
