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

    /// <summary>
    /// Shows the strip identification pattern (first 3 LEDs green, last 3 red, every 10th white) for
    /// <paramref name="seconds"/>, 0 ends it early; used to write strip.layout by hand (since 1.3).
    /// </summary>
    Task<CommandResult> ShowStripTestAsync(int seconds, CancellationToken cancellationToken);

    /// <summary>Moves the active profile by <paramref name="delta"/> places in config order, wrapping around (since 1.3).</summary>
    Task<CommandResult> StepProfileAsync(int delta, CancellationToken cancellationToken);

    /// <summary>The whole configuration the Service runs on and its revision (since 1.4, for the UI).</summary>
    Task<ConfigSnapshot> GetConfigAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the configuration if nobody changed it since <see cref="ConfigUpdateRequest.ExpectedRevision"/>,
    /// validates, applies and saves it (since 1.4). Lighting, the strip layout, the capture pipeline and hotkeys apply
    /// at once; other strip hardware, OpenRGB and logging settings after a Service restart. The OpenRGB executable path
    /// cannot be changed over IPC (SPEC 9: no process launch).
    /// </summary>
    Task<ConfigUpdateResult> UpdateConfigAsync(ConfigUpdateRequest request, CancellationToken cancellationToken);

    /// <summary>OpenRGB devices the Service drives right now, with their zones (since 1.4).</summary>
    Task<DeviceList> GetDevicesAsync(CancellationToken cancellationToken);

    /// <summary>The colors the strip shows right now, for the UI's live preview; the UI polls it while visible (since 1.5).</summary>
    Task<StripColors> GetStripColorsAsync(CancellationToken cancellationToken);

    /// <summary>Closes and reopens the strip's serial port (diagnostics when the LED box stops reacting) (since 1.6).</summary>
    Task<CommandResult> ReopenStripAsync(CancellationToken cancellationToken);
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
/// <param name="SettingsRevision">
/// Changes whenever <see cref="AgentSettings"/> change (since 1.4); the Agent then calls GetAgentSettingsAsync again.
/// </param>
/// <param name="ConfigRevision">Revision of the whole configuration (since 1.4); the UI reloads it when it changes.</param>
public sealed record AgentState(
    long Sequence,
    ServiceStatus Status,
    IReadOnlyList<string> Profiles,
    string ActiveProfile,
    string ShownProfile,
    AmbilightMode StripMode,
    int StripBrightnessPercent,
    bool CaptureWanted,
    bool AllOff,
    long SettingsRevision = 0,
    long ConfigRevision = 0);

public sealed record AgentSettings(int LedCount, StripLayoutConfig Layout, ScreenCaptureConfig Capture, HotkeysConfig Hotkeys);

/// <summary>One change to the active profile's strip. Null fields stay unchanged.</summary>
/// <param name="BrightnessDelta">Added to the brightness of the current mode, clamped to 0-100.</param>
/// <param name="ToggleSolid">Solid on, or back to the mode before it.</param>
/// <param name="CycleMode">Screen, Radial, Solid, Off, Screen...</param>
public sealed record StripChangeRequest(
    AmbilightMode? Mode = null, int? BrightnessPercent = null, int? BrightnessDelta = null, bool ToggleSolid = false, bool CycleMode = false);

/// <param name="Revision">Increases with every change; pass it back in <see cref="ConfigUpdateRequest.ExpectedRevision"/>.</param>
public sealed record ConfigSnapshot(long Revision, AppConfig Config);

public sealed record ConfigUpdateRequest(long ExpectedRevision, AppConfig Config);

/// <param name="Revision">The revision now in effect (the new one on success).</param>
/// <param name="Errors">Validation errors; the configuration was not changed.</param>
/// <param name="RestartRequired">Saved, but some changes take effect only after the Service restarts.</param>
public sealed record ConfigUpdateResult(bool Success, string? Message, long Revision, IReadOnlyList<string> Errors, bool RestartRequired);

/// <param name="Rgb">R, G, B bytes per LED in strip order (base64 in JSON); empty without a strip.</param>
public sealed record StripColors(byte[] Rgb);

public sealed record DeviceList(IReadOnlyList<DeviceInfo> Devices, int StripLedCount);

/// <param name="Index">SDK device index.</param>
/// <param name="Type">OpenRGB device type number (0 motherboard, 1 DRAM, 2 GPU, 5 keyboard, 6 mouse, ...).</param>
public sealed record DeviceInfo(int Index, string Name, int Type, IReadOnlyList<ZoneInfo> Zones);

/// <param name="Type">OpenRGB zone type: 0 single, 1 linear, 2 matrix.</param>
/// <param name="Resizable">True for addressable headers whose LED count is set in openRgb.zoneSizes.</param>
public sealed record ZoneInfo(string Name, int Type, int LedCount, bool Resizable, int MatrixWidth, int MatrixHeight);
