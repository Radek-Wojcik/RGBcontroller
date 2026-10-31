using System.Diagnostics;
using RGBcontroller.Core.Config;
using RGBcontroller.Core.OpenRgb;

namespace RGBcontroller.Service;

/// <summary>
/// Diagnostic for spikes R2/R3 on the real machine: connects a second SDK client to the running (supervised)
/// OpenRGB, prints every device as our parser reads it and, with --rescan, measures a device rescan.
/// It never starts OpenRGB and writes no colors.
/// </summary>
internal static class Probe
{
    public static async Task<int> RunAsync(OpenRgbConfig config, bool rescan)
    {
        try
        {
            return await RunCoreAsync(config, rescan).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or TimeoutException or IOException
            or OperationCanceledException or OpenRgbProtocolException)
        {
            Console.Error.WriteLine($"Probe failed for OpenRGB SDK at {config.Host}:{config.Port}: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> RunCoreAsync(OpenRgbConfig config, bool rescan)
    {
        await using var client = await OpenRgbSdkClient.ConnectAsync(
            config.Host, config.Port, "RGBcontroller probe", TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        Console.WriteLine($"Connected to {config.Host}:{config.Port}: server protocol {client.ServerProtocolVersion}, using {client.ProtocolVersion}");
        var before = await PrintDevicesAsync(client).ConfigureAwait(false);
        if (!rescan)
        {
            return 0;
        }

        var notices = 0;
        var lastNotice = Stopwatch.StartNew();
        client.DeviceListUpdated += () =>
        {
            Interlocked.Increment(ref notices);
            lastNotice.Restart();
        };

        Console.WriteLine();
        Console.WriteLine("Requesting rescan (packet 140)...");
        var total = Stopwatch.StartNew();
        client.RequestRescan();
        var quiet = TimeSpan.FromSeconds(3);
        while (total.Elapsed < TimeSpan.FromSeconds(60) && (Volatile.Read(ref notices) == 0 || lastNotice.Elapsed < quiet))
        {
            await Task.Delay(100).ConfigureAwait(false);
        }

        var settled = total.Elapsed - (Volatile.Read(ref notices) == 0 ? TimeSpan.Zero : lastNotice.Elapsed);
        Console.WriteLine($"DEVICE_LIST_UPDATED notices: {notices}; last one {settled.TotalSeconds:0.0} s after the request");
        var after = await PrintDevicesAsync(client).ConfigureAwait(false);
        var same = before.Order().SequenceEqual(after.Order());
        Console.WriteLine(same ? "Rescan OK: same devices before and after." : "Rescan CHANGED the device set.");
        return same ? 0 : 1;
    }

    private static async Task<List<string>> PrintDevicesAsync(OpenRgbSdkClient client)
    {
        var count = await client.GetControllerCountAsync().ConfigureAwait(false);
        var keys = new List<string>(count);
        Console.WriteLine($"{count} device(s):");
        for (var i = 0; i < count; i++)
        {
            var d = await client.GetControllerDataAsync(i).ConfigureAwait(false);
            keys.Add(d.Name + "|" + d.Location);
            Console.WriteLine($"  [{i}] {d.Name} ({d.Vendor}) at {d.Location}");
            Console.WriteLine($"      LEDs {d.Leds.Count}, active mode {d.ActiveMode}, modes: {string.Join(", ", d.Modes.Select(m => m.Name))}");
            foreach (var z in d.Zones)
            {
                var matrix = z.Matrix.Count > 0 ? $", matrix {z.MatrixHeight}x{z.MatrixWidth}" : "";
                Console.WriteLine($"      zone {z.Name}: {z.LedsCount} LEDs (min {z.LedsMin}, max {z.LedsMax}){matrix}, segments {z.Segments.Count}, flags 0x{z.Flags:X}");
            }
        }

        return keys;
    }
}
