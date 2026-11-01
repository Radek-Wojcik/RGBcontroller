using RGBcontroller.Core;
using RGBcontroller.Core.Config;
using RGBcontroller.Core.Lighting;
using RGBcontroller.Core.OpenRgb;
using RGBcontroller.Core.Strip;
using Serilog;

namespace RGBcontroller.Service.Lighting;

/// <summary>Receives the OpenRGB devices from the supervisor.</summary>
internal interface IDeviceLightingSink
{
    /// <summary>A fresh enumeration (start, restart or rescan) is ready to be driven through <paramref name="client"/>.</summary>
    void Attach(OpenRgbSdkClient client, IReadOnlyList<ControllerData> devices);

    /// <summary>Stop writing to OpenRGB (device list changing, connection lost, stopping). Returns once no frame is in flight.</summary>
    void Detach();
}

/// <summary>
/// Owns the effect engine and its render loop. The strip is lit as soon as <see cref="Start"/> runs; OpenRGB devices
/// join when the supervisor attaches them.
/// </summary>
internal sealed class LightingController : IDeviceLightingSink, IDisposable
{
    private readonly LightingEngine _engine;
    private readonly HardwareOutput _output;
    private readonly RenderLoop _loop;

    public LightingController(LightingConfig config, StripWriter? strip, int stripLedCount, ILogger log)
    {
        _output = new HardwareOutput(strip, log);
        _engine = new LightingEngine(config, strip is null ? 0 : stripLedCount, strip?.EffectiveMaxFps ?? 30, log);
        _loop = new RenderLoop(_engine, _output, log);
    }

    public LightingConfig Config => _engine.Config;

    public void Start() => _loop.Start();

    /// <summary>Applies a changed lighting config (profile switch) on the next frame.</summary>
    public void Apply(LightingConfig config)
    {
        _engine.SetConfig(config);
        _loop.Wake();
    }

    /// <summary>Session or display changed: logon/lock/active profile and the display-off turn-off sets.</summary>
    public void SetSituation(LightingSituation situation)
    {
        _engine.SetSituation(situation);
        _loop.Wake();
    }

    /// <summary>Sends black to everything and waits (bounded) until the frame is out; the PC is about to sleep.</summary>
    public bool Suspend(TimeSpan timeout)
    {
        _engine.SetBlackout(true);
        return _loop.RenderNow(timeout);
    }

    /// <summary>Back to the profile after resume. OpenRGB devices stay detached until the supervisor's rescan re-attaches them.</summary>
    public void Resume()
    {
        Detach();
        _engine.SetBlackout(false);
        _loop.Wake();
    }

    public void Attach(OpenRgbSdkClient client, IReadOnlyList<ControllerData> devices)
    {
        _output.Client = client;
        _engine.SetDevices(devices);
        _loop.Wake();
    }

    public void Detach()
    {
        _engine.ClearDevices();
        _output.Client = null;
    }

    /// <summary>Ends the render loop; afterwards nothing is written to the strip or OpenRGB.</summary>
    public void Stop() => _loop.Stop();

    public void Dispose() => _loop.Dispose();

    /// <summary>Writes engine frames to the current SDK connection and the strip writer.</summary>
    private sealed class HardwareOutput(StripWriter? strip, ILogger log) : ILightingOutput
    {
        private readonly ILogger _log = log.ForContext<LightingController>();
        private volatile OpenRgbSdkClient? _client;
        private volatile bool _failed;

        public OpenRgbSdkClient? Client
        {
            get => _client;
            set
            {
                _client = value;
                _failed = false;
            }
        }

        public void SetDeviceMode(int deviceIndex, int modeIndex, ControllerMode mode)
        {
            var client = _client;
            if (client is null)
            {
                return;
            }

            try
            {
                // UPDATEMODE, not SETCUSTOMMODE: only UpdateMode makes OpenRGB push the mode to the hardware.
                client.UpdateMode(deviceIndex, modeIndex, mode);
                _log.Information("OpenRGB device {Index} set to mode '{Mode}'", deviceIndex, mode.Name);
            }
            catch (IOException ex)
            {
                ReportFailure(ex);
            }
        }

        public void SetDeviceLeds(int deviceIndex, ReadOnlySpan<RgbColor> colors)
        {
            var client = _client;
            if (client is null || _failed)
            {
                return;
            }

            try
            {
                client.UpdateLeds(deviceIndex, colors);
            }
            catch (IOException ex)
            {
                ReportFailure(ex);
            }
        }

        public void SetStripLeds(ReadOnlySpan<RgbColor> colors) => strip?.SetColors(colors, 100);

        /// <summary>The supervisor notices the lost connection and reconnects; until then writes are skipped.</summary>
        private void ReportFailure(IOException ex)
        {
            if (!_failed)
            {
                _failed = true;
                _log.Warning("OpenRGB write failed ({Error}); waiting for the supervisor to reconnect", ex.Message);
            }
        }
    }
}
