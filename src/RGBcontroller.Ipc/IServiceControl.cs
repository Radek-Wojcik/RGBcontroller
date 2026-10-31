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
