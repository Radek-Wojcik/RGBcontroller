using RGBcontroller.Core;
using RGBcontroller.Core.Config;
using RGBcontroller.Core.Logging;
using Serilog;

namespace RGBcontroller.Service;

/// <summary>
/// Stage 0 skeleton: console mode only. Loads config and writes logs so the foundation can be checked
/// without touching hardware. The Windows Service host, OpenRGB supervisor and Adalight writer arrive in stage 1.
/// </summary>
internal static class Program
{
    private const string Usage = """
        RGBcontroller.Service (stage 0 skeleton)

          --console              Run in the console until Ctrl+C (no hardware access in stage 0).
          --data-dir <folder>    Use <folder> instead of %ProgramData%\RGBcontroller.
        """;

    private static int Main(string[] args)
    {
        if (!TryParse(args, out var console, out var dataDir) || !console)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        var paths = dataDir is null ? AppPaths.Default : new AppPaths(Path.GetFullPath(dataDir));
        var store = new ConfigStore(paths.ConfigFile);
        var load = store.Load();

        using var logger = AppLogging.CreateLogger("service", paths.LogDirectory, load.Config.Logging.MinimumLevel, alsoWriteToConsole: true);
        var log = logger.ForContext(typeof(Program));
        log.Information("RGBcontroller.Service {Version} starting in console mode, data folder {DataRoot}",
            typeof(Program).Assembly.GetName().Version, paths.DataRoot);
        log.Information("Config {Outcome} from {ConfigFile}", load.Outcome, store.FilePath);
        foreach (var message in load.Messages)
        {
            log.Warning("Config: {Message}", message);
        }

        log.Information("Strip: {LedCount} LEDs, {BaudRate} baud, max {MaxFps} FPS, port {Port}",
            load.Config.Strip.LedCount, load.Config.Strip.BaudRate, load.Config.Strip.MaxFps,
            load.Config.Strip.PortName ?? $"auto (VID {load.Config.Strip.UsbVendorId} PID {load.Config.Strip.UsbProductId})");

        using var stop = new ManualResetEventSlim();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Set();
        };
        log.Information("Running; press Ctrl+C to stop");
        stop.Wait();
        log.Information("Stopped");
        return 0;
    }

    private static bool TryParse(string[] args, out bool console, out string? dataDir)
    {
        console = false;
        dataDir = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--console":
                    console = true;
                    break;
                case "--data-dir" when i + 1 < args.Length:
                    dataDir = args[++i];
                    break;
                default:
                    return false;
            }
        }

        return true;
    }
}
