using System.Globalization;
using System.ServiceProcess;
using RGBcontroller.Core;
using RGBcontroller.Core.Config;
using RGBcontroller.Core.Logging;
using RGBcontroller.Ipc;
using RGBcontroller.Service.Ipc;
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
          --status               Show the running Service's state and its profiles (control pipe, no elevation).
          --profile <name>       Switch the running Service to profile <name> and save it.
          --group <name> [--in <profile>] [--fps <1-60>] [--speed <cycles/s>] [--scale <x>] [--brightness <0-100>]
                                 Tune a group live and save it: the frame rate applies to the group in every
                                 profile, speed/scale/brightness to its effect in <profile> (default: the active one).
          --strip-test [<seconds>]
                                 Show the strip identification pattern (default 60 s, 0 ends it): the first 3 LEDs
                                 green, the last 3 red, every 10th white. Use it to fill in strip.layout.
          --strip-reopen         Close and reopen the strip's serial port (when the LED box stops reacting).
          --data-dir <folder>    Use <folder> instead of %ProgramData%\RGBcontroller.
        """;

    private static async Task<int> Main(string[] args)
    {
        if (!TryParse(args, out var options))
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        if (options.Mode is Mode.Status or Mode.SetProfile or Mode.TuneGroup or Mode.StripTest or Mode.StripReopen)
        {
            Func<IServiceControl, Task<CommandResult>>? command = options.Mode switch
            {
                Mode.SetProfile => service => service.SetActiveProfileAsync(options.Profile!, CancellationToken.None),
                Mode.TuneGroup => service => service.TuneGroupAsync(options.Tuning!, CancellationToken.None),
                Mode.StripTest => service => service.ShowStripTestAsync(options.Seconds, CancellationToken.None),
                Mode.StripReopen => service => service.ReopenStripAsync(CancellationToken.None),
                _ => null,
            };
            return await ControlClient.RunAsync(command).ConfigureAwait(false);
        }

        var paths = options.DataDir is null ? AppPaths.Default : new AppPaths(Path.GetFullPath(options.DataDir));
        var store = new ConfigStore(paths.ConfigFile);

        if (options.Mode == Mode.Probe)
        {
            // Read-only: never create the Service's data folder from a user prompt.
            var probeConfig = File.Exists(paths.ConfigFile) ? store.Load().Config : AppConfig.CreateDefault();
            return await Probe.RunAsync(probeConfig.OpenRgb, options.Rescan).ConfigureAwait(false);
        }

        var load = store.Load();

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

        // A rejected or too new file is never overwritten; the defaults then run in memory only.
        var canSaveConfig = load.Outcome is not (ConfigLoadOutcome.InvalidUsingDefaults or ConfigLoadOutcome.TooNewUsingDefaults);
        LightingHost CreateHost() => new(load.Config, store, canSaveConfig, paths, AppContext.BaseDirectory, logger);

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
        Status,
        SetProfile,
        TuneGroup,
        StripTest,
        StripReopen,
    }

    private sealed record Options(
        Mode Mode, bool Rescan, string? DataDir, string? Profile = null, GroupTuningRequest? Tuning = null, int Seconds = 60);

    private static bool TryParse(string[] args, out Options options)
    {
        var mode = Mode.None;
        var rescan = false;
        string? dataDir = null;
        string? profile = null;
        GroupTuningRequest? tuning = null;
        var seconds = 60;
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
                case "--status" when mode == Mode.None:
                    mode = Mode.Status;
                    break;
                case "--profile" when mode == Mode.None && i + 1 < args.Length:
                    mode = Mode.SetProfile;
                    profile = args[++i];
                    break;
                case "--group" when mode == Mode.None && i + 1 < args.Length:
                    mode = Mode.TuneGroup;
                    tuning = new GroupTuningRequest(args[++i]);
                    break;
                case "--strip-reopen" when mode == Mode.None:
                    mode = Mode.StripReopen;
                    break;
                case "--strip-test" when mode == Mode.None:
                    mode = Mode.StripTest;
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var duration))
                    {
                        seconds = duration;
                        i++;
                    }

                    break;
                case "--in" when tuning is not null && i + 1 < args.Length:
                    tuning = tuning with { Profile = args[++i] };
                    break;
                case "--fps" when tuning is not null && i + 1 < args.Length && int.TryParse(args[i + 1], out var fps):
                    tuning = tuning with { MaxFps = fps };
                    i++;
                    break;
                case "--brightness" when tuning is not null && i + 1 < args.Length && int.TryParse(args[i + 1], out var brightness):
                    tuning = tuning with { BrightnessPercent = brightness };
                    i++;
                    break;
                case "--speed" when tuning is not null && i + 1 < args.Length && TryParseNumber(args[i + 1], out var speed):
                    tuning = tuning with { Speed = speed };
                    i++;
                    break;
                case "--scale" when tuning is not null && i + 1 < args.Length && TryParseNumber(args[i + 1], out var scale):
                    tuning = tuning with { Scale = scale };
                    i++;
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

        options = new Options(mode, rescan, dataDir, profile, tuning, seconds);
        return true;
    }

    /// <summary>Accepts "0.3" and the Polish "0,3".</summary>
    private static bool TryParseNumber(string text, out double value) =>
        double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
