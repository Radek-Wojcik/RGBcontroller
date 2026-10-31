using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using RGBcontroller.Ipc;
using Serilog;
using StreamJsonRpc;

namespace RGBcontroller.Service.Ipc;

/// <summary>
/// JSON-RPC server on the control pipe (SPEC 3). ACL: SYSTEM and Administrators full control, interactive users
/// read/write, so the tray, the UI and the command line can connect without elevation. The surface is
/// <see cref="IServiceControl"/> only: validated app operations, no paths, no process launch (SPEC 9).
/// </summary>
internal sealed class ControlServer : IAsyncDisposable
{
    private const int MaxClients = 8;
    private static readonly TimeSpan BusyRetry = TimeSpan.FromSeconds(2);

    private readonly string _pipeName;
    private readonly IServiceControl _target;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _sessions = [];
    private Task _acceptLoop = Task.CompletedTask;

    public ControlServer(string pipeName, IServiceControl target, ILogger log)
    {
        _pipeName = pipeName;
        _target = target;
        _log = log.ForContext<ControlServer>();
    }

    public void Start() => _acceptLoop = Task.Run(AcceptLoopAsync);

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _acceptLoop.ConfigureAwait(false);
        Task[] sessions;
        lock (_sessions)
        {
            sessions = [.. _sessions];
        }

        await Task.WhenAll(sessions).ConfigureAwait(false);
        _stop.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        var first = true;
        _log.Information("Control pipe {Pipe} listening", _pipeName);
        while (!_stop.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = CreatePipe(first);
                first = false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Another process owns the name (first instance) or all instances are busy.
                _log.Warning("Control pipe {Pipe} unavailable: {Error}", _pipeName, ex.Message);
                if (!await DelayAsync(BusyRetry).ConfigureAwait(false))
                {
                    break;
                }

                continue;
            }

            try
            {
                await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                continue;
            }

            lock (_sessions)
            {
                _sessions.RemoveAll(t => t.IsCompleted);
                _sessions.Add(ServeAsync(pipe));
            }
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe)
    {
        await using (pipe.ConfigureAwait(false))
        {
            using var rpc = IpcRpc.CreateConnection(pipe);
            rpc.AddLocalRpcTarget<IServiceControl>(_target, null);
            rpc.StartListening();
            using var registration = _stop.Token.Register(rpc.Dispose);
            try
            {
                await rpc.Completion.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or RemoteRpcException)
            {
                _log.Debug("Control client disconnected: {Error}", ex.Message);
            }
        }
    }

    private NamedPipeServerStream CreatePipe(bool firstInstance)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));

        // The creating account (SYSTEM for the Service, the developer in console mode and tests) must be able to add instances.
        using var self = WindowsIdentity.GetCurrent();
        security.AddAccessRule(new PipeAccessRule(self.User!, PipeAccessRights.FullControl, AccessControlType.Allow));

        var options = PipeOptions.Asynchronous | (firstInstance ? PipeOptions.FirstPipeInstance : PipeOptions.None);
        return NamedPipeServerStreamAcl.Create(_pipeName, PipeDirection.InOut, MaxClients, PipeTransmissionMode.Byte, options, 0, 0, security);
    }

    private async Task<bool> DelayAsync(TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay, _stop.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
