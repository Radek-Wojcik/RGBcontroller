using RGBcontroller.Core;
using RGBcontroller.Core.Config;
using RGBcontroller.Core.OpenRgb;
using Serilog;

namespace RGBcontroller.Service.OpenRgb;

/// <summary>
/// Stage 1 device lighting: re-reads every OpenRGB controller, applies configured zone sizes, switches it to
/// Direct mode and writes one static color. Idempotent, so it is simply re-run whenever the device list changes.
/// </summary>
internal sealed class DeviceLighting(
    OpenRgbSdkClient client, OpenRgbConfig openRgb, LightingConfig lighting, StartupClock clock, ILogger log)
{
    private readonly ILogger _log = log.ForContext<DeviceLighting>();
    private readonly HashSet<string> _litDevices = [];
    private readonly List<int> _ledCounts = [];

    /// <summary>Devices from the last successful <see cref="ApplyAllAsync"/>.</summary>
    public IReadOnlyList<ControllerData> Devices { get; private set; } = [];

    public async Task ApplyAllAsync(CancellationToken cancellationToken)
    {
        RgbColor.TryParseHex(lighting.StaticColor, out var baseColor);
        var color = baseColor.Scale(lighting.DeviceBrightnessPercent);

        var count = await client.GetControllerCountAsync(cancellationToken).ConfigureAwait(false);
        var devices = new List<ControllerData>(count);
        _ledCounts.Clear();
        for (var index = 0; index < count; index++)
        {
            var data = await client.GetControllerDataAsync(index, cancellationToken).ConfigureAwait(false);
            data = await ApplyZoneSizesAsync(index, data, cancellationToken).ConfigureAwait(false);
            devices.Add(data);
            _ledCounts.Add(data.Leds.Count);

            client.SetCustomMode(index);
            var colors = new RgbColor[data.Leds.Count];
            colors.AsSpan().Fill(color);
            client.UpdateLeds(index, colors);

            if (_litDevices.Add(data.Name + "|" + data.Location))
            {
                _log.Information(
                    "First light {Device} ({Leds} LEDs) {ElapsedMs} ms after Service start; {Zones}",
                    data.Name, data.Leds.Count, (long)clock.Elapsed.TotalMilliseconds, Describe(data));
            }
        }

        Devices = devices;
        _log.Information("Applied {Color} to {Count} OpenRGB devices", color, count);
    }

    /// <summary>Best-effort black on every known device (stop and shutdown paths). Uses cached LED counts, no round trips.</summary>
    public void Blackout()
    {
        for (var index = 0; index < _ledCounts.Count; index++)
        {
            try
            {
                client.UpdateLeds(index, new RgbColor[_ledCounts[index]]);
            }
            catch (IOException ex)
            {
                _log.Warning("Could not send black to OpenRGB device {Index}: {Error}", index, ex.Message);
                return;
            }
        }

        _log.Information("Sent black to {Count} OpenRGB devices", _ledCounts.Count);
    }

    private async Task<ControllerData> ApplyZoneSizesAsync(int index, ControllerData data, CancellationToken cancellationToken)
    {
        var resized = false;
        for (var zoneIndex = 0; zoneIndex < data.Zones.Count; zoneIndex++)
        {
            var zone = data.Zones[zoneIndex];
            var wanted = openRgb.ZoneSizes.FirstOrDefault(z =>
                string.Equals(z.Device, data.Name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(z.Zone, zone.Name, StringComparison.OrdinalIgnoreCase));
            if (wanted is null || zone.LedsCount == wanted.Leds)
            {
                continue;
            }

            if (zone.LedsMin == zone.LedsMax || wanted.Leds < zone.LedsMin || wanted.Leds > zone.LedsMax)
            {
                _log.Warning("Zone {Device}/{Zone} cannot be resized to {Leds} (allowed {Min}-{Max})",
                    data.Name, zone.Name, wanted.Leds, zone.LedsMin, zone.LedsMax);
                continue;
            }

            _log.Information("Resizing zone {Device}/{Zone} from {From} to {To} LEDs", data.Name, zone.Name, zone.LedsCount, wanted.Leds);
            client.ResizeZone(index, zoneIndex, wanted.Leds);
            resized = true;
        }

        return resized
            ? await client.GetControllerDataAsync(index, cancellationToken).ConfigureAwait(false)
            : data;
    }

    private static string Describe(ControllerData data) =>
        string.Join(", ", data.Zones.Select(z => $"{z.Name}={z.LedsCount}"));
}
