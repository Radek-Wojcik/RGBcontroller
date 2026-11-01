using RGBcontroller.Core;
using RGBcontroller.Core.Config;
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
internal sealed class LightingHost : IControllableHost, IAsyncDisposable
{
    private const string SingletonMutexName = @"Global\RGBcontroller.LightingHost";

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
    private OpenRgbLauncher? _launcher;
    private ControlServer? _control;
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
        _lighting = new LightingController(_config.Lighting, _strip, _config.Strip.LedCount, _log);
        _situation = new SituationTracker(ConsoleSession.Query, _lighting.SetSituation, _log);
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
        _supervisorTask = Task.Run(() => Supervisor.RunAsync(_stop.Token));

        _control = new ControlServer(IpcProtocol.ControlPipeName, new ServiceControlEndpoint(this), _log);
        _control.Start();
    }

    /// <summary>Sends black to the strip and all OpenRGB devices, then stops OpenRGB. Bounded by <paramref name="timeout"/>.</summary>
    public async Task StopAsync(TimeSpan timeout)
    {
        _log.Information("Lighting host stopping");
        _situation?.Dispose();

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
    }

    /// <summary>SERVICE_CONTROL_SESSIONCHANGE; the tracker re-reads the console session.</summary>
    public void OnSessionChange(string reason, int sessionId) => _situation?.OnSessionChange(reason, sessionId);

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
    private void Commit(AppConfig config)
    {
        _config = config;
        _lighting?.Apply(config.Lighting);
        if (!_canSaveConfig)
        {
            _log.Warning("Config file was rejected at start; the change is not saved");
            return;
        }

        try
        {
            _store.Save(config);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warning("Could not save the config: {Error}", ex.Message);
        }
    }

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
        if (state == StripState.Connected && !_stripLit)
        {
            _stripLit = true;
            _log.Information("First light Adalight strip on {Port} {ElapsedMs} ms after Service start",
                port, (long)_clock.Elapsed.TotalMilliseconds);
        }
    }
}
