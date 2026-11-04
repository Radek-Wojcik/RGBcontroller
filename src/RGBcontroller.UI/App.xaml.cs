using System.Windows;
using System.Windows.Threading;
using RGBcontroller.Core;
using RGBcontroller.Core.Logging;
using Serilog.Core;

namespace RGBcontroller.UI;

/// <summary>
/// The on-demand UI (SPEC 3): one instance per user session, started from the tray or the Start menu, never
/// resident. A second start shows the running window and exits. Closing the window ends the process.
/// </summary>
public partial class App : Application
{
    private const string InstanceName = @"Local\RGBcontroller.UI";

    private Logger? _logger;
    private Mutex? _instance;
    private EventWaitHandle? _show;
    private RegisteredWaitHandle? _showWait;
    private bool _ownsInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _show = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + ".Show");
        _instance = new Mutex(initiallyOwned: true, InstanceName, out var first);
        if (!first)
        {
            _show.Set();
            Shutdown();
            return;
        }

        _ownsInstance = true;

        _logger = AppLogging.CreateLogger("ui", AppPaths.UserLogDirectory);
        var log = _logger.ForContext<App>();
        log.Information("RGBcontroller.UI {Version} starting", typeof(App).Assembly.GetName().Version);
        DispatcherUnhandledException += (_, args) => log.Error(args.Exception, "Unhandled UI exception");

        var window = new MainWindow(_logger);
        MainWindow = window;
        window.Show();
        _showWait = ThreadPool.RegisterWaitForSingleObject(_show, (_, _) => Dispatcher.BeginInvoke(window.BringToFront), null, Timeout.Infinite, false);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showWait?.Unregister(null);
        _logger?.ForContext<App>().Information("UI exiting");
        _logger?.Dispose();
        _show?.Dispose();
        if (_ownsInstance)
        {
            _instance!.ReleaseMutex();
        }

        _instance?.Dispose();
        base.OnExit(e);
    }
}
