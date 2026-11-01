using System.Diagnostics;
using RGBcontroller.Core.Lighting;
using RGBcontroller.Service.Hardware;
using Serilog;

namespace RGBcontroller.Service.Lighting;

/// <summary>
/// Drives <see cref="LightingEngine.Render"/> on one dedicated thread. Sleeps on a high-resolution timer until the
/// next device is due, and with nothing animated it waits only for <see cref="Wake"/> (static profile: 0% CPU).
/// </summary>
internal sealed class RenderLoop : IDisposable
{
    private static readonly TimeSpan ErrorRetry = TimeSpan.FromSeconds(1);

    private readonly LightingEngine _engine;
    private readonly ILightingOutput _output;
    private readonly ILogger _log;
    private readonly AutoResetEvent _wake = new(false);
    private readonly AutoResetEvent _rendered = new(false);
    private readonly HighResolutionTimer _timer = new();
    private readonly Stopwatch _clock = new();
    private Thread? _thread;
    private volatile bool _stopping;
    private long _requested;
    private long _completed;

    public RenderLoop(LightingEngine engine, ILightingOutput output, ILogger log)
    {
        _engine = engine;
        _output = output;
        _log = log.ForContext<RenderLoop>();
    }

    public void Start()
    {
        if (_thread is not null)
        {
            return;
        }

        _clock.Start();
        _thread = new Thread(Run) { Name = "RGBcontroller render loop", IsBackground = true, Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        _log.Information("Render loop started (high-resolution timer: {HighResolution})", _timer.IsHighResolution);
    }

    /// <summary>Renders again now; call after changing the engine's profile or devices.</summary>
    public void Wake() => _wake.Set();

    /// <summary>
    /// Renders now and waits until that frame has been handed to the outputs (suspend: black must go out before the
    /// system sleeps). A frame that was already in progress when this was called does not count.
    /// </summary>
    /// <returns>False on timeout or when the loop is not running.</returns>
    public bool RenderNow(TimeSpan timeout)
    {
        var request = Interlocked.Increment(ref _requested);
        _wake.Set();
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (Interlocked.Read(ref _completed) < request)
        {
            var remaining = deadline - Stopwatch.GetTimestamp();
            if (_thread is null || remaining <= 0 || !_rendered.WaitOne(TimeSpan.FromSeconds((double)remaining / Stopwatch.Frequency)))
            {
                return Interlocked.Read(ref _completed) >= request;
            }
        }

        return true;
    }

    /// <summary>Stops the thread after the frame in progress. No frames are written afterwards.</summary>
    public void Stop()
    {
        _stopping = true;
        _wake.Set();
        _thread?.Join();
    }

    public void Dispose()
    {
        Stop();
        _wake.Dispose();
        _rendered.Dispose();
        _timer.Dispose();
    }

    private void Run()
    {
        WaitHandle[] wakeOnly = [_wake];
        WaitHandle[] wakeOrTimer = [_wake, _timer];
        while (!_stopping)
        {
            TimeSpan? next;
            var serving = Interlocked.Read(ref _requested);
            try
            {
                next = _engine.Render(_clock.Elapsed, _output);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Render failed; retrying in {Seconds} s", ErrorRetry.TotalSeconds);
                next = ErrorRetry;
            }

            Interlocked.Exchange(ref _completed, serving);
            _rendered.Set();
            if (_stopping)
            {
                break;
            }

            if (next is null)
            {
                WaitHandle.WaitAny(wakeOnly);
            }
            else if (next > TimeSpan.Zero)
            {
                _timer.Arm(next.Value);
                WaitHandle.WaitAny(wakeOrTimer);
            }
        }
    }
}
