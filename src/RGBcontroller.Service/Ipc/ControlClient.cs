using System.IO.Pipes;
using RGBcontroller.Ipc;
using StreamJsonRpc;

namespace RGBcontroller.Service.Ipc;

/// <summary>The command-line side of the control pipe: <c>--status</c> and <c>--profile &lt;name&gt;</c>.</summary>
internal static class ControlClient
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);

    /// <param name="profile">Profile to activate, or null to only print the status.</param>
    public static async Task<int> RunAsync(string? profile, string pipeName = IpcProtocol.ControlPipeName)
    {
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync((int)ConnectTimeout.TotalMilliseconds).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Console.Error.WriteLine($"The RGBcontroller Service is not running (no control pipe '{pipeName}').");
            return 1;
        }

        using var rpc = IpcRpc.CreateConnection(pipe);
        var service = rpc.Attach<IServiceControl>();
        rpc.StartListening();
        try
        {
            var hello = await service.HelloAsync(new HelloRequest(ClientKind.Cli, IpcProtocol.Current, "cli"), CancellationToken.None)
                .ConfigureAwait(false);
            if (hello.Compatibility != VersionCompatibility.Compatible)
            {
                Console.Error.WriteLine(hello.Message);
                return 1;
            }

            if (profile is not null)
            {
                var result = await service.SetActiveProfileAsync(profile, CancellationToken.None).ConfigureAwait(false);
                if (!result.Success)
                {
                    Console.Error.WriteLine(result.Message);
                    return 1;
                }
            }

            var status = await service.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
            var profiles = await service.GetProfilesAsync(CancellationToken.None).ConfigureAwait(false);
            Console.WriteLine($"RGBcontroller.Service {hello.AppVersion} (IPC {hello.ProtocolVersion})");
            Console.WriteLine($"  OpenRGB: {status.OpenRgb}   strip: {status.Strip}   ({status.Detail})");
            Console.WriteLine($"  Active profile: {profiles.ActiveProfile}");
            Console.WriteLine($"  Profiles: {string.Join(", ", profiles.Profiles)}");
            return 0;
        }
        catch (Exception ex) when (ex is RemoteRpcException or ConnectionLostException or IOException)
        {
            Console.Error.WriteLine($"Control pipe error: {ex.Message}");
            return 1;
        }
    }
}
