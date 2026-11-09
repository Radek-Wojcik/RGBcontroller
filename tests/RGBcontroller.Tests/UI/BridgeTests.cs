using System.Text.Json.Nodes;
using RGBcontroller.Core.Config;
using RGBcontroller.Ipc;
using RGBcontroller.UI;

namespace RGBcontroller.Tests.UI;

public sealed class BridgeTests
{
    [Fact]
    public async Task CallsTheServiceAndRepliesWithTheSameId()
    {
        var service = new FakeService();
        var bridge = new Bridge(() => service);
        var ct = TestContext.Current.CancellationToken;

        var reply = JsonNode.Parse(await bridge.HandleAsync("""{"id": 7, "method": "changeStrip", "params": {"mode": "Solid", "brightnessPercent": 80}}""", ct))!;

        Assert.Equal(7, reply["id"]!.GetValue<int>());
        Assert.True(reply["result"]!["success"]!.GetValue<bool>());
        Assert.Equal(new StripChangeRequest(Mode: AmbilightMode.Solid, BrightnessPercent: 80), service.LastStripChange);
    }

    [Fact]
    public async Task ForwardsIdentifyRequestsToTheService()
    {
        var service = new FakeService();
        var bridge = new Bridge(() => service);

        var reply = JsonNode.Parse(await bridge.HandleAsync(
            """{"id": 9, "method": "identify", "params": {"deviceIndex": 2, "zone": "Aura Addressable 1", "start": 0, "count": 20, "seconds": 60}}""",
            TestContext.Current.CancellationToken))!;

        Assert.True(reply["result"]!["success"]!.GetValue<bool>());
        Assert.Equal(new IdentifyRequest(2, "Aura Addressable 1", 0, 20, 60), service.LastIdentify);
    }

    [Fact]
    public async Task SendsTheConfigInTheWireFormat()
    {
        var bridge = new Bridge(() => new FakeService());
        var reply = JsonNode.Parse(await bridge.HandleAsync("""{"id": "a", "method": "getConfig"}""", TestContext.Current.CancellationToken))!;

        Assert.Equal("a", reply["id"]!.GetValue<string>());
        Assert.Equal(3, reply["result"]!["revision"]!.GetValue<int>());
        Assert.Equal("BottomLeft", reply["result"]!["config"]!["strip"]!["layout"]!["startCorner"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("""{"id": 1, "method": "launchProcess", "params": {"path": "cmd.exe"}}""", "Unknown method")]
    [InlineData("""{"id": 1, "method": "setActiveProfile"}""", "Missing parameter")]
    [InlineData("not json", "")]
    [InlineData("""[1, 2]""", "JSON object")]
    public async Task RejectsBadRequestsWithAnError(string request, string error)
    {
        var bridge = new Bridge(() => new FakeService());
        var reply = JsonNode.Parse(await bridge.HandleAsync(request, TestContext.Current.CancellationToken))!;

        Assert.Contains(error, reply["error"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Null(reply["result"]);
    }

    [Fact]
    public async Task SendsStripColorsAsBase64()
    {
        var bridge = new Bridge(() => new FakeService());
        var reply = JsonNode.Parse(await bridge.HandleAsync("""{"id": 2, "method": "getStripColors"}""", TestContext.Current.CancellationToken))!;

        Assert.Equal(Convert.ToBase64String([255, 0, 128]), reply["result"]!["rgb"]!.GetValue<string>());
    }

    [Fact]
    public async Task ReportsAMissingService()
    {
        var bridge = new Bridge(() => null);
        var reply = JsonNode.Parse(await bridge.HandleAsync("""{"id": 1, "method": "getStatus"}""", TestContext.Current.CancellationToken))!;

        Assert.Contains("not connected", reply["error"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    private sealed class FakeService : IServiceControl
    {
        public StripChangeRequest? LastStripChange { get; private set; }

        private static Task<CommandResult> Ok() => Task.FromResult(new CommandResult(true, null));

        public Task<HelloResponse> HelloAsync(HelloRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ServiceStatus> GetStatusAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ServiceStatus(ComponentState.Ok, ComponentState.Ok, null));

        public Task<ProfileList> GetProfilesAsync(CancellationToken cancellationToken) => Task.FromResult(new ProfileList(["Rainbow"], "Rainbow"));

        public Task<CommandResult> SetActiveProfileAsync(string name, CancellationToken cancellationToken) => Ok();

        public Task<CommandResult> TuneGroupAsync(GroupTuningRequest request, CancellationToken cancellationToken) => Ok();

        public Task<AgentState> SubscribeAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AgentSettings> GetAgentSettingsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<CommandResult> ChangeStripAsync(StripChangeRequest request, CancellationToken cancellationToken)
        {
            LastStripChange = request;
            return Ok();
        }

        public Task<CommandResult> SetAllOffAsync(bool off, CancellationToken cancellationToken) => Ok();

        public Task<CommandResult> ShowStripTestAsync(int seconds, CancellationToken cancellationToken) => Ok();

        public Task<CommandResult> ShowStripMarkerAsync(StripMarkerRequest request, CancellationToken cancellationToken) => Ok();

        public Task<CommandResult> StepProfileAsync(int delta, CancellationToken cancellationToken) => Ok();

        public Task<ConfigSnapshot> GetConfigAsync(CancellationToken cancellationToken) => Task.FromResult(new ConfigSnapshot(3, AppConfig.CreateDefault()));

        public Task<ConfigUpdateResult> UpdateConfigAsync(ConfigUpdateRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<DeviceList> GetDevicesAsync(CancellationToken cancellationToken) => Task.FromResult(new DeviceList([], 133));

        public Task<StripColors> GetStripColorsAsync(CancellationToken cancellationToken) => Task.FromResult(new StripColors([255, 0, 128]));

        public Task<CommandResult> ReopenStripAsync(CancellationToken cancellationToken) => Task.FromResult(new CommandResult(true, null));

        public IdentifyRequest? LastIdentify { get; private set; }

        public Task<CommandResult> IdentifyAsync(IdentifyRequest request, CancellationToken cancellationToken)
        {
            LastIdentify = request;
            return Ok();
        }

        public Task<IReadOnlyList<DeviceColors>> GetDeviceColorsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DeviceColors>>([]);
    }
}
