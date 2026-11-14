using System.Diagnostics;

namespace RGBcontroller.Core.Ambilight;

/// <summary>
/// Notices a worker thread that stopped making progress. The worker calls <see cref="Beat"/> with the name of the step it
/// is entering; a checker on another thread asks <see cref="IsStalled"/>. A thread stuck inside a driver call (a GPU that
/// never answers) logs nothing and cannot rescue itself, so the stage it was in is the only clue to what it waited for.
/// </summary>
public sealed class StallWatchdog
{
    private readonly TimeSpan _limit;
    private readonly Func<TimeSpan> _clock;
    private long _lastBeatTicks;
    private volatile string _stage = "starting";

    public StallWatchdog(TimeSpan limit, Func<TimeSpan>? clock = null)
    {
        _limit = limit;
        _clock = clock ?? (static () => Stopwatch.GetElapsedTime(0));
        Interlocked.Exchange(ref _lastBeatTicks, _clock().Ticks);
    }

    /// <summary>The step the worker last reported.</summary>
    public string Stage => _stage;

    /// <summary>Marks progress: the worker is now in <paramref name="stage"/>.</summary>
    public void Beat(string stage)
    {
        _stage = stage;
        Interlocked.Exchange(ref _lastBeatTicks, _clock().Ticks);
    }

    /// <summary>True when the worker has not called <see cref="Beat"/> for longer than the limit.</summary>
    public bool IsStalled(out TimeSpan idle, out string stage)
    {
        idle = _clock() - TimeSpan.FromTicks(Interlocked.Read(ref _lastBeatTicks));
        stage = _stage;
        return idle > _limit;
    }
}

/// <summary>
/// "--restart-after &lt;pid&gt; &lt;count&gt;": how the Agent starts its replacement after it found its own capture thread
/// stuck. The new instance waits for the old process to end (it holds the single-instance mutex) and carries the number of
/// restarts so far, so a hang that comes back every time cannot restart the Agent forever.
/// </summary>
public readonly record struct AgentRestart(int PreviousProcessId, int Count)
{
    /// <summary>Restarts in a row after which the Agent stops restarting itself.</summary>
    public const int MaxCount = 3;

    /// <summary>The new instance uses <see cref="Count"/> only while it is younger than this; a long, stable run starts over.</summary>
    public static readonly TimeSpan StableAfter = TimeSpan.FromMinutes(10);

    public static bool TryParse(IReadOnlyList<string> args, out AgentRestart restart)
    {
        restart = default;
        if (args.Count == 3 && args[0] == "--restart-after"
            && int.TryParse(args[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var pid)
            && int.TryParse(args[2], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var count)
            && pid > 0 && count >= 0)
        {
            restart = new AgentRestart(pid, count);
            return true;
        }

        return false;
    }

    public string ToArguments() => FormattableString.Invariant($"--restart-after {PreviousProcessId} {Count}");

    /// <summary>True while another automatic restart is allowed, given how long this instance has run.</summary>
    public static bool MayRestart(int countSoFar, TimeSpan uptime) => (uptime >= StableAfter ? 0 : countSoFar) < MaxCount;

    /// <summary>The count the next instance carries: starts over after a long, stable run.</summary>
    public static int NextCount(int countSoFar, TimeSpan uptime) => (uptime >= StableAfter ? 0 : countSoFar) + 1;
}
