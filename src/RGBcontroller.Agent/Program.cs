using System.Globalization;
using RGBcontroller.Agent.Capture;
using RGBcontroller.Core;
using RGBcontroller.Core.Ambilight;
using RGBcontroller.Core.Config;
using RGBcontroller.Core.Logging;

namespace RGBcontroller.Agent;

/// <summary>
/// The per-user Agent (SPEC 3): tray icon, global hotkeys and the Screen mode capture. It never touches hardware;
/// strip colors and commands go to the Service over IPC. One instance per user session.
/// </summary>
internal static class Program
{
    private const string Usage = """
        RGBcontroller.Agent

          (no arguments)                        Tray icon, hotkeys and screen capture for this user session.
          --restart-after <pid> <count>         Used by the Agent's own watchdog; waits for <pid> to end, then starts normally.
          --capture-probe <seconds> <folder>    Run the screen pipeline without the Service and write a report
                                                and a snapshot with the sampled bands into <folder>.
        """;

    private static void WaitForExit(int processId, TimeSpan timeout)
    {
        try
        {
            using var previous = System.Diagnostics.Process.GetProcessById(processId);
            previous.WaitForExit(timeout);
        }
        catch (ArgumentException)
        {
            // Already gone.
        }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        // DuplicateOutput1 (HDR desktops) refuses processes that are not per-monitor DPI aware.
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        using var logger = AppLogging.CreateLogger("agent", AppPaths.UserLogDirectory);
        var log = logger.ForContext(typeof(Program));

        if (args.Length == 3 && args[0] == "--capture-probe" && int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
        {
            // Read-only: the config belongs to the Service; users may read it.
            var paths = AppPaths.Default;
            var config = File.Exists(paths.ConfigFile) ? new ConfigStore(paths.ConfigFile).Load().Config : AppConfig.CreateDefault();
            return CaptureProbe.Run(seconds, args[2], config, logger);
        }

        // Started by the watchdog of an Agent that found its capture thread stuck: wait until that process is gone.
        var restartsSoFar = 0;
        if (AgentRestart.TryParse(args, out var restart))
        {
            restartsSoFar = restart.Count;
            WaitForExit(restart.PreviousProcessId, TimeSpan.FromSeconds(30));
            args = [];
        }

        if (args.Length > 0)
        {
            MessageBox.Show(Usage, "RGBcontroller.Agent", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 2;
        }

        using var single = new Mutex(initiallyOwned: true, @"Local\RGBcontroller.Agent", out var first);
        if (!first)
        {
            log.Information("Another Agent already runs in this session; exiting");
            return 0;
        }

        log.Information("RGBcontroller.Agent {Version} starting", typeof(Program).Assembly.GetName().Version);
        if (restartsSoFar > 0)
        {
            log.Warning("Restarted by the capture watchdog (restart {Count} of {Max}); the previous Agent's capture thread was stuck", restartsSoFar, AgentRestart.MaxCount);
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.ThreadException += (_, e) => log.Error(e.Exception, "Unhandled UI exception");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => log.Fatal(e.ExceptionObject as Exception, "Unhandled exception");
        using var context = new AgentApplication(logger, restartsSoFar);
        Application.Run(context);
        return 0;
    }
}
