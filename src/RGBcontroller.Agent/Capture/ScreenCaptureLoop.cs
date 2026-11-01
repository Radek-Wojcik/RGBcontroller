using System.Diagnostics;
using RGBcontroller.Core;
using RGBcontroller.Core.Ambilight;
using RGBcontroller.Core.Config;
using RGBcontroller.Service.Hardware;
using Serilog;
using SharpGen.Runtime;

namespace RGBcontroller.Agent.Capture;

/// <summary>Where the capture loop sends the strip (the Service's frame pipe).</summary>
internal interface IStripFrameSink
{
    /// <summary>New smoothed colors in strip LED order, before brightness.</summary>
    void SendColors(ReadOnlySpan<RgbColor> colors);

    /// <summary>The screen cannot be captured; the Service shows the profile's fallback mode until colors arrive again.</summary>
    void SendUnavailable();
}

/// <summary>Counters for diagnostics and the CPU check.</summary>
internal sealed record CaptureStats(long Frames, long Sent, long Lost, string? Monitor, ScreenPixelFormat Format, (int Width, int Height) SampleSize,
    double ScreenWidthCm, float HdrScale, ContentRect Content);

/// <summary>
/// The Screen mode thread (SPEC 6.2): captures at most <see cref="ScreenCaptureConfig.CaptureFps"/>, smooths and sends
/// at most <see cref="ScreenCaptureConfig.OutputFps"/>, and only while something changes. With a still screen and a
/// settled strip it blocks in AcquireNextFrame, which wakes only when the desktop changes, so an idle desktop costs
/// next to nothing. A lost duplication (UAC, lock, mode change, exclusive fullscreen) is re-created with back-off;
/// the strip keeps its last colors for the grace time, then the Service is told to show the fallback.
/// Resources exist only between <see cref="Start"/> and <see cref="Stop"/>.
/// </summary>
internal sealed class ScreenCaptureLoop : IScreenImageSink, IDisposable
{
    private const uint IdleWaitMilliseconds = 200;
    private static readonly TimeSpan[] Backoff = [TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)];

    private readonly ScreenCaptureConfig _config;
    private readonly StripLayout _layout;
    private readonly IStripFrameSink _output;
    private readonly ILogger _log;
    private readonly ManualResetEvent _stop = new(false);
    private readonly Stopwatch _clock = new();
    private Thread? _thread;
    private AmbilightPipeline? _pipeline;
    private DesktopDuplicator? _duplicator;
    private TimeSpan _now;
    private long _frames;
    private long _sent;
    private long _lost;
    private double _screenWidthCm;

    public ScreenCaptureLoop(ScreenCaptureConfig config, StripLayoutConfig layout, int ledCount, IStripFrameSink output, ILogger log)
    {
        _config = config;
        _layout = new StripLayout(layout, ledCount);
        _output = output;
        _log = log.ForContext<ScreenCaptureLoop>();
    }

    public bool IsRunning => _thread is not null;

    public CaptureStats Stats
    {
        get
        {
            var duplicator = _duplicator;
            return new CaptureStats(Interlocked.Read(ref _frames), Interlocked.Read(ref _sent), Interlocked.Read(ref _lost), duplicator?.Monitor.Name,
                duplicator?.PixelFormat ?? ScreenPixelFormat.Bgra8, duplicator?.SampleSize ?? default, _screenWidthCm,
                _pipeline?.HdrScale ?? 1, _pipeline?.Content ?? default);
        }
    }

    public void Start()
    {
        if (_thread is not null)
        {
            return;
        }

        _stop.Reset();
        _thread = new Thread(Run) { IsBackground = true, Name = "Screen capture" };
        _thread.Start();
    }

    /// <summary>Ends the thread and releases the duplication and GPU resources (SPEC 6.2: capture stops completely).</summary>
    public void Stop()
    {
        if (_thread is null)
        {
            return;
        }

        _stop.Set();
        _thread.Join();
        _thread = null;
    }

    public void Dispose()
    {
        Stop();
        _stop.Dispose();
    }

    void IScreenImageSink.OnImage(in ScreenImage image) => _pipeline!.OnImage(image, _now);

    private void Run()
    {
        _log.Information("Screen capture started ({CaptureFps} FPS capture, {OutputFps} FPS output)", _config.CaptureFps, _config.OutputFps);
        using var timer = new HighResolutionTimer();
        WaitHandle[] waits = [_stop, timer];
        var captureInterval = TimeSpan.FromSeconds(1.0 / _config.CaptureFps);
        var outputInterval = TimeSpan.FromSeconds(1.0 / _config.OutputFps);
        var failures = 0;
        TimeSpan? lostSince = null;
        var reportedUnavailable = false;
        var nextCapture = TimeSpan.Zero;
        var lastStep = TimeSpan.Zero;
        var nextStep = TimeSpan.Zero;
        _clock.Restart();
        try
        {
            while (!_stop.WaitOne(0))
            {
                _now = _clock.Elapsed;
                if (_duplicator is null)
                {
                    if (TryOpen())
                    {
                        failures = 0;
                    }
                    else
                    {
                        lostSince ??= _now;
                        if (!reportedUnavailable && _now - lostSince >= TimeSpan.FromMilliseconds(_config.FallbackGraceMilliseconds))
                        {
                            reportedUnavailable = true;
                            _output.SendUnavailable();
                            _log.Information("Screen capture unavailable; the Service shows the fallback");
                        }

                        timer.Arm(Backoff[Math.Min(failures++, Backoff.Length - 1)]);
                        WaitHandle.WaitAny(waits);
                        continue;
                    }
                }

                if (_now >= nextCapture)
                {
                    var settled = _pipeline!.IsSettled;
                    var outcome = Capture(settled ? IdleWaitMilliseconds : 0);
                    _now = _clock.Elapsed;
                    if (outcome == CaptureOutcome.Lost)
                    {
                        lostSince = _now;
                        continue;
                    }

                    if (outcome == CaptureOutcome.Frame)
                    {
                        Interlocked.Increment(ref _frames);
                        lostSince = null;
                        reportedUnavailable = false;
                        if (settled)
                        {
                            // Waking from idle: smooth from now on, starting with this frame.
                            lastStep = _now;
                            nextStep = _now;
                        }
                    }

                    // Never poll the desktop faster than the capture rate; when idle, block again right away.
                    nextCapture = outcome == CaptureOutcome.NoChange && settled ? _now : _now + captureInterval;
                }

                if (_pipeline!.IsSettled)
                {
                    if (_now < nextCapture)
                    {
                        timer.Arm(nextCapture - _now);
                        WaitHandle.WaitAny(waits);
                    }

                    continue;
                }

                if (_now >= nextStep)
                {
                    if (_pipeline.Step(_now - lastStep))
                    {
                        _output.SendColors(_pipeline.Output);
                        Interlocked.Increment(ref _sent);
                    }

                    lastStep = _now;
                    nextStep = _now + outputInterval;
                }

                var due = _pipeline.IsSettled || nextCapture < nextStep ? nextCapture : nextStep;
                if (due > _now)
                {
                    timer.Arm(due - _now);
                    WaitHandle.WaitAny(waits);
                }
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Screen capture failed");
            _output.SendUnavailable();
        }
        finally
        {
            _duplicator?.Dispose();
            _duplicator = null;
            _log.Information("Screen capture stopped: {Frames} frames, {Sent} sent, {Lost} losses", _frames, _sent, _lost);
        }
    }

    private CaptureOutcome Capture(uint timeoutMilliseconds)
    {
        try
        {
            var outcome = _duplicator!.TryCapture(timeoutMilliseconds, this);
            if (outcome != CaptureOutcome.Lost)
            {
                return outcome;
            }

            _log.Information("Desktop duplication lost (secure desktop, mode change or fullscreen); re-creating");
        }
        catch (SharpGenException ex)
        {
            _log.Warning("Desktop duplication failed ({Error}); re-creating", ex.Message);
        }

        Interlocked.Increment(ref _lost);
        _duplicator!.Dispose();
        _duplicator = null;
        return CaptureOutcome.Lost;
    }

    private bool TryOpen()
    {
        try
        {
            _duplicator = DesktopDuplicator.Open(_config.Monitor, _config.MinimumSampleWidth);
        }
        catch (Exception ex) when (ex is SharpGenException or InvalidOperationException)
        {
            _log.Debug("Desktop duplication not available: {Error}", ex.Message);
            return false;
        }

        var monitor = _duplicator.Monitor;
        var width = _config.ScreenWidthCm > 0 ? _config.ScreenWidthCm : DisplayInfo.ScreenWidthCm(monitor.Name);
        if (width is null)
        {
            // 16:9 at 31.5" is 69.7 cm wide; without EDID a typical desktop size keeps the bands plausible.
            width = 60;
            _log.Warning("Screen width of {Monitor} unknown (no EDID); assuming {Width} cm, set capture.screenWidthCm", monitor.Name, width);
        }

        if (_pipeline is null || width != _screenWidthCm)
        {
            _screenWidthCm = width.Value;
            _pipeline = new AmbilightPipeline(_layout, _config, _screenWidthCm);
        }

        var sdrWhite = _config.HdrSdrWhiteNits > 0 ? _config.HdrSdrWhiteNits : monitor.Hdr ? DisplayInfo.SdrWhiteNits(monitor.Name) ?? 80 : 80;
        _pipeline.HdrScale = ColorMath.HdrScale(sdrWhite);
        if (!monitor.Unrotated)
        {
            _log.Warning("Monitor {Monitor} is rotated; the strip edges will not match the picture", monitor.Name);
        }

        _log.Information("Duplicating {Monitor} {Width}x{Height}, {Range}, screen {ScreenCm:0.0} cm wide, SDR white {SdrWhite:0} nits",
            monitor.Name, monitor.Width, monitor.Height, monitor.Hdr ? "HDR" : "SDR", _screenWidthCm, sdrWhite);
        return true;
    }
}
