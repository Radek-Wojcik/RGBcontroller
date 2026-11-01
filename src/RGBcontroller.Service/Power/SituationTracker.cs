using RGBcontroller.Core.Lighting;
using Serilog;

namespace RGBcontroller.Service.Power;

/// <summary>
/// Combines the console session (WTS) and the console display state into the <see cref="LightingSituation"/> the
/// engine renders for. Every raw event is logged (spike R4). The session is re-read on every trigger and once more
/// shortly after a session event, because .NET delivers session events on thread-pool threads in no fixed order.
/// </summary>
internal sealed class SituationTracker : IDisposable
{
    private static readonly TimeSpan RecheckDelay = TimeSpan.FromSeconds(2);

    private readonly Func<ConsoleSessionInfo> _querySession;
    private readonly Action<LightingSituation> _changed;
    private readonly ILogger _log;
    private readonly Lock _gate = new();
    private readonly Timer _recheck;
    private DisplayStateMonitor? _display;
    private ConsoleSessionInfo _session;
    private bool _displayOn = true;
    private LightingSituation _current;

    public SituationTracker(Func<ConsoleSessionInfo> querySession, Action<LightingSituation> changed, ILogger log)
    {
        _querySession = querySession;
        _changed = changed;
        _log = log.ForContext<SituationTracker>();
        _recheck = new Timer(_ => Refresh("re-check"));
        _session = querySession();
        _current = new(_session.Kind, _displayOn);
        _log.Information("Console {Session}", _session);
    }

    public LightingSituation Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>Starts display notifications; the first callback reports the current display state.</summary>
    public void Start()
    {
        _display = new DisplayStateMonitor(OnDisplayState, ex => _log.Error(ex, "Display state handler failed"));
        try
        {
            _display.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            _log.Error("Display on/off is not tracked: {Error}", ex.Message);
            _display = null;
        }
    }

    /// <summary>SERVICE_CONTROL_SESSIONCHANGE (lock, unlock, logon, logoff, console connect...).</summary>
    public void OnSessionChange(string reason, int sessionId)
    {
        _log.Information("Session event {Reason} for session {SessionId}", reason, sessionId);
        Refresh(reason);
        _recheck.Change(RecheckDelay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Re-reads the session (after resume, or from the re-check timer) and reports a change.</summary>
    public void Refresh(string trigger)
    {
        var session = _querySession();
        Update(trigger, session, displayOn: null);
    }

    public void Dispose()
    {
        _display?.Dispose();
        _recheck.Dispose();
    }

    private void OnDisplayState(DisplayState state)
    {
        _log.Information("Display event {State}", state);

        // Dimmed is the warning before the idle timeout; the user still sees the screen.
        Update($"display {state}", _querySession(), state != DisplayState.Off);
    }

    /// <summary>The handler runs under the lock so concurrent triggers reach the engine in the order they were decided.</summary>
    private void Update(string trigger, ConsoleSessionInfo session, bool? displayOn)
    {
        lock (_gate)
        {
            if (session != _session)
            {
                _log.Information("Console {Session} (after {Trigger})", session, trigger);
                _session = session;
            }

            _displayOn = displayOn ?? _displayOn;
            var situation = new LightingSituation(_session.Kind, _displayOn);
            if (situation == _current)
            {
                return;
            }

            _log.Information("Situation {From} -> {To} (after {Trigger})", _current, situation, trigger);
            _current = situation;
            _changed(situation);
        }
    }
}
