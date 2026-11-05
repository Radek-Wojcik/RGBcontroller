using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using RGBcontroller.Core;
using RGBcontroller.Ipc;
using Serilog;

namespace RGBcontroller.UI;

/// <summary>
/// The UI window (SPEC 3 and 8): a WebView2 that shows the web front end from the install folder under a private
/// host name, and a bridge from the page to the Service's control pipe. Nothing else is reachable: navigation away
/// from that host and new windows are blocked. Closing the window ends the process.
/// </summary>
public partial class MainWindow : Window
{
    private const string Host = "ui.rgbcontroller.example";
    private static readonly string Origin = $"https://{Host}/";

    private readonly ILogger _log;
    private readonly ServiceClient _client;
    private readonly Bridge _bridge;
    private readonly CancellationTokenSource _closing = new();
    private string? _connectionEvent;
    private string? _stateEvent;
    private bool _pageReady;

    public MainWindow(ILogger log)
    {
        InitializeComponent();
        _log = log.ForContext<MainWindow>();
        _client = new ServiceClient(log);
        _bridge = new Bridge(() => _client.Service);
        _client.ConnectionChanged += (version, error) =>
            Publish(ref _connectionEvent, Bridge.Event("connection", new { connected = version is not null, version, error }));
        _client.StateChanged += state => Publish(ref _stateEvent, Bridge.Event("state", state));
        SourceInitialized += (_, _) => UseDarkTitleBar();
        Loaded += async (_, _) => await StartAsync();
        Closed += (_, _) => _closing.Cancel();
    }

    /// <summary>Brings the window to the front (a second start of the UI asks the first one to show itself).</summary>
    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    private async Task StartAsync()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        if (!File.Exists(Path.Combine(root, "index.html")))
        {
            // The React front end is not built (no Node.js on this machine yet): show the bridge test page.
            root = Path.Combine(AppContext.BaseDirectory, "fallback");
        }

        try
        {
            var dataFolder = Path.Combine(Path.GetDirectoryName(AppPaths.UserLogDirectory)!, "webview2");
            var environment = await CoreWebView2Environment.CreateAsync(null, dataFolder);
            await Web.EnsureCoreWebView2Async(environment);
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or COMException or IOException or UnauthorizedAccessException)
        {
            _log.Error(ex, "WebView2 could not start");
            Message.Text = "The Microsoft Edge WebView2 Runtime is missing or failed to start.\n"
                + "Install it from https://go.microsoft.com/fwlink/p/?LinkId=2124703 and open RGBcontroller again.\n\n" + ex.Message;
            return;
        }

        var core = Web.CoreWebView2;
        var settings = core.Settings;
        settings.AreHostObjectsAllowed = false;
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.AreDefaultContextMenusEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
#if DEBUG
        settings.AreDevToolsEnabled = true;
#else
        settings.AreDevToolsEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
#endif
        core.SetVirtualHostNameToFolderMapping(Host, root, CoreWebView2HostResourceAccessKind.Deny);
        core.NavigationStarting += (_, e) =>
        {
            if (!e.Uri.StartsWith(Origin, StringComparison.OrdinalIgnoreCase))
            {
                _log.Warning("Blocked navigation to {Uri}", e.Uri);
                e.Cancel = true;
            }
        };
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.WebMessageReceived += OnWebMessage;
        core.NavigationCompleted += (_, e) =>
        {
            Web.Visibility = Visibility.Visible;
            Message.Visibility = Visibility.Collapsed;
            _pageReady = e.IsSuccess;
            Post(_connectionEvent);
            Post(_stateEvent);
        };
        core.ProcessFailed += (_, e) => _log.Warning("WebView2 process failed: {Kind}", e.ProcessFailedKind);
        core.Navigate(Origin + "index.html");
        _client.Start();
        _log.Information("UI started, front end from {Root}", root);
    }

    private async void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!e.Source.StartsWith(Origin, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string message;
        try
        {
            message = e.TryGetWebMessageAsString();
        }
        catch (ArgumentException)
        {
            return;
        }

        var reply = await _bridge.HandleAsync(message, _closing.Token);
        Post(reply);
    }

    /// <summary>Remembers the latest event of a kind (sent again after a reload) and posts it to the page.</summary>
    private void Publish(ref string? latest, string message)
    {
        latest = message;
        Dispatcher.BeginInvoke(() => Post(message));
    }

    private void Post(string? message)
    {
        if (message is not null && _pageReady && !_closing.IsCancellationRequested)
        {
            Web.CoreWebView2?.PostWebMessageAsString(message);
        }
    }

    private void UseDarkTitleBar()
    {
        var enabled = 1;
        _ = DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, DwmUseImmersiveDarkMode, ref enabled, sizeof(int));
    }

    private const int DwmUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
