using System.Diagnostics;

namespace RGBcontroller.Service;

/// <summary>Measures time-to-first-light (spike R5) relative to Service start.</summary>
internal sealed class StartupClock
{
    private readonly Stopwatch _sinceStart = Stopwatch.StartNew();

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;

    public TimeSpan Elapsed => _sinceStart.Elapsed;
}
