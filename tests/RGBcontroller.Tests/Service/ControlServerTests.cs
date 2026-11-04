using System.IO.Pipes;
using RGBcontroller.Core.Config;
using RGBcontroller.Ipc;
using RGBcontroller.Service;
using RGBcontroller.Service.Ipc;
using Serilog.Core;

namespace RGBcontroller.Tests.Service;

public sealed class ControlServerTests
{
    [Fact]
    public async Task Client_CanQueryAndSwitchProfilesOverTheNamedPipe()
    {
        var ct = TestContext.Current.CancellationToken;
        var pipeName = "RGBcontroller.Test." + Guid.NewGuid().ToString("N");
        var host = new FakeHost();
        await using var server = new ControlServer(pipeName, new ServiceControlEndpoint(host), Logger.None);
        server.Start();

        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000, ct);
        using var rpc = IpcRpc.CreateConnection(pipe);
        var service = rpc.Attach<IServiceControl>();
        rpc.StartListening();

        var hello = await service.HelloAsync(new HelloRequest(ClientKind.Cli, IpcProtocol.Current, "test"), ct);
        Assert.Equal(VersionCompatibility.Compatible, hello.Compatibility);

        var switched = await service.SetActiveProfileAsync("night", ct);
        Assert.True(switched.Success);
        Assert.Equal("Night", (await service.GetProfilesAsync(ct)).ActiveProfile);
        Assert.Equal("Night", (await service.GetStatusAsync(ct)).ActiveProfile);

        var unknown = await service.SetActiveProfileAsync("Disco", ct);
        Assert.False(unknown.Success);
        Assert.Equal("Night", host.Active);

        var tuned = await service.TuneGroupAsync(new GroupTuningRequest("RAM", MaxFps: 5, Speed: 0.3), ct);
        Assert.True(tuned.Success);
        Assert.Equal(new GroupTuningRequest("RAM", MaxFps: 5, Speed: 0.3), host.LastTuning);
    }

    [Fact]
    public async Task Endpoint_RejectsNonFiniteTuning()
    {
        var host = new FakeHost();
        var endpoint = new ServiceControlEndpoint(host);

        var result = await endpoint.TuneGroupAsync(new GroupTuningRequest("RAM", Speed: double.NaN), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Null(host.LastTuning);
    }

    [Fact]
    public async Task Endpoint_RejectsOversizedNamesWithoutTouchingTheHost()
    {
        var host = new FakeHost();
        var endpoint = new ServiceControlEndpoint(host);

        var result = await endpoint.SetActiveProfileAsync(new string('x', 500), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(0, host.SetCalls);
    }

    [Fact]
    public async Task Endpoint_TellsAnOldClientToUpdate()
    {
        var endpoint = new ServiceControlEndpoint(new FakeHost());

        var hello = await endpoint.HelloAsync(new HelloRequest(ClientKind.UI, new ProtocolVersion(0, 9), "0.0.1"), TestContext.Current.CancellationToken);

        Assert.Equal(VersionCompatibility.ClientTooOld, hello.Compatibility);
        Assert.NotNull(hello.Message);
    }

    [Fact]
    public async Task SubscribedClients_ReceiveStateChanges()
    {
        var ct = TestContext.Current.CancellationToken;
        var pipeName = "RGBcontroller.Test." + Guid.NewGuid().ToString("N");
        var host = new FakeHost();
        await using var server = new ControlServer(pipeName, subscription => new ServiceControlEndpoint(host, subscription), Logger.None);
        server.Start();

        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000, ct);
        using var rpc = IpcRpc.CreateConnection(pipe);
        var events = new EventSink();
        rpc.AddLocalRpcTarget<IServiceEvents>(events, null);
        var service = rpc.Attach<IServiceControl>();
        rpc.StartListening();

        server.Broadcast(host.GetAgentState());
        var first = await service.SubscribeAsync(ct);
        Assert.Equal("Rainbow", first.ActiveProfile);

        host.SetAllOff(true);
        server.Broadcast(host.GetAgentState());
        var pushed = await events.Next.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);

        Assert.True(pushed.AllOff);
        Assert.True(pushed.Sequence > first.Sequence);
        Assert.Equal(AmbilightMode.Screen, pushed.StripMode);
    }

    [Fact]
    public async Task Endpoint_ValidatesStripChanges()
    {
        var host = new FakeHost();
        var endpoint = new ServiceControlEndpoint(host);
        var ct = TestContext.Current.CancellationToken;

        Assert.False((await endpoint.ChangeStripAsync(new StripChangeRequest(BrightnessPercent: 101), ct)).Success);
        Assert.False((await endpoint.ChangeStripAsync(new StripChangeRequest(Mode: (AmbilightMode)42), ct)).Success);
        Assert.False((await endpoint.StepProfileAsync(1000, ct)).Success);
        Assert.Null(host.LastStripChange);

        Assert.True((await endpoint.ChangeStripAsync(new StripChangeRequest(BrightnessDelta: -10), ct)).Success);
        Assert.Equal(new StripChangeRequest(BrightnessDelta: -10), host.LastStripChange);
    }

    [Fact]
    public async Task Endpoint_RejectsIncompleteConfigs()
    {
        var host = new FakeHost();
        var endpoint = new ServiceControlEndpoint(host);
        var ct = TestContext.Current.CancellationToken;

        Assert.False((await endpoint.UpdateConfigAsync(null!, ct)).Success);
        Assert.False((await endpoint.UpdateConfigAsync(new ConfigUpdateRequest(1, AppConfig.CreateDefault() with { Lighting = null! }), ct)).Success);
        Assert.Null(host.Updated);
        Assert.True((await endpoint.UpdateConfigAsync(new ConfigUpdateRequest(1, AppConfig.CreateDefault()), ct)).Success);
    }

    [Fact]
    public void ConfigEdits_CannotChangeTheOpenRgbExecutable()
    {
        var current = AppConfig.CreateDefault();
        var proposed = current with { SchemaVersion = 99, OpenRgb = current.OpenRgb with { ExecutablePath = @"C:\evil.exe", Port = 7000 } };

        var kept = LightingHost.ProtectConfig(current, proposed);

        Assert.Equal(current.OpenRgb.ExecutablePath, kept.OpenRgb.ExecutablePath);
        Assert.Equal(current.SchemaVersion, kept.SchemaVersion);
        Assert.Equal(7000, kept.OpenRgb.Port);
    }

    [Fact]
    public void ConfigEdits_ReportWhatNeedsARestart()
    {
        var current = AppConfig.CreateDefault();

        Assert.False(LightingHost.NeedsRestart(current, current with { Strip = current.Strip with { Layout = new StripLayoutConfig { Top = 41 } } }));
        Assert.False(LightingHost.NeedsRestart(current, current with { Capture = new ScreenCaptureConfig { Gamma = 2.2 } }));
        Assert.True(LightingHost.NeedsRestart(current, current with { Strip = current.Strip with { LedCount = 133 } }));
        Assert.True(LightingHost.NeedsRestart(current, current with { OpenRgb = current.OpenRgb with { Port = 7000 } }));
    }

    private sealed class EventSink : IServiceEvents
    {
        public TaskCompletionSource<AgentState> Next { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void StateChanged(AgentState state) => Next.TrySetResult(state);
    }

    private sealed class FakeHost : IControllableHost
    {
        private long _sequence;

        public bool AllOff { get; private set; }

        public StripChangeRequest? LastStripChange { get; private set; }

        public AgentState GetAgentState() =>
            new(++_sequence, GetStatus(), _profiles, Active, Active, AmbilightMode.Screen, 50, CaptureWanted: true, AllOff);

        public AgentSettings GetAgentSettings() => new(113, new StripLayoutConfig(), new ScreenCaptureConfig(), new HotkeysConfig());

        public string? ChangeStrip(StripChangeRequest request)
        {
            LastStripChange = request;
            return null;
        }

        public void SetAllOff(bool off) => AllOff = off;

        public string? StepProfile(int delta) => null;

        public AppConfig? Updated { get; private set; }

        public ConfigSnapshot GetConfig() => new(1, AppConfig.CreateDefault());

        public ConfigUpdateResult UpdateConfig(long expectedRevision, AppConfig config)
        {
            Updated = config;
            return new ConfigUpdateResult(true, null, expectedRevision + 1, [], false);
        }

        public DeviceList GetDevices() => new([], 113);

        public void ShowStripTest(TimeSpan duration)
        {
        }

        private readonly string[] _profiles = ["Rainbow", "Night"];

        public string Active { get; private set; } = "Rainbow";

        public int SetCalls { get; private set; }

        public GroupTuningRequest? LastTuning { get; private set; }

        public string? TuneGroup(GroupTuningRequest request)
        {
            LastTuning = request;
            return null;
        }

        public ServiceStatus GetStatus() => new(ComponentState.Ok, ComponentState.Ok, null, Active);

        public ProfileList GetProfiles() => new(_profiles, Active);

        public string? SetActiveProfile(string name)
        {
            SetCalls++;
            var match = _profiles.FirstOrDefault(p => string.Equals(p, name, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                return $"No profile named '{name}'.";
            }

            Active = match;
            return null;
        }
    }
}
