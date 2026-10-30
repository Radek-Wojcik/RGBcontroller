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
}

public enum ClientKind
{
    Agent,
    UI,
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

public sealed record ServiceStatus(ComponentState OpenRgb, ComponentState Strip, string? Detail);
