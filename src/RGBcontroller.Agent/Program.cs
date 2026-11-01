using System.Globalization;
using RGBcontroller.Agent.Capture;
using RGBcontroller.Core;
using RGBcontroller.Core.Config;
using RGBcontroller.Core.Logging;

namespace RGBcontroller.Agent;

/// <summary>
/// The per-user Agent (SPEC 3): tray icon, global hotkeys and the Screen mode capture. It never touches hardware;
/// strip colors and commands go to the Service over IPC.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // DuplicateOutput1 (HDR desktops) refuses processes that are not per-monitor DPI aware.
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        using var logger = AppLogging.CreateLogger("agent", AppPaths.UserLogDirectory);
        var log = logger.ForContext(typeof(Program));
        log.Information("RGBcontroller.Agent {Version} starting", typeof(Program).Assembly.GetName().Version);

        if (args.Length == 3 && args[0] == "--capture-probe" && int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
        {
            var paths = AppPaths.Default;
            var config = File.Exists(paths.ConfigFile) ? new ConfigStore(paths.ConfigFile).Load().Config : AppConfig.CreateDefault();
            return CaptureProbe.Run(seconds, args[2], config, logger);
        }

        return 0;
    }
}
