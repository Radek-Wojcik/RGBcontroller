using RGBcontroller.Core.Config;
using RGBcontroller.Core.OpenRgb;
using RGBcontroller.Ipc;
using Serilog;

namespace RGBcontroller.Service.OpenRgb;

public sealed record SupervisorTimings(TimeSpan HealthCheckInterval, TimeSpan StableUptime, Func<int, TimeSpan> RestartBackoff)
{
    public TimeSpan ConnectAttemptTimeout { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan ConnectRetryDelay { get; init; } = TimeSpan.FromMilliseconds(250);

    public TimeSpan BlackoutGrace { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>Time since the system started (GetTickCount64).</summary>
    public Func<TimeSpan> Uptime { get; init; } = static () => TimeSpan.FromMilliseconds(Environment.TickCount64);

    /// <summary>SPEC 3: restart back-off 1, 2, 5, 15, 30 s.</summary>
    public static TimeSpan DefaultBackoff(int failures) => failures switch
    {
        <= 1 => TimeSpan.FromSeconds(1),
        2 => TimeSpan.FromSeconds(2),
        3 => TimeSpan.FromSeconds(5),
        4 => TimeSpan.FromSeconds(15),
        _ => TimeSpan.FromSeconds(30),
    };

    public static SupervisorTimings Default { get; } = new(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(60), DefaultBackoff);
}

/// <summary>
/// Keeps exactly one OpenRGB alive: removes strays on start, launches the pinned build, connects the SDK client,
/// lights the devices, re-applies after DEVICE_LIST_UPDATED (rescans, hot-plug), health-checks the SDK and
/// restarts with back-off when OpenRGB exits or stops answering. Never starts a second instance while one runs.
/// </summary>
internal sealed class OpenRgbSupervisor
{
    public const string ClientName = "RGBcontroller";

    private readonly IOpenRgbLauncher _launcher;
    private readonly OpenRgbConfig _config;
    private readonly LightingConfig _lighting;
    private readonly StartupClock _clock;
    private readonly SupervisorTimings _timings;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _deviceListChanged = new(0, 1);
    private readonly HashSet<string> _litDevices = [];
    private int _starts;

    public OpenRgbSupervisor(
        IOpenRgbLauncher launcher, OpenRgbConfig config, LightingConfig lighting, StartupClock clock, ILogger log, SupervisorTimings? timings = null)
    {
        _launcher = launcher;
        _config = config;
        _lighting = lighting;
        _clock = clock;
        _log = log.ForContext<OpenRgbSupervisor>();
        _timings = timings ?? SupervisorTimings.Default;
    }

    public ComponentState State { get; private set; } = ComponentState.Unknown;

    /// <summary>Number of OpenRGB launches so far (1 = no restart).</summary>
    public int Starts => Volatile.Read(ref _starts);

    /// <summary>Raised after each successful (re)apply with the device count.</summary>
    public event Action<int>? DevicesApplied;

    /// <summary>Runs until <paramref name="stopToken"/> is cancelled, then sends black to all devices and stops OpenRGB.</summary>
    public async Task RunAsync(CancellationToken stopToken)
    {
        var killed = _launcher.KillStrays();
        if (killed > 0)
        {
            _log.Warning("Removed {Count} OpenRGB instance(s) not started by the Service", killed);
        }

        var bootWait = TimeSpan.FromSeconds(_config.MinimumUptimeSeconds) - _timings.Uptime();
        if (bootWait > TimeSpan.Zero)
        {
            _log.Information("Waiting {Seconds:0.0} s before starting OpenRGB so its device scan does not run while Windows starts the display",
                bootWait.TotalSeconds);
            if (!await DelayAsync(bootWait, stopToken).ConfigureAwait(false))
            {
                State = ComponentState.Offline;
                return;
            }
        }

        var failures = 0;
        while (!stopToken.IsCancellationRequested)
        {
            State = ComponentState.Starting;
            var startedAt = _clock.Elapsed;
            IOpenRgbProcess process;
            try
            {
                process = _launcher.Start();
                Interlocked.Increment(ref _starts);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                _log.Error("Could not start OpenRGB: {Error}", ex.Message);
                failures++;
                State = ComponentState.Offline;
                if (!await DelayAsync(_timings.RestartBackoff(failures), stopToken).ConfigureAwait(false))
                {
                    break;
                }

                continue;
            }

            using (process)
            {
                await RunSessionAsync(process, stopToken).ConfigureAwait(false);
                process.Kill();
            }

            if (stopToken.IsCancellationRequested)
            {
                break;
            }

            failures = _clock.Elapsed - startedAt >= _timings.StableUptime ? 1 : failures + 1;
            State = ComponentState.Degraded;
            var delay = _timings.RestartBackoff(failures);
            _log.Warning("Restarting OpenRGB in {Delay} s (consecutive failures {Failures})", delay.TotalSeconds, failures);
            if (!await DelayAsync(delay, stopToken).ConfigureAwait(false))
            {
                break;
            }
        }

        State = ComponentState.Offline;
        _log.Information("OpenRGB supervisor stopped");
    }

    private async Task RunSessionAsync(IOpenRgbProcess process, CancellationToken stopToken)
    {
        OpenRgbSdkClient? client = null;
        DeviceLighting? lighting = null;
        try
        {
            client = await ConnectAsync(process, stopToken).ConfigureAwait(false);
            _log.Information("Connected to OpenRGB SDK (protocol {Protocol}, server {Server}) {ElapsedMs} ms after Service start",
                client.ProtocolVersion, client.ServerProtocolVersion, (long)_clock.Elapsed.TotalMilliseconds);

            var disconnected = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.Disconnected += cause => disconnected.TrySetResult(cause);
            client.DeviceListUpdated += SignalDeviceListChanged;

            lighting = new DeviceLighting(client, _config, _lighting, _clock, _litDevices, _log);
            await lighting.ApplyAllAsync(stopToken).ConfigureAwait(false);
            State = ComponentState.Ok;
            DevicesApplied?.Invoke(lighting.Devices.Count);

            Task? changed = null;
            while (true)
            {
                changed ??= _deviceListChanged.WaitAsync(stopToken);
                var health = Task.Delay(_timings.HealthCheckInterval, stopToken);
                var first = await Task.WhenAny(process.Exited, disconnected.Task, changed, health).ConfigureAwait(false);
                stopToken.ThrowIfCancellationRequested();

                if (first == process.Exited)
                {
                    _log.Error("OpenRGB pid {Pid} exited unexpectedly", process.Id);
                    return;
                }

                if (first == disconnected.Task)
                {
                    _log.Error("OpenRGB SDK connection lost: {Error}", (await disconnected.Task.ConfigureAwait(false))?.Message);
                    return;
                }

                if (first == changed)
                {
                    changed = null;
                    await WaitForQuietDeviceListAsync(stopToken).ConfigureAwait(false);
                    _log.Information("Device list changed; re-applying lighting");
                    await lighting.ApplyAllAsync(stopToken).ConfigureAwait(false);
                    DevicesApplied?.Invoke(lighting.Devices.Count);
                    continue;
                }

                await client.GetControllerCountAsync(stopToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
        {
            if (lighting is not null && client is { IsConnected: true })
            {
                await BlackoutAsync(lighting, client).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or OpenRgbProtocolException or System.Net.Sockets.SocketException)
        {
            _log.Error("OpenRGB session failed: {Error}", ex.Message);
        }
        finally
        {
            if (client is not null)
            {
                client.DeviceListUpdated -= SignalDeviceListChanged;
                await client.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task<OpenRgbSdkClient> ConnectAsync(IOpenRgbProcess process, CancellationToken stopToken)
    {
        var deadline = _clock.Elapsed + TimeSpan.FromSeconds(_config.StartupTimeoutSeconds);
        while (true)
        {
            if (process.Exited.IsCompleted)
            {
                throw new IOException("OpenRGB exited before its SDK server accepted a connection.");
            }

            try
            {
                return await OpenRgbSdkClient.ConnectAsync(_config.Host, _config.Port, ClientName, _timings.ConnectAttemptTimeout, stopToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (!stopToken.IsCancellationRequested
                && ex is System.Net.Sockets.SocketException or OperationCanceledException or TimeoutException or IOException)
            {
                if (_clock.Elapsed > deadline)
                {
                    throw new TimeoutException($"OpenRGB SDK server did not accept a connection within {_config.StartupTimeoutSeconds} s.");
                }
            }

            await Task.Delay(_timings.ConnectRetryDelay, stopToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Sends black, then one round trip so OpenRGB has processed the writes, then a short grace period because
    /// OpenRGB pushes LED updates to the hardware on per-device threads. Only then may the process be killed.
    /// </summary>
    private async Task BlackoutAsync(DeviceLighting lighting, OpenRgbSdkClient client)
    {
        lighting.Blackout();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1.5));
        try
        {
            await client.GetControllerCountAsync(timeout.Token).ConfigureAwait(false);
            await Task.Delay(_timings.BlackoutGrace, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or IOException)
        {
            _log.Warning("OpenRGB did not confirm the black frame: {Error}", ex.Message);
        }
    }

    /// <summary>Debounce: wait until no DEVICE_LIST_UPDATED arrived for the settle period (a rescan sends several).</summary>
    private async Task WaitForQuietDeviceListAsync(CancellationToken stopToken)
    {
        var settle = TimeSpan.FromMilliseconds(_config.DeviceSettleMilliseconds);
        while (await _deviceListChanged.WaitAsync(settle, stopToken).ConfigureAwait(false))
        {
            // Another notice arrived inside the settle window; keep waiting.
        }
    }

    private void SignalDeviceListChanged()
    {
        try
        {
            _deviceListChanged.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already pending.
        }
    }

    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken stopToken)
    {
        try
        {
            await Task.Delay(delay, stopToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
