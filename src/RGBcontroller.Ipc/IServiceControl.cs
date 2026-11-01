using RGBcontroller.Core.Config;

namespace RGBcontroller.Ipc;

/// <summary>
/// JSON-RPC surface the Service exposes on <see cref="IpcProtocol.ControlPipeName"/>.
/// Every method takes validated, app-level arguments only: no paths, no process launch (SPEC section 9).
/// Clients must call <see cref="HelloAsync"/> first and stop if the result is not compatible.
/// </summary>
public interface IServiceControl
{
    Task<HelloResponse> HelloAsync(HelloRequest request, CancellationToken cancellationToken);

    Task<ServiceStatus> GetStatusAsync(CancellationToken cancellationToken);

    /// <summary>Profile names in config order and the active one (since 1.1).</summary>
    Task<ProfileList> GetProfilesAsync(CancellationToken cancellationToken);

    /// <summary>Switches the active profile and saves it (since 1.1). Unknown names are rejected, not created.</summary>
    Task<CommandResult> SetActiveProfileAsync(string name, CancellationToken cancellationToken);

    /// <summary>Changes a group's frame rate and its effect's speed, scale or brightness, applies and saves it (since 1.2).</summary>
    Task<CommandResult> TuneGroupAsync(GroupTuningRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the current <see cref="AgentState"/>; afterwards the Service sends <see cref="IServiceEvents.StateChanged"/>
    /// notifications on this connection whenever it changes (since 1.3).
    /// </summary>
    Task<AgentState> SubscribeAsync(CancellationToken cancellationToken);

    /// <summary>Strip layout, capture pipeline and hotkeys from the Service's config (since 1.3).</summary>
    Task<AgentSettings> GetAgentSettingsAsync(CancellationToken cancellationToken);

    /// <summary>Strip mode or brightness of the active profile, saved (since 1.3).</summary>
    Task<CommandResult> ChangeStripAsync(StripChangeRequest request, CancellationToken cancellationToken);

    /// <summary>Everything off (true) or back on; not saved, a Service restart turns everything on (since 1.3).</summary>
    Task<CommandResult> SetAllOffAsync(bool off, CancellationToken cancellationToken);

    /// <summary>Moves the active profile by <paramref name="delta"/> places in config order, wrapping around (since 1.3).</summary>
    Task<CommandResult> StepProfileAsync(int delta, CancellationToken cancellationToken);
}

/// <summary>Notifications the Service sends to subscribed clients (JSON-RPC notifications, no reply).</summary>
public interface IServiceEvents
{
    void StateChanged(AgentState state);
}

public enum ClientKind
{
    Agent,
    UI,

    /// <summary>The Service executable's command-line switches (--status, --profile).</summary>
    Cli,
}

public sealed record HelloRequest(ClientKind Client, ProtocolVersion ProtocolVersion, string AppVersion);

public sealed record HelloResponse(
    ProtocolVersion ProtocolVersion,
    string AppVersion,
    VersionCompatibility Compatibility,
    string? Message);

public enum ComponentState
{
    Unknown,
    Starting,
    Ok,
    Degraded,
    Offline,
}

public sealed record ServiceStatus(ComponentState OpenRgb, ComponentState Strip, string? Detail, string? ActiveProfile = null);

public sealed record ProfileList(IReadOnlyList<string> Profiles, string ActiveProfile);

public sealed record CommandResult(bool Success, string? Message);

/// <summary>Null fields stay unchanged. <paramref name="Profile"/> null means the active profile.</summary>
public sealed record GroupTuningRequest(
    string Group, string? Profile = null, int? MaxFps = null, double? Speed = null, double? Scale = null, int? BrightnessPercent = null);

/// <summary>What the tray and the capture need to know; sent on subscribe and on every change.</summary>
/// <param name="Sequence">Increases with every state; a client ignores a state older than the one it has.</param>
/// <param name="ShownProfile">Profile in effect right now (logon, lock or active profile).</param>
/// <param name="StripMode">Ambilight mode of the active profile.</param>
/// <param name="StripBrightnessPercent">Brightness of that mode (Solid: its power).</param>
/// <param name="CaptureWanted">True while the strip should mirror the screen: Screen mode, unlocked, display on, nothing off.</param>
public sealed record AgentState(
    long Sequence,
    ServiceStatus Status,
    IReadOnlyList<string> Profiles,
    string ActiveProfile,
    string ShownProfile,
    AmbilightMode StripMode,
    int StripBrightnessPercent,
    bool CaptureWanted,
    bool AllOff);

public sealed record AgentSettings(int LedCount, StripLayoutConfig Layout, ScreenCaptureConfig Capture, HotkeysConfig Hotkeys);

/// <summary>One change to the active profile's strip. Null fields stay unchanged.</summary>
/// <param name="BrightnessDelta">Added to the brightness of the current mode, clamped to 0-100.</param>
/// <param name="ToggleSolid">Solid on, or back to the mode before it.</param>
/// <param name="CycleMode">Screen, Radial, Solid, Off, Screen...</param>
public sealed record StripChangeRequest(
    AmbilightMode? Mode = null, int? BrightnessPercent = null, int? BrightnessDelta = null, bool ToggleSolid = false, bool CycleMode = false);
