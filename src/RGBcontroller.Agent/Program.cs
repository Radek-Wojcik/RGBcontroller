using RGBcontroller.Core;
using RGBcontroller.Core.Logging;

namespace RGBcontroller.Agent;

/// <summary>
/// Stage 0 skeleton. The tray icon, hotkeys and the ambilight capture pipeline arrive in stage 4.
/// The Agent never touches hardware; it talks to the Service over IPC.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main()
    {
        using var logger = AppLogging.CreateLogger("agent", AppPaths.UserLogDirectory);
        logger.ForContext(typeof(Program)).Information("RGBcontroller.Agent {Version} skeleton started and exiting",
            typeof(Program).Assembly.GetName().Version);
        return 0;
    }
}
