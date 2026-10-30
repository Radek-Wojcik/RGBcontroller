using System.Diagnostics;
using RGBcontroller.Core.Config;
using Serilog;

namespace RGBcontroller.Core.Strip;

/// <summary>An open serial connection to the strip controller.</summary>
public interface IStripTransport : IDisposable
{
    string PortName { get; }

    /// <summary>Queues a frame; throws IOException, TimeoutException or UnauthorizedAccessException when the port is gone.</summary>
    void Write(ReadOnlySpan<byte> frame);

    /// <summary>Blocks until queued bytes are on the wire (used before closing so the black frame is not lost).</summary>
    void Flush();
}

/// <summary>Finds and opens the strip's serial port (by USB VID/PID or an explicit override).</summary>
public interface IStripPortProvider
{
    /// <returns>The port name, or null when the device is not present.</returns>
    string? FindPort();

    IStripTransport Open(string portName);
}

public enum StripState
{
    Stopped,
    Searching,
    Settling,
    Connected,
}

public sealed record StripWriterTimings(TimeSpan Settle, TimeSpan KeepAlive, Func<int, TimeSpan> Backoff)
{
    public int StartupFrames { get; init; } = 3;

    public TimeSpan StartupFrameGap { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>Reconnect delays 0.5, 1, 2, 5 s, then every 5 s (discovery is a cheap registry lookup).</summary>
    public static TimeSpan DefaultBackoff(int attempt) => attempt switch
    {
        0 => TimeSpan.FromMilliseconds(500),
        1 => TimeSpan.FromSeconds(1),
        2 => TimeSpan.FromSeconds(2),
        _ => TimeSpan.FromSeconds(5),
    };

    public static StripWriterTimings FromConfig(StripConfig config) => new(
        TimeSpan.FromMilliseconds(config.OpenSettleMilliseconds),
        TimeSpan.FromMilliseconds(config.KeepAliveMilliseconds),
        DefaultBackoff);
}

/// <summary>
/// Owns the Adalight strip: discovers the port, waits for the board to settle after open, sends a startup
/// burst (the first frames can be lost), writes only on change plus a slow keep-alive, reconnects with
/// back-off when the port disappears (CH340 re-enumeration on resume, spike R6) and always ends with black.
/// The write path does not allocate: colors are copied into a fixed buffer and rendered into a fixed frame.
/// </summary>
public sealed class StripWriter : IAsyncDisposable
{
    private readonly IStripPortProvider _ports;
    private readonly ILogger _log;
    private readonly StripWriterTimings _timings;
    private readonly AdalightFrame _frame;
    private readonly RgbColor[] _colors;
    private readonly Lock _colorsLock = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly TimeSpan _minFrameInterval;
    private readonly Stopwatch _sinceLastWrite = new();
    private int _brightnessPercent;
    private Task _loop = Task.CompletedTask;
    private long _framesWritten;

    public StripWriter(StripConfig config, IStripPortProvider ports, ILogger log, StripWriterTimings? timings = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        _ports = ports;
        _log = log.ForContext<StripWriter>();
        _timings = timings ?? StripWriterTimings.FromConfig(config);
        _frame = new AdalightFrame(config.LedCount, config.ColorOrder, config.CurrentLimitMilliamps);
        _colors = new RgbColor[config.LedCount];

        var linkFps = AdalightFrame.MaxFramesPerSecond(config.BaudRate, config.LedCount);
        EffectiveMaxFps = Math.Max(1, Math.Min(config.MaxFps, (int)Math.Floor(linkFps * 0.95)));
        _minFrameInterval = TimeSpan.FromSeconds(1.0 / EffectiveMaxFps);
    }

    /// <summary>Configured FPS clamped to 95% of what the serial link can carry.</summary>
    public int EffectiveMaxFps { get; }

    public StripState State { get; private set; } = StripState.Stopped;

    public string? PortName { get; private set; }

    public long FramesWritten => Interlocked.Read(ref _framesWritten);

    /// <summary>Raised on every state change with the port name (if any).</summary>
    public event Action<StripState, string?>? StateChanged;

    public void Start()
    {
        if (!_loop.IsCompleted)
        {
            return;
        }

        _loop = Task.Run(() => RunAsync(_stop.Token));
    }

    public void SetAll(RgbColor color, int brightnessPercent)
    {
        lock (_colorsLock)
        {
            _colors.AsSpan().Fill(color);
            _brightnessPercent = brightnessPercent;
        }

        Wake();
    }

    public void SetColors(ReadOnlySpan<RgbColor> colors, int brightnessPercent)
    {
        lock (_colorsLock)
        {
            var count = Math.Min(colors.Length, _colors.Length);
            colors[..count].CopyTo(_colors);
            _colors.AsSpan(count).Clear();
            _brightnessPercent = brightnessPercent;
        }

        Wake();
    }

    /// <summary>Writes black, closes the port and stops reconnecting. Safe to call more than once.</summary>
    public async Task StopAsync()
    {
        if (!_stop.IsCancellationRequested)
        {
            await _stop.CancelAsync().ConfigureAwait(false);
        }

        await _loop.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _stop.Dispose();
        _wake.Dispose();
    }

    private void Wake()
    {
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already signalled; the loop will pick up the latest colors.
        }
    }

    private async Task RunAsync(CancellationToken token)
    {
        var attempt = 0;
        IStripTransport? transport = null;
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var port = _ports.FindPort();
                    if (port is null)
                    {
                        SetState(StripState.Searching, null);
                        await Task.Delay(_timings.Backoff(attempt++), token).ConfigureAwait(false);
                        continue;
                    }

                    transport = _ports.Open(port);
                    SetState(StripState.Settling, port);
                    await Task.Delay(_timings.Settle, token).ConfigureAwait(false);
                    for (var i = 0; i < _timings.StartupFrames; i++)
                    {
                        WriteCurrent(transport);
                        await Task.Delay(_timings.StartupFrameGap, token).ConfigureAwait(false);
                    }

                    SetState(StripState.Connected, port);
                    attempt = 0;
                    while (true)
                    {
                        await _wake.WaitAsync(_timings.KeepAlive, token).ConfigureAwait(false);
                        var wait = _minFrameInterval - _sinceLastWrite.Elapsed;
                        if (wait > TimeSpan.Zero)
                        {
                            await Task.Delay(wait, token).ConfigureAwait(false);
                        }

                        WriteCurrent(transport);
                    }
                }
                catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or InvalidOperationException)
                {
                    _log.Warning("Strip port {Port} failed ({Error}); reconnecting", PortName, ex.Message);
                    transport?.Dispose();
                    transport = null;
                    SetState(StripState.Searching, null);
                    await Task.Delay(_timings.Backoff(attempt++), token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Stop requested.
        }
        finally
        {
            if (transport is not null)
            {
                WriteBlackAndClose(transport);
            }

            SetState(StripState.Stopped, null);
        }
    }

    private void WriteCurrent(IStripTransport transport)
    {
        lock (_colorsLock)
        {
            _frame.Render(_colors, _brightnessPercent);
        }

        transport.Write(_frame.Bytes);
        _sinceLastWrite.Restart();
        Interlocked.Increment(ref _framesWritten);
    }

    private void WriteBlackAndClose(IStripTransport transport)
    {
        try
        {
            _frame.Render([], 0);
            for (var i = 0; i < _timings.StartupFrames; i++)
            {
                transport.Write(_frame.Bytes);
                Interlocked.Increment(ref _framesWritten);
            }

            transport.Flush();
            _log.Information("Strip on {Port} set to black", transport.PortName);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or InvalidOperationException)
        {
            _log.Warning("Could not send black to the strip on {Port}: {Error}", transport.PortName, ex.Message);
        }
        finally
        {
            transport.Dispose();
        }
    }

    private void SetState(StripState state, string? port)
    {
        if (State == state && PortName == port)
        {
            return;
        }

        State = state;
        PortName = port;
        _log.Information("Strip {State} {Port}", state, port);
        StateChanged?.Invoke(state, port);
    }
}
