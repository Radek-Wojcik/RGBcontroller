using System.Diagnostics;
using RGBcontroller.Agent.Capture;
using RGBcontroller.Agent.Tray;
using RGBcontroller.Core.Config;
using RGBcontroller.Ipc;
using Serilog;

namespace RGBcontroller.Agent;

/// <summary>
/// The Agent's message loop (SPEC 7.1): tray icon and menu, global hotkeys, and the Screen capture, which runs only
/// while the Service says the strip mirrors the screen (Screen mode, unlocked, display on). The menu and the
/// brightness slider are created when the menu first opens; while idle the Agent only waits for messages.
/// </summary>
internal sealed class AgentApplication : ApplicationContext
{
    private static readonly (AmbilightMode Mode, string Label)[] StripModes =
    [
        (AmbilightMode.Screen, "Screen"),
        (AmbilightMode.Radial, "Radial rainbow"),
        (AmbilightMode.Solid, "Solid color"),
        (AmbilightMode.Off, "Off"),
    ];

    private readonly ILogger _log;
    private readonly ServiceConnection _service;
    private readonly HotkeyWindow _hotkeys;
    private readonly NotifyIcon _tray;
    private readonly Dictionary<TrayIconState, Icon> _icons = [];
    private readonly System.Windows.Forms.Timer _brightnessThrottle = new() { Interval = 80 };
    private ContextMenuStrip? _menu;
    private BrightnessSlider? _slider;
    private ToolStripControlHost? _sliderHost;
    private AgentState? _state;
    private AgentSettings? _settings;
    private ScreenCaptureLoop? _capture;
    private int? _pendingBrightness;

    public AgentApplication(ILogger log)
    {
        _log = log.ForContext<AgentApplication>();
        _service = new ServiceConnection(SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext(), log);
        _service.StateChanged += OnStateChanged;
        _service.SettingsChanged += OnSettingsChanged;
        _hotkeys = new HotkeyWindow(OnHotkey, log);
        _brightnessThrottle.Tick += (_, _) => FlushBrightness();
        _tray = new NotifyIcon { Icon = Icon(TrayIconState.Off), Text = "RGBcontroller: connecting to the Service", Visible = true };
        _tray.MouseUp += OnTrayMouseUp;
        _service.Start();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _log.Information("Agent exiting");
            StopCapture();
            _tray.Visible = false;
            _tray.Dispose();
            _menu?.Dispose();
            _hotkeys.Dispose();
            _brightnessThrottle.Dispose();
            _service.Dispose();
            foreach (var icon in _icons.Values)
            {
                icon.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    private void OnSettingsChanged(AgentSettings settings)
    {
        var captureChanged = _settings is null
            || _settings.LedCount != settings.LedCount || _settings.Layout != settings.Layout || _settings.Capture != settings.Capture;
        if (_settings is null || _settings.Hotkeys != settings.Hotkeys)
        {
            var failed = _hotkeys.Register(settings.Hotkeys.Bindings);
            if (failed.Count > 0)
            {
                _tray.ShowBalloonTip(5000, "RGBcontroller", $"Hotkeys taken by another program: {string.Join(", ", failed)}", ToolTipIcon.Warning);
            }
        }

        _settings = settings;
        if (captureChanged && _capture is not null)
        {
            StopCapture();
        }

        UpdateCapture();
    }

    private void OnStateChanged(AgentState? state)
    {
        _state = state;
        UpdateCapture();
        UpdateTray();
    }

    private void UpdateCapture()
    {
        var wanted = _state is { CaptureWanted: true } && _settings is not null;
        if (wanted && _capture is null)
        {
            _capture = new ScreenCaptureLoop(_settings!.Capture, _settings.Layout, _settings.LedCount, _service, _log);
            _capture.Start();
        }
        else if (!wanted && _capture is not null)
        {
            StopCapture();
        }
    }

    private void StopCapture()
    {
        var capture = _capture;
        _capture = null;
        capture?.Dispose();
    }

    private void UpdateTray()
    {
        var state = _state;
        if (state is null)
        {
            _tray.Icon = Icon(TrayIconState.Off);
            _tray.Text = "RGBcontroller: Service not running";
            return;
        }

        var healthy = state.Status.OpenRgb == ComponentState.Ok && state.Status.Strip == ComponentState.Ok;
        _tray.Icon = Icon(state.AllOff ? TrayIconState.Off : healthy ? TrayIconState.Ok : TrayIconState.Degraded);
        var text = state.AllOff
            ? "RGBcontroller: everything off"
            : $"RGBcontroller: {state.ShownProfile}, strip {state.StripMode} {state.StripBrightnessPercent}%"
              + (healthy ? "" : $" (OpenRGB {state.Status.OpenRgb}, strip {state.Status.Strip})");
        _tray.Text = text.Length > 127 ? text[..127] : text;
        if (_slider is not null && !_slider.Focused)
        {
            _slider.Value = state.StripBrightnessPercent;
        }
    }

    private Icon Icon(TrayIconState state)
    {
        if (!_icons.TryGetValue(state, out var icon))
        {
            _icons[state] = icon = TrayTheme.CreateIcon(state);
        }

        return icon;
    }

    private void OnTrayMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button is not (MouseButtons.Left or MouseButtons.Right))
        {
            return;
        }

        var created = _menu is null;
        _menu ??= CreateMenu();

        // NotifyIcon opens its ContextMenuStrip by itself on right click once it has one; the first click (the menu is
        // created lazily) and left clicks open it here, at the cursor like a right click.
        if (created || e.Button == MouseButtons.Left)
        {
            typeof(NotifyIcon).GetMethod("ShowContextMenu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.Invoke(_tray, null);
        }
    }

    private ContextMenuStrip CreateMenu()
    {
        var menu = new ContextMenuStrip
        {
            Renderer = TrayTheme.CreateRenderer(),
            BackColor = TrayTheme.Background,
            ForeColor = TrayTheme.Text,
            ShowImageMargin = false,
            ShowCheckMargin = true,
        };
        menu.Opening += (_, _) => Populate(menu);
        _tray.ContextMenuStrip = menu;
        return menu;
    }

    private void Populate(ContextMenuStrip menu)
    {
        menu.SuspendLayout();
        foreach (ToolStripItem item in menu.Items)
        {
            if (item != _sliderHost)
            {
                item.Dispose();
            }
        }

        menu.Items.Clear();
        var state = _state;
        var connected = state is not null;

        var ui = Path.Combine(AppContext.BaseDirectory, "RGBcontroller.UI.exe");
        var open = Item("Open RGBcontroller", File.Exists(ui), () => Launch(ui));
        open.Font = new Font(open.Font, FontStyle.Bold);
        menu.Items.Add(open);
        menu.Items.Add(new ToolStripSeparator());

        var profiles = Item("Profiles", connected, null);
        foreach (var name in state?.Profiles ?? [])
        {
            var profile = Item(name, true, () => _service.Send($"Profile {name}", s => s.SetActiveProfileAsync(name, CancellationToken.None)));
            profile.Checked = string.Equals(name, state!.ActiveProfile, StringComparison.OrdinalIgnoreCase);
            profiles.DropDownItems.Add(profile);
        }

        Theme(profiles.DropDown);
        menu.Items.Add(profiles);

        var strip = Item("Ambilight", connected, null);
        foreach (var (mode, label) in StripModes)
        {
            var item = Item(label, true, () => _service.Send($"Strip {mode}", s => s.ChangeStripAsync(new StripChangeRequest(Mode: mode), CancellationToken.None)));
            item.Checked = state?.StripMode == mode;
            strip.DropDownItems.Add(item);
        }

        Theme(strip.DropDown);
        menu.Items.Add(strip);

        menu.Items.Add(new ToolStripLabel("Strip brightness") { ForeColor = TrayTheme.DimText, Margin = new Padding(8, 4, 0, 0) });
        menu.Items.Add(BrightnessHost(state?.StripBrightnessPercent ?? 0, connected));
        menu.Items.Add(new ToolStripSeparator());

        var allOff = state?.AllOff == true;
        menu.Items.Add(Item(allOff ? "Turn everything on" : "Turn everything off", connected,
            () => _service.Send(allOff ? "All on" : "All off", s => s.SetAllOffAsync(!allOff, CancellationToken.None))));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Item("Exit Agent", true, ExitThread));
        menu.ResumeLayout();
    }

    private ToolStripControlHost BrightnessHost(int value, bool enabled)
    {
        if (_slider is null)
        {
            _slider = new BrightnessSlider { Size = new Size(200, 28) };
            _slider.ValueChanged += (_, _) =>
            {
                _pendingBrightness = _slider.Value;
                _brightnessThrottle.Start();
            };
        }

        _slider.Value = value;
        _slider.Enabled = enabled;
        return _sliderHost ??= new ToolStripControlHost(_slider) { AutoSize = false, Size = new Size(200, 28), Margin = new Padding(8, 0, 8, 4) };
    }

    private void FlushBrightness()
    {
        _brightnessThrottle.Stop();
        if (_pendingBrightness is { } value)
        {
            _pendingBrightness = null;
            _service.Send($"Strip brightness {value}%", s => s.ChangeStripAsync(new StripChangeRequest(BrightnessPercent: value), CancellationToken.None));
        }
    }

    private void OnHotkey(HotkeyBinding binding)
    {
        var step = _settings?.Hotkeys.BrightnessStepPercent ?? 10;
        Func<IServiceControl, Task<CommandResult>> command = binding.Action switch
        {
            HotkeyAction.StripSolidToggle => s => s.ChangeStripAsync(new StripChangeRequest(ToggleSolid: true), CancellationToken.None),
            HotkeyAction.StripBrightnessUp => s => s.ChangeStripAsync(new StripChangeRequest(BrightnessDelta: step), CancellationToken.None),
            HotkeyAction.StripBrightnessDown => s => s.ChangeStripAsync(new StripChangeRequest(BrightnessDelta: -step), CancellationToken.None),
            HotkeyAction.CycleStripMode => s => s.ChangeStripAsync(new StripChangeRequest(CycleMode: true), CancellationToken.None),
            HotkeyAction.NextProfile => s => s.StepProfileAsync(1, CancellationToken.None),
            HotkeyAction.PreviousProfile => s => s.StepProfileAsync(-1, CancellationToken.None),
            HotkeyAction.Profile => s => s.SetActiveProfileAsync(binding.Profile ?? "", CancellationToken.None),
            _ => s => s.SetAllOffAsync(_state?.AllOff != true, CancellationToken.None),
        };
        _service.Send($"Hotkey {binding.Keys}", command);
    }

    private void Launch(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _log.Warning("Could not start {Path}: {Error}", path, ex.Message);
        }
    }

    private static ToolStripMenuItem Item(string text, bool enabled, Action? click)
    {
        var item = new ToolStripMenuItem(text) { Enabled = enabled, ForeColor = TrayTheme.Text };
        if (click is not null)
        {
            item.Click += (_, _) => click();
        }

        return item;
    }

    private static void Theme(ToolStripDropDown dropDown)
    {
        dropDown.Renderer = TrayTheme.CreateRenderer();
        dropDown.BackColor = TrayTheme.Background;
        if (dropDown is ToolStripDropDownMenu menu)
        {
            menu.ShowImageMargin = false;
            menu.ShowCheckMargin = true;
        }
    }
}
