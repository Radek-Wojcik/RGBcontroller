using System.ServiceProcess;
using RGBcontroller.Core;
using RGBcontroller.Core.Config;
using RGBcontroller.Core.Logging;
using Serilog;
using Serilog.Events;

namespace RGBcontroller.Service;

internal static class Program
{
    private const string Usage = """
        RGBcontroller.Service

          (no arguments)         Run as the Windows Service (started by the Service Control Manager).
          --console              Run the same lighting host in this console until Ctrl+C. Needs an elevated
                                 prompt and the Service stopped: it drives the hardware and replaces any OpenRGB.
          --probe [--rescan]     Read-only check of the running OpenRGB SDK server (spikes R2/R3);
                                 --rescan also asks OpenRGB to re-detect devices and times it.
          --data-dir <folder>    Use <folder> instead of %ProgramData%\RGBcontroller.
        """;

    private static async Task<int> Main(string[] args)
    {
        if (!TryParse(args, out var options))
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        var paths = options.DataDir is null ? AppPaths.Default : new AppPaths(Path.GetFullPath(options.DataDir));
        var store = new ConfigStore(paths.ConfigFile);
        var load = store.Load();

        if (options.Mode == Mode.Probe)
        {
            return await Probe.RunAsync(load.Config.OpenRgb, options.Rescan).ConfigureAwait(false);
        }

        if (options.Mode == Mode.None)
        {
            if (Environment.UserInteractive)
            {
                Console.Error.WriteLine(Usage);
                return 2;
            }
        }

        var console = options.Mode == Mode.Console;
        await using var logger = AppLogging.CreateLogger("service", paths.LogDirectory, load.Config.Logging.MinimumLevel, alsoWriteToConsole: console);
        var log = logger.ForContext(typeof(Program));
        AppDomain.CurrentDomain.UnhandledException += (_, e) => log.Fatal(e.ExceptionObject as Exception, "Unhandled exception");
        log.Information("RGBcontroller.Service {Version} starting ({Mode}), data folder {DataRoot}",
            typeof(Program).Assembly.GetName().Version, console ? "console" : "service", paths.DataRoot);
        LogConfigLoad(log, store, load);

        LightingHost CreateHost() => new(load.Config, paths, AppContext.BaseDirectory, logger);

        if (!console)
        {
            ServiceBase.Run(new RgbControllerService(CreateHost, logger));
            return 0;
        }

        await using var host = CreateHost();
        try
        {
            host.Start();
        }
        catch (InvalidOperationException ex)
        {
            log.Error("{Error}", ex.Message);
            return 1;
        }

        var stop = new TaskCompletionSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.TrySetResult();
        };
        log.Information("Running; press Ctrl+C to stop (sends black to all devices)");
        await stop.Task.ConfigureAwait(false);
        await host.StopAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
        return 0;
    }

    private static void LogConfigLoad(ILogger log, ConfigStore store, ConfigLoadResult load)
    {
        var problem = load.Outcome is ConfigLoadOutcome.RecoveredFromCorrupt
            or ConfigLoadOutcome.InvalidUsingDefaults or ConfigLoadOutcome.TooNewUsingDefaults;
        log.Information("Config {Outcome:l} from {ConfigFile}", load.Outcome.ToString(), store.FilePath);
        foreach (var message in load.Messages)
        {
            log.Write(problem ? LogEventLevel.Warning : LogEventLevel.Information, "Config: {Message:l}", message);
        }
    }

    private enum Mode
    {
        None,
        Console,
        Probe,
    }

    private sealed record Options(Mode Mode, bool Rescan, string? DataDir);

    private static bool TryParse(string[] args, out Options options)
    {
        var mode = Mode.None;
        var rescan = false;
        string? dataDir = null;
        options = new Options(mode, rescan, dataDir);
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--console" when mode == Mode.None:
                    mode = Mode.Console;
                    break;
                case "--probe" when mode == Mode.None:
                    mode = Mode.Probe;
                    break;
                case "--rescan":
                    rescan = true;
                    break;
                case "--data-dir" when i + 1 < args.Length:
                    dataDir = args[++i];
                    break;
                default:
                    return false;
            }
        }

        if (rescan && mode != Mode.Probe)
        {
            return false;
        }

        options = new Options(mode, rescan, dataDir);
        return true;
    }
}
