using RGBcontroller.Core;
using RGBcontroller.Core.Config;
using RGBcontroller.Core.Strip;
using RGBcontroller.Service.Hardware;
using RGBcontroller.Service.OpenRgb;
using Serilog;

namespace RGBcontroller.Service;

/// <summary>
/// Composition root shared by the Windows Service and console mode. Start order follows SPEC 3 / R5:
/// the strip lights immediately, OpenRGB (detection takes about 6 s) comes second. Stop sends black everywhere.
/// </summary>
internal sealed class LightingHost : IAsyncDisposable
{
    private const string SingletonMutexName = @"Global\RGBcontroller.LightingHost";

    private readonly AppConfig _config;
    private readonly string _installDirectory;
    private readonly AppPaths _paths;
    private readonly ILogger _log;
    private readonly StartupClock _clock = new();
    private readonly CancellationTokenSource _stop = new();
    private Mutex? _singleton;
    private StripWriter? _strip;
    private OpenRgbLauncher? _launcher;
    private Task _supervisorTask = Task.CompletedTask;
    private bool _stripLit;

    public LightingHost(AppConfig config, AppPaths paths, string installDirectory, ILogger log)
    {
        _config = config;
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
        RgbColor.TryParseHex(_config.Lighting.StaticColor, out var color);

        if (_config.Strip.Enabled)
        {
            _strip = new StripWriter(_config.Strip, new UsbSerialPortProvider(_config.Strip), _log);
            _strip.StateChanged += OnStripStateChanged;
            _strip.SetAll(color, _config.Lighting.StripBrightnessPercent);
            _strip.Start();
            _log.Information("Strip writer started: {Leds} LEDs, max {Fps} FPS", _config.Strip.LedCount, _strip.EffectiveMaxFps);
        }

        var executable = Path.GetFullPath(Path.Combine(_installDirectory, _config.OpenRgb.ExecutablePath));
        _launcher = new OpenRgbLauncher(executable, _paths.OpenRgbConfigDirectory, _config.OpenRgb, _log);
        Supervisor = new OpenRgbSupervisor(_launcher, _config.OpenRgb, _config.Lighting, _clock, _log);
        _supervisorTask = Task.Run(() => Supervisor.RunAsync(_stop.Token));
    }

    /// <summary>Sends black to the strip and all OpenRGB devices, then stops OpenRGB. Bounded by <paramref name="timeout"/>.</summary>
    public async Task StopAsync(TimeSpan timeout)
    {
        _log.Information("Lighting host stopping");
        await _stop.CancelAsync().ConfigureAwait(false);
        var work = Task.WhenAll(_supervisorTask, _strip?.StopAsync() ?? Task.CompletedTask);
        if (await Task.WhenAny(work, Task.Delay(timeout)).ConfigureAwait(false) != work)
        {
            _log.Warning("Stop did not finish within {Timeout} s; OpenRGB is terminated with the job object", timeout.TotalSeconds);
        }

        _launcher?.Dispose();
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

        _stop.Dispose();
        _singleton?.Dispose();
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
