using Nerdbank.Streams;
using RGBcontroller.Core.Config;
using RGBcontroller.Ipc;

namespace RGBcontroller.Tests.Ipc;

public sealed class IpcContractTests
{
    [Theory]
    [InlineData(1, 0, 1, 0, VersionCompatibility.Compatible)]
    [InlineData(1, 0, 1, 3, VersionCompatibility.Compatible)]
    [InlineData(1, 2, 1, 1, VersionCompatibility.ServerTooOld)]
    [InlineData(2, 0, 1, 9, VersionCompatibility.ServerTooOld)]
    [InlineData(1, 9, 2, 0, VersionCompatibility.ClientTooOld)]
    public void ProtocolVersion_Compatibility(int clientMajor, int clientMinor, int serverMajor, int serverMinor, VersionCompatibility expected)
    {
        var client = new ProtocolVersion(clientMajor, clientMinor);

        Assert.Equal(expected, client.CheckAgainstServer(new ProtocolVersion(serverMajor, serverMinor)));
    }

    [Fact]
    public async Task JsonRpc_HelloAndStatus_RoundTrip()
    {
        var ct = TestContext.Current.CancellationToken;
        var (clientStream, serverStream) = FullDuplexStream.CreatePair();
        using var server = IpcRpc.CreateConnection(serverStream);
        server.AddLocalRpcTarget<IServiceControl>(new FakeService(new ProtocolVersion(1, 0)), null);
        server.StartListening();
        using var client = IpcRpc.CreateConnection(clientStream);
        var proxy = client.Attach<IServiceControl>();
        client.StartListening();

        var hello = await proxy.HelloAsync(new HelloRequest(ClientKind.UI, new ProtocolVersion(1, 0), "0.0.1"), ct);
        var status = await proxy.GetStatusAsync(ct);

        Assert.Equal(VersionCompatibility.Compatible, hello.Compatibility);
        Assert.Equal(new ProtocolVersion(1, 0), hello.ProtocolVersion);
        Assert.Equal(new ServiceStatus(ComponentState.Ok, ComponentState.Degraded, "COM3 reconnecting"), status);
    }

    [Fact]
    public async Task JsonRpc_NewerClient_IsToldServerIsTooOld()
    {
        var ct = TestContext.Current.CancellationToken;
        var (clientStream, serverStream) = FullDuplexStream.CreatePair();
        using var server = IpcRpc.CreateConnection(serverStream);
        server.AddLocalRpcTarget<IServiceControl>(new FakeService(new ProtocolVersion(1, 0)), null);
        server.StartListening();
        using var client = IpcRpc.CreateConnection(clientStream);
        var proxy = client.Attach<IServiceControl>();
        client.StartListening();

        var hello = await proxy.HelloAsync(new HelloRequest(ClientKind.Agent, new ProtocolVersion(2, 0), "9.0.0"), ct);

        Assert.Equal(VersionCompatibility.ServerTooOld, hello.Compatibility);
        Assert.NotNull(hello.Message);
    }

    private static readonly AgentSettings Settings = new(
        60,
        new StripLayoutConfig { StartCorner = StripCorner.TopRight, Clockwise = false, Top = 20, Gaps = [3, 4] },
        new ScreenCaptureConfig { Monitor = @"\\.\DISPLAY2", Gamma = 1.8, LetterboxDetection = false },
        new HotkeysConfig { Bindings = [new("Alt+P", HotkeyAction.Profile, "Night")], BrightnessStepPercent = 5 });

    [Fact]
    public async Task JsonRpc_CarriesAgentSettingsAndStateUnchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        var (clientStream, serverStream) = FullDuplexStream.CreatePair();
        using var server = IpcRpc.CreateConnection(serverStream);
        server.AddLocalRpcTarget<IServiceControl>(new FakeService(IpcProtocol.Current), null);
        server.StartListening();
        using var client = IpcRpc.CreateConnection(clientStream);
        var proxy = client.Attach<IServiceControl>();
        client.StartListening();

        Assert.Equal(Settings, await proxy.GetAgentSettingsAsync(ct));
        var state = await proxy.SubscribeAsync(ct);
        Assert.Equal(AmbilightMode.Solid, state.StripMode);
        Assert.Equal(["Rainbow"], state.Profiles);
        var change = await proxy.ChangeStripAsync(new StripChangeRequest(ToggleSolid: true), ct);
        Assert.Equal(new StripChangeRequest(ToggleSolid: true).ToString(), change.Message);
    }

    private static readonly AppConfig FullConfig = AppConfig.CreateDefault() with
    {
        Strip = new StripConfig { LedCount = 133, PortName = "COM7", Layout = new StripLayoutConfig { Offset = 9, Gaps = [1, 2] } },
        Capture = new ScreenCaptureConfig { CaptureFps = 25, Monitor = @"\.\DISPLAY2" },
    };

    [Fact]
    public async Task JsonRpc_CarriesTheWholeConfigUnchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        var (clientStream, serverStream) = FullDuplexStream.CreatePair();
        using var server = IpcRpc.CreateConnection(serverStream);
        server.AddLocalRpcTarget<IServiceControl>(new FakeService(IpcProtocol.Current), null);
        server.StartListening();
        using var client = IpcRpc.CreateConnection(clientStream);
        var proxy = client.Attach<IServiceControl>();
        client.StartListening();

        var snapshot = await proxy.GetConfigAsync(ct);
        Assert.Equal(7, snapshot.Revision);
        Assert.Equal(FullConfig, snapshot.Config);
        var update = await proxy.UpdateConfigAsync(new ConfigUpdateRequest(snapshot.Revision, snapshot.Config), ct);
        Assert.True(update.Success);
        var devices = await proxy.GetDevicesAsync(ct);
        Assert.Equal(132, devices.Devices[0].Zones[0].LedCount);
    }

    [Fact]
    public void FrameCodec_RoundTripsHeader()
    {
        Span<byte> buffer = stackalloc byte[FrameCodec.HeaderSize];
        var header = new FrameHeader(FrameType.StripColors, Sequence: 42, PayloadLength: 113 * 3);

        FrameCodec.WriteHeader(buffer, header);

        Assert.True(FrameCodec.TryReadHeader(buffer, out var read));
        Assert.Equal(header, read);
        Assert.Equal("RGBF"u8.ToArray(), buffer[..4].ToArray());
    }

    [Theory]
    [InlineData(0, (byte)'X')]
    [InlineData(4, (byte)2)]
    [InlineData(5, (byte)9)]
    [InlineData(15, (byte)0xFF)]
    public void FrameCodec_RejectsMalformedHeaders(int offset, byte value)
    {
        var buffer = new byte[FrameCodec.HeaderSize];
        FrameCodec.WriteHeader(buffer, new FrameHeader(FrameType.Preview, 1, 10));
        buffer[offset] = value;

        Assert.False(FrameCodec.TryReadHeader(buffer, out _));
    }

    [Fact]
    public void FrameCodec_RejectsOversizedPayload()
    {
        var buffer = new byte[FrameCodec.HeaderSize];

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FrameCodec.WriteHeader(buffer, new FrameHeader(FrameType.Preview, 1, FrameCodec.MaxPayloadLength + 1)));
    }

    private sealed class FakeService(ProtocolVersion serverVersion) : IServiceControl
    {
        public Task<HelloResponse> HelloAsync(HelloRequest request, CancellationToken cancellationToken)
        {
            var compatibility = request.ProtocolVersion.CheckAgainstServer(serverVersion);
            var message = compatibility == VersionCompatibility.Compatible ? null : $"Service speaks IPC {serverVersion}.";
            return Task.FromResult(new HelloResponse(serverVersion, "0.0.1", compatibility, message));
        }

        public Task<ServiceStatus> GetStatusAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ServiceStatus(ComponentState.Ok, ComponentState.Degraded, "COM3 reconnecting"));

        public Task<ProfileList> GetProfilesAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ProfileList(["Rainbow"], "Rainbow"));

        public Task<CommandResult> SetActiveProfileAsync(string name, CancellationToken cancellationToken) =>
            Task.FromResult(new CommandResult(true, null));

        public Task<CommandResult> TuneGroupAsync(GroupTuningRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new CommandResult(true, null));

        public Task<AgentState> SubscribeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new AgentState(7, new ServiceStatus(ComponentState.Ok, ComponentState.Ok, null), ["Rainbow"], "Rainbow", "Rainbow",
                AmbilightMode.Solid, 40, CaptureWanted: false, AllOff: false));

        public Task<AgentSettings> GetAgentSettingsAsync(CancellationToken cancellationToken) => Task.FromResult(Settings);

        public Task<CommandResult> ChangeStripAsync(StripChangeRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new CommandResult(true, request.ToString()));

        public Task<CommandResult> SetAllOffAsync(bool off, CancellationToken cancellationToken) => Task.FromResult(new CommandResult(off, null));

        public Task<CommandResult> StepProfileAsync(int delta, CancellationToken cancellationToken) => Task.FromResult(new CommandResult(true, null));

        public Task<CommandResult> ShowStripTestAsync(int seconds, CancellationToken cancellationToken) => Task.FromResult(new CommandResult(true, null));

        public Task<ConfigSnapshot> GetConfigAsync(CancellationToken cancellationToken) => Task.FromResult(new ConfigSnapshot(7, FullConfig));

        public Task<ConfigUpdateResult> UpdateConfigAsync(ConfigUpdateRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new ConfigUpdateResult(request.Config == FullConfig, null, request.ExpectedRevision + 1, [], false));

        public Task<StripColors> GetStripColorsAsync(CancellationToken cancellationToken) => Task.FromResult(new StripColors([1, 2, 3]));

        public Task<DeviceList> GetDevicesAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new DeviceList([new DeviceInfo(0, "Keyboard", 5, [new ZoneInfo("Keys", 2, 132, false, 22, 6)])], 133));
    }
}
