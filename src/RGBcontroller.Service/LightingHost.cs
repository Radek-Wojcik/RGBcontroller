using RGBcontroller.Core;
using RGBcontroller.Core.Config;
using RGBcontroller.Core.Strip;
using RGBcontroller.Ipc;
using RGBcontroller.Service.Hardware;
using RGBcontroller.Service.Ipc;
using RGBcontroller.Service.Lighting;
using RGBcontroller.Service.OpenRgb;
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
    private OpenRgbLauncher? _launcher;
    private ControlServer? _control;
    private Task _supervisorTask = Task.CompletedTask;
    private bool _stripLit;

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
        _lighting = new LightingController(_config.Lighting, _strip, _config.Strip.LedCount, _log);
        _lighting.Start();
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

            _config = _config with { Lighting = _config.Lighting with { ActiveProfile = profile.Name } };
            _lighting?.Apply(_config.Lighting);
            _log.Information("Active profile set to {Profile}", profile.Name);
            if (!_canSaveConfig)
            {
                _log.Warning("Config file was rejected at start; the profile change is not saved");
                return null;
            }

            try
            {
                _store.Save(_config);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Warning("Could not save the active profile: {Error}", ex.Message);
            }

            return null;
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
