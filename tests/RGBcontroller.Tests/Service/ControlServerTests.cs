using System.IO.Pipes;
using RGBcontroller.Ipc;
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

    private sealed class FakeHost : IControllableHost
    {
        private readonly string[] _profiles = ["Rainbow", "Night"];

        public string Active { get; private set; } = "Rainbow";

        public int SetCalls { get; private set; }

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
