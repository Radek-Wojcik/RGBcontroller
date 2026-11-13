using System.Diagnostics;
using RGBcontroller.Core.Ambilight;
using Serilog;

namespace RGBcontroller.Agent.Capture;

/// <summary>
/// Rescues an Agent whose capture thread is stuck inside a driver call (it logs nothing and cannot end itself; seen once as
/// a frozen Agent with the GPU busy). A timer on its own thread checks the capture loop's beats; when they stop for longer
/// than <see cref="ScreenCaptureLoop.StallLimit"/> it records the step the thread was in, starts a replacement Agent that
/// waits for this one to end, and ends this process. After <see cref="AgentRestart.MaxCount"/> quick restarts in a row it
/// stops trying, so a hang that returns every time cannot loop forever.
/// </summary>
internal sealed class CaptureWatchdog : IDisposable
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(5);

    private readonly Func<ScreenCaptureLoop?> _loop;
    private readonly ILogger _log;
    private readonly int _restartsSoFar;
    private readonly Action _flushLogs;
    private readonly Stopwatch _uptime = Stopwatch.StartNew();
    private readonly System.Threading.Timer _timer;
    private int _fired;

    public CaptureWatchdog(Func<ScreenCaptureLoop?> loop, ILogger log, int restartsSoFar, Action flushLogs)
    {
        _loop = loop;
        _log = log.ForContext<CaptureWatchdog>();
        _restartsSoFar = restartsSoFar;
        _flushLogs = flushLogs;
        _timer = new System.Threading.Timer(_ => Check(), null, CheckInterval, CheckInterval);
    }

    public void Dispose() => _timer.Dispose();

    private void Check()
    {
        var loop = _loop();
        if (loop is not { IsRunning: true } || !loop.Watchdog.IsStalled(out var idle, out var stage) || Interlocked.Exchange(ref _fired, 1) == 1)
        {
            return;
        }

        if (!AgentRestart.MayRestart(_restartsSoFar, _uptime.Elapsed))
        {
            _log.Error("The screen capture thread has been stuck for {Seconds:0} s in step '{Stage}'; not restarting again after {Count} quick restarts",
                idle.TotalSeconds, stage, _restartsSoFar);
            return;
        }

        var next = AgentRestart.NextCount(_restartsSoFar, _uptime.Elapsed);
        _log.Error("The screen capture thread has been stuck for {Seconds:0} s in step '{Stage}'; restarting the Agent ({Count} of {Max})",
            idle.TotalSeconds, stage, next, AgentRestart.MaxCount);
        try
        {
            var path = Environment.ProcessPath;
            if (path is not null)
            {
                using var _ = Process.Start(new ProcessStartInfo(path, new AgentRestart(Environment.ProcessId, next).ToArguments()) { UseShellExecute = false });
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _log.Error(ex, "Could not start the replacement Agent");
        }

        _flushLogs();

        // Environment.Exit can wait for things a stuck driver call holds; the kill is the backstop.
        new Thread(() =>
        {
            Thread.Sleep(5000);
            Process.GetCurrentProcess().Kill();
        }) { IsBackground = true }.Start();
        Environment.Exit(3);
    }
}
