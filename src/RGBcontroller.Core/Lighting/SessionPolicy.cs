using RGBcontroller.Core.Config;

namespace RGBcontroller.Core.Lighting;

/// <summary>State of the interactive console session, as the Service sees it.</summary>
public enum SessionKind
{
    /// <summary>A user is logged in and the session is unlocked.</summary>
    Unlocked,

    /// <summary>The logged-in user's session is locked.</summary>
    Locked,

    /// <summary>No user owns the console: the logon screen after boot, logoff or "Switch user".</summary>
    LogonScreen,
}

/// <summary>Where the PC is right now: the console session and whether the display is on.</summary>
public readonly record struct LightingSituation(SessionKind Session, bool DisplayOn)
{
    public static LightingSituation Default { get; } = new(SessionKind.Unlocked, DisplayOn: true);

    public override string ToString() => $"{Session}, display {(DisplayOn ? "on" : "off")}";
}

/// <summary>
/// The rows of SPEC 4 that select what is shown: the profile follows the session (logon screen, unlocked, locked),
/// and while the display is off one of the two turn-off sets forces outputs to black. Sleep is handled separately
/// (blackout).
/// </summary>
public static class SessionPolicy
{
    private static readonly TurnOffSet Nothing = new();

    public static string ProfileFor(LightingConfig config, SessionKind session)
    {
        ArgumentNullException.ThrowIfNull(config);
        return session switch
        {
            SessionKind.LogonScreen => config.LogonProfile,
            SessionKind.Locked => config.LockProfile ?? config.ActiveProfile,
            _ => config.ActiveProfile,
        };
    }

    /// <summary>The logon screen counts as locked: nobody is at an unlocked desktop.</summary>
    public static TurnOffSet TurnedOff(LightingConfig config, LightingSituation situation)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (situation.DisplayOn)
        {
            return Nothing;
        }

        return situation.Session == SessionKind.Unlocked ? config.DisplayOff.Unlocked : config.DisplayOff.Locked;
    }
}
