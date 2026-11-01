using RGBcontroller.Ipc;

namespace RGBcontroller.Service.Ipc;

/// <summary>Operations the lighting host offers to IPC clients.</summary>
internal interface IControllableHost
{
    ServiceStatus GetStatus();

    ProfileList GetProfiles();

    /// <returns>Null on success, otherwise a message for the client.</returns>
    string? SetActiveProfile(string name);

    /// <returns>Null on success, otherwise a message for the client.</returns>
    string? TuneGroup(GroupTuningRequest request);
}

/// <summary>Validates IPC input and forwards it to the host. Runs on StreamJsonRpc threads.</summary>
internal sealed class ServiceControlEndpoint(IControllableHost host) : IServiceControl
{
    public const int MaxNameLength = 100;

    private static readonly string AppVersion = typeof(ServiceControlEndpoint).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public Task<HelloResponse> HelloAsync(HelloRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var server = IpcProtocol.Current;
        var compatibility = request.ProtocolVersion.CheckAgainstServer(server);
        var message = compatibility switch
        {
            VersionCompatibility.ClientTooOld => $"The Service speaks IPC {server}; update {request.Client}.",
            VersionCompatibility.ServerTooOld => $"The Service speaks IPC {server}, older than {request.ProtocolVersion}; update the Service.",
            _ => null,
        };
        return Task.FromResult(new HelloResponse(server, AppVersion, compatibility, message));
    }

    public Task<ServiceStatus> GetStatusAsync(CancellationToken cancellationToken) => Task.FromResult(host.GetStatus());

    public Task<ProfileList> GetProfilesAsync(CancellationToken cancellationToken) => Task.FromResult(host.GetProfiles());

    public Task<CommandResult> SetActiveProfileAsync(string name, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength)
        {
            return Task.FromResult(new CommandResult(false, $"A profile name has 1-{MaxNameLength} characters."));
        }

        var error = host.SetActiveProfile(name);
        return Task.FromResult(new CommandResult(error is null, error));
    }

    public Task<CommandResult> TuneGroupAsync(GroupTuningRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsName(request.Group) || (request.Profile is not null && !IsName(request.Profile)))
        {
            return Task.FromResult(new CommandResult(false, $"Group and profile names have 1-{MaxNameLength} characters."));
        }

        if (request.Speed is { } speed && !double.IsFinite(speed) || request.Scale is { } scale && !double.IsFinite(scale))
        {
            return Task.FromResult(new CommandResult(false, "Speed and scale must be finite numbers."));
        }

        var error = host.TuneGroup(request);
        return Task.FromResult(new CommandResult(error is null, error));
    }

    private static bool IsName(string? name) => !string.IsNullOrWhiteSpace(name) && name.Length <= MaxNameLength;
}
