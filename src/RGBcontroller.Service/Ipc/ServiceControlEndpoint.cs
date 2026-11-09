using RGBcontroller.Core.Config;
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

    AgentState GetAgentState();

    AgentSettings GetAgentSettings();

    /// <returns>Null on success, otherwise a message for the client.</returns>
    string? ChangeStrip(StripChangeRequest request);

    void SetAllOff(bool off);

    /// <returns>Null on success, otherwise a message for the client.</returns>
    string? StepProfile(int delta);

    void ShowStripTest(TimeSpan duration);

    void ShowStripMarker(int start, int count, TimeSpan duration);

    ConfigSnapshot GetConfig();

    ConfigUpdateResult UpdateConfig(long expectedRevision, AppConfig config);

    DeviceList GetDevices();

    StripColors GetStripColors();

    IReadOnlyList<DeviceColors> GetDeviceColors();

    /// <returns>Null on success, otherwise a message for the client.</returns>
    string? ReopenStrip();

    LogTail GetLogTail(string component, int lines);

    /// <returns>Null on success, otherwise a message for the client.</returns>
    string? RestartOpenRgb();

    /// <returns>Null on success, otherwise a message for the client.</returns>
    string? RescanDevices();

    /// <param name="duration">Zero ends the identification.</param>
    /// <returns>Null on success, otherwise a message for the client.</returns>
    string? Identify(IdentifyRequest request, TimeSpan duration);
}

/// <summary>The control connection a request arrived on; subscribing makes the server push state changes to it.</summary>
internal interface IStateSubscription
{
    void Subscribe();
}

/// <summary>Validates IPC input and forwards it to the host. One instance per connection; runs on StreamJsonRpc threads.</summary>
internal sealed class ServiceControlEndpoint(IControllableHost host, IStateSubscription? subscription = null) : IServiceControl
{
    public const int MaxNameLength = 100;

    /// <summary>Largest LED range one identification may ask for (the biggest zone is a few hundred LEDs).</summary>
    public const int MaxIdentifyLeds = 1000;

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

    public Task<AgentState> SubscribeAsync(CancellationToken cancellationToken)
    {
        subscription?.Subscribe();
        return Task.FromResult(host.GetAgentState());
    }

    public Task<AgentSettings> GetAgentSettingsAsync(CancellationToken cancellationToken) => Task.FromResult(host.GetAgentSettings());

    public Task<CommandResult> ChangeStripAsync(StripChangeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Mode is { } mode && !Enum.IsDefined(mode))
        {
            return Task.FromResult(new CommandResult(false, $"Unknown strip mode {mode}."));
        }

        if (request.BrightnessPercent is < 0 or > 100 || request.BrightnessDelta is < -100 or > 100)
        {
            return Task.FromResult(new CommandResult(false, "Brightness is 0-100 and a step -100 to 100."));
        }

        var error = host.ChangeStrip(request);
        return Task.FromResult(new CommandResult(error is null, error));
    }

    public Task<CommandResult> SetAllOffAsync(bool off, CancellationToken cancellationToken)
    {
        host.SetAllOff(off);
        return Task.FromResult(new CommandResult(true, null));
    }

    public Task<CommandResult> ShowStripTestAsync(int seconds, CancellationToken cancellationToken)
    {
        if (seconds is < 0 or > 600)
        {
            return Task.FromResult(new CommandResult(false, "The strip test lasts 0-600 seconds."));
        }

        host.ShowStripTest(TimeSpan.FromSeconds(seconds));
        return Task.FromResult(new CommandResult(true, null));
    }

    public Task<CommandResult> ShowStripMarkerAsync(StripMarkerRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Seconds is < 0 or > 600 || request.Start is < 0 or > MaxIdentifyLeds || request.Count is < 1 or > MaxIdentifyLeds)
        {
            return Task.FromResult(new CommandResult(false, $"A strip marker lights 1-{MaxIdentifyLeds} LEDs for 0-600 seconds."));
        }

        host.ShowStripMarker(request.Start, request.Count, TimeSpan.FromSeconds(request.Seconds));
        return Task.FromResult(new CommandResult(true, null));
    }

    public Task<ConfigSnapshot> GetConfigAsync(CancellationToken cancellationToken) => Task.FromResult(host.GetConfig());

    public Task<ConfigUpdateResult> UpdateConfigAsync(ConfigUpdateRequest request, CancellationToken cancellationToken)
    {
        // A JSON payload can leave any member null despite the non-nullable types; the validator expects whole objects.
        if (request?.Config is not { } config || config.Strip?.Layout is null || config.OpenRgb is null || config.Lighting is null
            || config.Capture is null || config.Hotkeys is null || config.Logging is null)
        {
            return Task.FromResult(new ConfigUpdateResult(false, "The configuration is incomplete.", 0, [], false));
        }

        try
        {
            return Task.FromResult(host.UpdateConfig(request.ExpectedRevision, config));
        }
        catch (NullReferenceException)
        {
            return Task.FromResult(new ConfigUpdateResult(false, "The configuration is incomplete.", 0, [], false));
        }
    }

    public Task<DeviceList> GetDevicesAsync(CancellationToken cancellationToken) => Task.FromResult(host.GetDevices());

    public Task<StripColors> GetStripColorsAsync(CancellationToken cancellationToken) => Task.FromResult(host.GetStripColors());

    public Task<IReadOnlyList<DeviceColors>> GetDeviceColorsAsync(CancellationToken cancellationToken) => Task.FromResult(host.GetDeviceColors());

    public Task<CommandResult> IdentifyAsync(IdentifyRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Seconds is < 0 or > 300)
        {
            return Task.FromResult(new CommandResult(false, "An identification lasts 0-300 seconds."));
        }

        if (request.Seconds > 0
            && (request.DeviceIndex < 0 || request.Start < 0 || request.Count is < 1 or > MaxIdentifyLeds
                || (request.Zone is not null && !IsName(request.Zone))))
        {
            return Task.FromResult(new CommandResult(false, $"Identify needs a device, a zone name of 1-{MaxNameLength} characters, a start of 0 or more and 1-{MaxIdentifyLeds} LEDs."));
        }

        var error = host.Identify(request, TimeSpan.FromSeconds(request.Seconds));
        return Task.FromResult(new CommandResult(error is null, error));
    }

    public Task<CommandResult> ReopenStripAsync(CancellationToken cancellationToken)
    {
        var error = host.ReopenStrip();
        return Task.FromResult(new CommandResult(error is null, error));
    }

    public Task<LogTail> GetLogTailAsync(LogTailRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Component != "service" || request.Lines is < 1 or > LogTailRequest.MaxLines)
        {
            throw new ArgumentException($"A log tail is 1-{LogTailRequest.MaxLines} lines of the 'service' log (the Agent logs in the user profile, out of the Service's reach).");
        }

        return Task.FromResult(host.GetLogTail(request.Component, request.Lines));
    }

    public Task<CommandResult> RestartOpenRgbAsync(CancellationToken cancellationToken)
    {
        var error = host.RestartOpenRgb();
        return Task.FromResult(new CommandResult(error is null, error));
    }

    public Task<CommandResult> RescanDevicesAsync(CancellationToken cancellationToken)
    {
        var error = host.RescanDevices();
        return Task.FromResult(new CommandResult(error is null, error));
    }

    public Task<CommandResult> StepProfileAsync(int delta, CancellationToken cancellationToken)
    {
        if (delta is < -100 or > 100)
        {
            return Task.FromResult(new CommandResult(false, "A profile step is -100 to 100."));
        }

        var error = host.StepProfile(delta);
        return Task.FromResult(new CommandResult(error is null, error));
    }
}
