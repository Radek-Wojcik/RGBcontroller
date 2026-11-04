using RGBcontroller.Core;
using RGBcontroller.Core.Config;
using RGBcontroller.Core.Ambilight;
using RGBcontroller.Core.Lighting;
using RGBcontroller.Core.Strip;
using RGBcontroller.Ipc;
using RGBcontroller.Service.Hardware;
using RGBcontroller.Service.Ipc;
using RGBcontroller.Service.Lighting;
using RGBcontroller.Service.OpenRgb;
using RGBcontroller.Service.Power;
using Serilog;

namespace RGBcontroller.Service;

/// <summary>
/// Composition root shared by the Windows Service and console mode. Start order follows SPEC 3 / R5:
/// the strip lights immediately, OpenRGB (detection takes about 6 s) comes second. Stop sends black everywhere.
/// </summary>
internal sealed class LightingHost : IControllableHost, IScreenFeedSink, IAsyncDisposable
{
    private const string SingletonMutexName = @"Global\RGBcontroller.LightingHost";

    /// <summary>Strip changes (tray slider, hotkeys) come in bursts; the config is written once they stop.</summary>
    private static readonly TimeSpan DeferredSaveDelay = TimeSpan.FromSeconds(2);

    private static readonly AmbilightMode[] ModeCycle = [AmbilightMode.Screen, AmbilightMode.Radial, AmbilightMode.Solid, AmbilightMode.Off];

    private readonly ConfigStore _store;
    private readonly bool _canSaveConfig;
    private readonly string _installDirectory;
    private readonly AppPaths _paths;
    private readonly ILogger _log;
    private readonly StartupClock _clock = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _configLock = new();
    private AppConfig _config;
    private Mutex? _singleton;
    private StripWriter? _strip;
    private LightingController? _lighting;
    private SituationTracker? _situation;
    private UsbArrivalMonitor? _usb;
    private OpenRgbLauncher? _launcher;
    private ControlServer? _control;
    private FrameServer? _frames;
    private Timer? _deferredSave;
    private Timer? _stripTestEnd;
    private bool _savePending;
    private bool _allOff;
    private bool _screenLive;
    private AmbilightMode? _modeBeforeSolid;
    private AmbilightMode? _modeBeforeOff;
    private long _stateSequence;
    private long _configRevision = 1;
    private long _settingsRevision = 1;
    private Task _supervisorTask = Task.CompletedTask;
    private bool _stripLit;
    private bool _suspended;
    private long _lastResumeTicks = long.MinValue / 2;

    /// <param name="canSaveConfig">False when the file on disk was rejected and defaults run in memory; the file is then never overwritten.</param>
    public LightingHost(AppConfig config, ConfigStore store, bool canSaveConfig, AppPaths paths, string installDirectory, ILogger log)
    {
        _config = config;
        _store = store;
        _canSaveConfig = canSaveConfig;
        _paths = paths;
        _installDirectory = installDirectory;
        _log = log.ForContext<LightingHost>();
    }

    public OpenRgbSupervisor? Supervisor { get; private set; }

    /// <exception cref="InvalidOperationException">Another Service or console instance already owns the hardware.</exception>
    public void Start()
    {
        _singleton = new Mutex(initiallyOwned: false, SingletonMutexName);
        try
        {
            if (!_singleton.WaitOne(0))
            {
                throw new InvalidOperationException("Another RGBcontroller Service or console instance is already running.");
            }
        }
        catch (AbandonedMutexException)
        {
            _log.Warning("Previous RGBcontroller instance ended without releasing the hardware lock");
        }

        _log.Information("Lighting host starting at {StartedAt:HH:mm:ss.fff}, system uptime {Uptime:l}",
            _clock.StartedAt, TimeSpan.FromMilliseconds(Environment.TickCount64).ToString(@"hh\:mm\:ss"));

        if (_config.Strip.Enabled)
        {
            _strip = new StripWriter(_config.Strip, new UsbSerialPortProvider(_config.Strip), _log);
            _strip.StateChanged += OnStripStateChanged;
        }

        // The render loop gives the strip its first frame before the port is even open; OpenRGB devices join later.
        // The session is read first so that frame already shows the logon, lock or active profile.
        _lighting = new LightingController(_config.Lighting, _strip, _config.Strip, _log);
        _situation = new SituationTracker(ConsoleSession.Query, OnSituationChanged, _log);
        _lighting.SetSituation(_situation.Current);
        _lighting.Start();
        _situation.Start();
        if (_strip is not null)
        {
            _strip.Start();
            _log.Information("Strip writer started: {Leds} LEDs, max {Fps} FPS", _config.Strip.LedCount, _strip.EffectiveMaxFps);
        }

        var executable = Path.GetFullPath(Path.Combine(_installDirectory, _config.OpenRgb.ExecutablePath));
        _launcher = new OpenRgbLauncher(executable, _paths.OpenRgbConfigDirectory, _config.OpenRgb, _log);
        Supervisor = new OpenRgbSupervisor(_launcher, _config.OpenRgb, _lighting, _clock, _log);
        Supervisor.StateChanged += _ => PublishState();
        _supervisorTask = Task.Run(() => Supervisor.RunAsync(_stop.Token));
        StartUsbMonitor(Supervisor);

        _deferredSave = new Timer(_ => SaveDeferred());
        _stripTestEnd = new Timer(_ => ShowStripTest(TimeSpan.Zero));
        _control = new ControlServer(IpcProtocol.ControlPipeName, subscription => new ServiceControlEndpoint(this, subscription), _log);
        StateChanged += _control.Broadcast;
        _control.Start();
        if (_strip is not null)
        {
            _frames = new FrameServer(IpcProtocol.FramePipeName, _config.Strip.LedCount, this, _log);
            _frames.Start();
        }
    }

    /// <summary>
    /// USB arrivals start a rescan in OpenRGB (SPEC 4 hot-plug). The strip's own USB-serial chip is skipped: the strip
    /// writer finds its port by itself and OpenRGB does not drive it.
    /// </summary>
    private void StartUsbMonitor(OpenRgbSupervisor supervisor)
    {
        var strip = $"VID_{_config.Strip.UsbVendorId}&PID_{_config.Strip.UsbProductId}";
        _usb = new UsbArrivalMonitor(
            link =>
            {
                if (!link.Contains(strip, StringComparison.OrdinalIgnoreCase))
                {
                    _log.Information("USB device arrived: {Device}", link);
                    supervisor.NotifyUsbDeviceArrived();
                }
            },
            ex => _log.Error(ex, "USB arrival handler failed"));
        try
        {
            _usb.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            _log.Warning("USB hot-plug notifications unavailable: {Error}", ex.Message);
            _usb = null;
        }
    }

    /// <summary>Sends black to the strip and all OpenRGB devices, then stops OpenRGB. Bounded by <paramref name="timeout"/>.</summary>
    public async Task StopAsync(TimeSpan timeout)
    {
        _log.Information("Lighting host stopping");
        _situation?.Dispose();
        _usb?.Dispose();

        // No effect frame may follow the black frames below.
        _lighting?.Stop();
        await _stop.CancelAsync().ConfigureAwait(false);
        var work = Task.WhenAll(_supervisorTask, _strip?.StopAsync() ?? Task.CompletedTask);
        if (await Task.WhenAny(work, Task.Delay(timeout)).ConfigureAwait(false) != work)
        {
            _log.Warning("Stop did not finish within {Timeout} s; OpenRGB is terminated with the job object", timeout.TotalSeconds);
        }

        _launcher?.Dispose();
        if (_control is not null)
        {
            await _control.DisposeAsync().ConfigureAwait(false);
        }

        if (_frames is not null)
        {
            await _frames.DisposeAsync().ConfigureAwait(false);
        }

        _deferredSave?.Dispose();
        _stripTestEnd?.Dispose();
        SaveDeferred();

        _log.Information("Lighting host stopped");
    }

    public async ValueTask DisposeAsync()
    {
        if (!_stop.IsCancellationRequested)
        {
            await StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }

        if (_strip is not null)
        {
            await _strip.DisposeAsync().ConfigureAwait(false);
        }

        _lighting?.Dispose();
        _stop.Dispose();
        _singleton?.Dispose();
    }

    /// <summary>Power event PBT_APMSUSPEND: black first, the time budget is small (SPEC 4).</summary>
    public void OnSuspend()
    {
        _suspended = true;
        var sent = _lighting?.Suspend(TimeSpan.FromMilliseconds(800)) ?? true;
        _log.Information("Suspending: black sent to all devices{Timeout}", sent ? "" : " (timed out)");
        PublishState();
    }

    /// <summary>
    /// Power events PBT_APMRESUMEAUTOMATIC and PBT_APMRESUMESUSPEND (both arrive after one wake-up when a user is present):
    /// handled once per suspend, or again only if the last resume is more than 10 s old.
    /// </summary>
    public void OnResume(string reason)
    {
        var now = Environment.TickCount64;
        if (!_suspended && now - _lastResumeTicks < 10_000)
        {
            return;
        }

        _suspended = false;
        _lastResumeTicks = now;
        _log.Information("Resume ({Reason}): strip back now, OpenRGB devices after the rescan", reason);
        _situation?.Refresh("resume");
        _lighting?.Resume();
        Supervisor?.NotifyResumed();
        PublishState();
    }

    /// <summary>SERVICE_CONTROL_SESSIONCHANGE; the tracker re-reads the console session.</summary>
    public void OnSessionChange(string reason, int sessionId) => _situation?.OnSessionChange(reason, sessionId);

    public ConfigSnapshot GetConfig()
    {
        lock (_configLock)
        {
            return new ConfigSnapshot(_configRevision, _config);
        }
    }

    public ConfigUpdateResult UpdateConfig(long expectedRevision, AppConfig proposed)
    {
        lock (_configLock)
        {
            if (expectedRevision != _configRevision)
            {
                return new ConfigUpdateResult(false, "The configuration changed meanwhile; reload it and try again.", _configRevision, [], false);
            }

            var current = _config;
            var config = ProtectConfig(current, proposed);
            var errors = ConfigValidator.Validate(config);
            if (errors.Count > 0)
            {
                return new ConfigUpdateResult(false, "The configuration is not valid.", _configRevision, errors, false);
            }

            var restart = NeedsRestart(current, config);
            if (config.Strip.Layout != current.Strip.Layout)
            {
                _lighting?.SetStripLayout(new StripLayout(config.Strip.Layout, current.Strip.LedCount));
            }

            if (config != current)
            {
                Commit(config);
                _log.Information("Configuration changed by a client (revision {Revision}){Restart}", _configRevision,
                    restart ? "; some changes apply after a Service restart" : "");
            }

            return new ConfigUpdateResult(true, null, _configRevision, [], restart);
        }
    }

    /// <summary>
    /// What a client may not change over IPC: the schema version, and the OpenRGB executable the Service starts as
    /// LocalSystem (SPEC 9: no process launch).
    /// </summary>
    internal static AppConfig ProtectConfig(AppConfig current, AppConfig proposed) =>
        proposed with
        {
            SchemaVersion = current.SchemaVersion,
            OpenRgb = proposed.OpenRgb with { ExecutablePath = current.OpenRgb.ExecutablePath },
        };

    /// <summary>Changes the running Service cannot apply: strip hardware (layout aside), OpenRGB and logging.</summary>
    internal static bool NeedsRestart(AppConfig current, AppConfig next) =>
        next.Strip with { Layout = current.Strip.Layout } != current.Strip
        || next.OpenRgb != current.OpenRgb
        || next.Logging != current.Logging;

    public DeviceList GetDevices()
    {
        var devices = _lighting?.Devices ?? [];
        var list = devices.Select((d, i) => new DeviceInfo(i, d.Name, d.Type,
            [.. d.Zones.Select(z => new ZoneInfo(z.Name, z.Type, (int)z.LedsCount, z.LedsMin != z.LedsMax, (int)z.MatrixWidth, (int)z.MatrixHeight))])).ToList();
        return new DeviceList(list, CurrentConfig.Strip.Enabled ? CurrentConfig.Strip.LedCount : 0);
    }

    public ServiceStatus GetStatus()
    {
        var strip = _strip?.State switch
        {
            StripState.Connected => ComponentState.Ok,
            StripState.Settling => ComponentState.Starting,
            StripState.Searching => ComponentState.Degraded,
            _ => ComponentState.Offline,
        };
        var openRgb = Supervisor?.State ?? ComponentState.Unknown;
        var detail = $"strip {_strip?.PortName ?? "-"}, OpenRGB starts {Supervisor?.Starts ?? 0}";
        return new ServiceStatus(openRgb, strip, detail, CurrentConfig.Lighting.ActiveProfile);
    }

    public AgentState GetAgentState()
    {
        var lighting = CurrentConfig.Lighting;
        var ambilight = lighting.FindProfile(lighting.ActiveProfile)?.Ambilight ?? new AmbilightConfig();
        var situation = _situation?.Current ?? LightingSituation.Default;
        return new AgentState(
            Interlocked.Increment(ref _stateSequence),
            GetStatus(),
            [.. lighting.Profiles.Select(p => p.Name)],
            lighting.ActiveProfile,
            SessionPolicy.ProfileFor(lighting, situation.Session),
            ambilight.Mode,
            ambilight.Mode == AmbilightMode.Solid ? ambilight.SolidBrightnessPercent : ambilight.BrightnessPercent,
            _lighting?.IsScreenWanted ?? false,
            _allOff,
            Interlocked.Read(ref _settingsRevision),
            Interlocked.Read(ref _configRevision));
    }

    public AgentSettings GetAgentSettings()
    {
        var config = CurrentConfig;
        return new AgentSettings(config.Strip.LedCount, config.Strip.Layout, config.Capture, config.Hotkeys);
    }

    public string? ChangeStrip(StripChangeRequest request)
    {
        lock (_configLock)
        {
            var lighting = _config.Lighting;
            var profile = lighting.FindProfile(lighting.ActiveProfile);
            if (profile is null)
            {
                return $"The active profile '{lighting.ActiveProfile}' does not exist.";
            }

            var before = profile.Ambilight;
            var mode = NextStripMode(before.Mode, request, ref _modeBeforeSolid, ref _modeBeforeOff);

            var solid = mode == AmbilightMode.Solid;
            var brightness = solid ? before.SolidBrightnessPercent : before.BrightnessPercent;
            brightness = Math.Clamp(request.BrightnessPercent ?? brightness + (request.BrightnessDelta ?? 0), 0, 100);
            var after = before with
            {
                Mode = mode,
                SolidBrightnessPercent = solid ? brightness : before.SolidBrightnessPercent,
                BrightnessPercent = solid ? before.BrightnessPercent : brightness,
            };
            if (after == before)
            {
                return null;
            }

            var profiles = lighting.Profiles.Select(p => ReferenceEquals(p, profile) ? p with { Ambilight = after } : p).ToList();
            Commit(_config with { Lighting = lighting with { Profiles = profiles } }, deferSave: true);
            _log.Information("Strip of {Profile}: {Mode} at {Brightness}%", profile.Name, mode, brightness);
            return null;
        }
    }

    /// <summary>
    /// The strip mode after <paramref name="request"/>. Solid toggles back to the mode before it. Raising the
    /// brightness of a strip that is Off (hotkey or tray slider) turns it back on in the mode it had before, because a
    /// brightness change on a dark strip looks like the Agent stopped responding (stage 4 gate).
    /// </summary>
    internal static AmbilightMode NextStripMode(
        AmbilightMode current, StripChangeRequest request, ref AmbilightMode? modeBeforeSolid, ref AmbilightMode? modeBeforeOff)
    {
        var mode = request.Mode ?? current;
        if (request.ToggleSolid)
        {
            if (mode == AmbilightMode.Solid)
            {
                mode = modeBeforeSolid ?? AmbilightMode.Screen;
            }
            else
            {
                modeBeforeSolid = mode;
                mode = AmbilightMode.Solid;
            }
        }

        if (request.CycleMode)
        {
            mode = ModeCycle[(Array.IndexOf(ModeCycle, mode) + 1) % ModeCycle.Length];
        }

        var brighter = request.BrightnessPercent > 0 || request.BrightnessDelta > 0;
        if (mode == AmbilightMode.Off && current == AmbilightMode.Off && request is { Mode: null, ToggleSolid: false, CycleMode: false } && brighter)
        {
            mode = modeBeforeOff ?? AmbilightMode.Screen;
        }

        if (mode == AmbilightMode.Off && current != AmbilightMode.Off)
        {
            modeBeforeOff = current;
        }

        return mode;
    }

    public void SetAllOff(bool off)
    {
        lock (_configLock)
        {
            if (_allOff == off)
            {
                return;
            }

            _allOff = off;
            _lighting?.SetAllOff(off);
        }

        _log.Information("Everything {State} (tray or hotkey)", off ? "off" : "back on");
        PublishState();
    }

    public string? StepProfile(int delta)
    {
        lock (_configLock)
        {
            var profiles = _config.Lighting.Profiles;
            if (profiles.Count == 0)
            {
                return "There are no profiles.";
            }

            var current = profiles.ToList().FindIndex(p => string.Equals(p.Name, _config.Lighting.ActiveProfile, StringComparison.OrdinalIgnoreCase));
            var next = ((current + delta) % profiles.Count + profiles.Count) % profiles.Count;
            return SetActiveProfile(profiles[next].Name);
        }
    }

    public void ShowStripTest(TimeSpan duration)
    {
        var on = duration > TimeSpan.Zero;
        _lighting?.SetStripTest(on);
        _stripTestEnd?.Change(on ? duration : Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _log.Information(on ? "Strip test pattern for {Seconds} s" : "Strip test pattern ended", duration.TotalSeconds);
    }

    public void OnScreenColors(ReadOnlySpan<RgbColor> colors)
    {
        if (!_screenLive)
        {
            _screenLive = true;
            _lighting?.SetScreenFeed(true);
            _log.Information("Strip mirrors the screen (Agent capture)");
        }

        _lighting?.PushScreenFrame(colors);
    }

    public void OnScreenUnavailable()
    {
        if (_screenLive)
        {
            _screenLive = false;
            _lighting?.SetScreenFeed(false);
            _log.Information("Screen colors stopped; the strip shows its fallback");
        }
    }

    /// <summary>Raised with the new state after anything the tray or the capture depends on changed.</summary>
    public event Action<AgentState>? StateChanged;

    public ProfileList GetProfiles()
    {
        var lighting = CurrentConfig.Lighting;
        return new ProfileList([.. lighting.Profiles.Select(p => p.Name)], lighting.ActiveProfile);
    }

    public string? SetActiveProfile(string name)
    {
        lock (_configLock)
        {
            var profile = _config.Lighting.FindProfile(name);
            if (profile is null)
            {
                return $"No profile named '{name}'.";
            }

            Commit(_config with { Lighting = _config.Lighting with { ActiveProfile = profile.Name } });
            _log.Information("Active profile set to {Profile}", profile.Name);
            return null;
        }
    }

    public string? TuneGroup(GroupTuningRequest request)
    {
        lock (_configLock)
        {
            var tuning = new GroupTuning(request.Group, request.Profile, request.MaxFps, request.Speed, request.Scale, request.BrightnessPercent);
            var lighting = LightingEdits.Apply(_config.Lighting, tuning, out var error);
            if (lighting is null)
            {
                return error;
            }

            var config = _config with { Lighting = lighting };
            var errors = ConfigValidator.Validate(config);
            if (errors.Count > 0)
            {
                return string.Join(" ", errors);
            }

            Commit(config);
            _log.Information("Tuned group {Group} in {Profile}: fps {Fps}, speed {Speed}, scale {Scale}, brightness {Brightness}",
                request.Group, request.Profile ?? lighting.ActiveProfile, request.MaxFps, request.Speed, request.Scale, request.BrightnessPercent);
            return null;
        }
    }

    /// <summary>Applies <paramref name="config"/> to the running engine and saves it. Call under the config lock.</summary>
    /// <param name="deferSave">Save after <see cref="DeferredSaveDelay"/> without further changes (slider drags, hotkey repeats).</param>
    private void Commit(AppConfig config, bool deferSave = false)
    {
        var before = _config;
        _config = config;
        Interlocked.Increment(ref _configRevision);
        if (before.Strip.LedCount != config.Strip.LedCount || before.Strip.Layout != config.Strip.Layout
            || before.Capture != config.Capture || before.Hotkeys != config.Hotkeys)
        {
            Interlocked.Increment(ref _settingsRevision);
        }

        _lighting?.Apply(config.Lighting);
        PublishState();
        if (!_canSaveConfig)
        {
            _log.Warning("Config file was rejected at start; the change is not saved");
            return;
        }

        if (deferSave && _deferredSave is not null)
        {
            _savePending = true;
            _deferredSave.Change(DeferredSaveDelay, Timeout.InfiniteTimeSpan);
            return;
        }

        _savePending = false;
        Save(config);
    }

    private void SaveDeferred()
    {
        lock (_configLock)
        {
            if (_savePending)
            {
                _savePending = false;
                Save(_config);
            }
        }
    }

    private void Save(AppConfig config)
    {
        try
        {
            _store.Save(config);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warning("Could not save the config: {Error}", ex.Message);
        }
    }

    private void OnSituationChanged(LightingSituation situation)
    {
        _lighting?.SetSituation(situation);
        PublishState();
    }

    private void PublishState() => StateChanged?.Invoke(GetAgentState());

    private AppConfig CurrentConfig
    {
        get
        {
            lock (_configLock)
            {
                return _config;
            }
        }
    }

    private void OnStripStateChanged(StripState state, string? port)
    {
        PublishState();
        if (state == StripState.Connected && !_stripLit)
        {
            _stripLit = true;
            _log.Information("First light Adalight strip on {Port} {ElapsedMs} ms after Service start",
                port, (long)_clock.Elapsed.TotalMilliseconds);
        }
    }
}
