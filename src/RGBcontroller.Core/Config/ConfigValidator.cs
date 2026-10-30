using System.Net;
using System.Text.RegularExpressions;

namespace RGBcontroller.Core.Config;

/// <summary>Range and safety checks for a deserialized <see cref="AppConfig"/>.</summary>
public static partial class ConfigValidator
{
    public const int MaxStripLeds = 1024;
    public const int MaxStripFps = 60;

    public static IReadOnlyList<string> Validate(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var errors = new List<string>();

        // schemaVersion is handled by ConfigStore and ConfigMigrator, not here.
        var strip = config.Strip;
        if (strip.PortName is not null && !ComPortRegex().IsMatch(strip.PortName))
        {
            errors.Add($"strip.portName '{strip.PortName}' is not a COM port name (expected e.g. COM3).");
        }

        if (!UsbIdRegex().IsMatch(strip.UsbVendorId))
        {
            errors.Add($"strip.usbVendorId '{strip.UsbVendorId}' must be 4 hex digits.");
        }

        if (!UsbIdRegex().IsMatch(strip.UsbProductId))
        {
            errors.Add($"strip.usbProductId '{strip.UsbProductId}' must be 4 hex digits.");
        }

        CheckRange(errors, "strip.ledCount", strip.LedCount, 1, MaxStripLeds);
        CheckRange(errors, "strip.baudRate", strip.BaudRate, 9600, 4_000_000);
        CheckRange(errors, "strip.maxFps", strip.MaxFps, 1, MaxStripFps);
        CheckRange(errors, "strip.openSettleMilliseconds", strip.OpenSettleMilliseconds, 0, 10_000);
        CheckRange(errors, "strip.currentLimitMilliamps", strip.CurrentLimitMilliamps, 100, 20_000);
        if (!Enum.IsDefined(strip.ColorOrder))
        {
            errors.Add($"strip.colorOrder '{strip.ColorOrder}' is not supported.");
        }

        var openRgb = config.OpenRgb;
        if (string.IsNullOrWhiteSpace(openRgb.ExecutablePath))
        {
            errors.Add("openRgb.executablePath must not be empty.");
        }

        if (!IsLoopback(openRgb.Host))
        {
            errors.Add($"openRgb.host '{openRgb.Host}' must be a loopback address.");
        }

        CheckRange(errors, "openRgb.port", openRgb.Port, 1024, 65535);
        CheckRange(errors, "openRgb.startupTimeoutSeconds", openRgb.StartupTimeoutSeconds, 5, 300);
        CheckRange(errors, "openRgb.deviceSettleMilliseconds", openRgb.DeviceSettleMilliseconds, 0, 10_000);
        CheckRange(errors, "strip.keepAliveMilliseconds", strip.KeepAliveMilliseconds, 100, 60_000);
        foreach (var zone in openRgb.ZoneSizes)
        {
            if (string.IsNullOrWhiteSpace(zone.Device) || string.IsNullOrWhiteSpace(zone.Zone))
            {
                errors.Add("openRgb.zoneSizes entries need a device and a zone name.");
            }

            CheckRange(errors, $"openRgb.zoneSizes '{zone.Zone}'", zone.Leds, 0, MaxStripLeds);
        }

        var lighting = config.Lighting;
        if (!RgbColor.TryParseHex(lighting.StaticColor, out _))
        {
            errors.Add($"lighting.staticColor '{lighting.StaticColor}' must be #RRGGBB.");
        }

        CheckRange(errors, "lighting.deviceBrightnessPercent", lighting.DeviceBrightnessPercent, 0, 100);
        CheckRange(errors, "lighting.stripBrightnessPercent", lighting.StripBrightnessPercent, 0, 100);

        if (!Enum.IsDefined(config.Logging.MinimumLevel))
        {
            errors.Add($"logging.minimumLevel '{config.Logging.MinimumLevel}' is not supported.");
        }

        return errors;
    }

    private static void CheckRange(List<string> errors, string name, int value, int min, int max)
    {
        if (value < min || value > max)
        {
            errors.Add($"{name} must be between {min} and {max}, found {value}.");
        }
    }

    private static bool IsLoopback(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));

    [GeneratedRegex(@"^COM[1-9][0-9]{0,2}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ComPortRegex();

    [GeneratedRegex(@"^[0-9A-Fa-f]{4}$", RegexOptions.CultureInvariant)]
    private static partial Regex UsbIdRegex();
}
