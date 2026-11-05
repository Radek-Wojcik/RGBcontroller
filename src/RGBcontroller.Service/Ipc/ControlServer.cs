using System.IO.Pipes;
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
    private readonly Func<IStateSubscription, IServiceControl> _createEndpoint;
    private readonly List<Connection> _subscribers = [];
    private readonly ILogger _log;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _sessions = [];
    private Task _acceptLoop = Task.CompletedTask;

    /// <summary>All connections share <paramref name="target"/>; nobody can subscribe to state changes.</summary>
    public ControlServer(string pipeName, IServiceControl target, ILogger log)
        : this(pipeName, _ => target, log)
    {
    }

    /// <param name="createEndpoint">Creates the endpoint of one connection; it calls the subscription when the client subscribes.</param>
    public ControlServer(string pipeName, Func<IStateSubscription, IServiceControl> createEndpoint, ILogger log)
    {
        _pipeName = pipeName;
        _createEndpoint = createEndpoint;
        _log = log.ForContext<ControlServer>();
    }

    /// <summary>Sends <paramref name="state"/> to every subscribed client as a JSON-RPC notification. Never blocks.</summary>
    public void Broadcast(AgentState state)
    {
        Connection[] subscribers;
        lock (_subscribers)
        {
            subscribers = [.. _subscribers];
        }

        foreach (var subscriber in subscribers)
        {
            subscriber.Notify(state);
        }
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
            var connection = new Connection(this, rpc);
            rpc.AddLocalRpcTarget<IServiceControl>(_createEndpoint(connection), null);
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
            finally
            {
                lock (_subscribers)
                {
                    _subscribers.Remove(connection);
                }
            }
        }
    }

    private NamedPipeServerStream CreatePipe(bool firstInstance) => ServicePipes.Create(_pipeName, MaxClients, firstInstance);

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

    /// <summary>One client connection. Notifications are sent one at a time, in order, without blocking the caller.</summary>
    private sealed class Connection(ControlServer server, JsonRpc rpc) : IStateSubscription
    {
        private readonly Lock _gate = new();
        private Task _sending = Task.CompletedTask;

        public void Subscribe()
        {
            lock (server._subscribers)
            {
                if (!server._subscribers.Contains(this))
                {
                    server._subscribers.Add(this);
                }
            }
        }

        public void Notify(AgentState state)
        {
            lock (_gate)
            {
                _sending = _sending.ContinueWith(_ => SendAsync(state), TaskScheduler.Default).Unwrap();
            }
        }

        private async Task SendAsync(AgentState state)
        {
            try
            {
                await rpc.NotifyAsync(nameof(IServiceEvents.StateChanged), state).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or ConnectionLostException)
            {
                server._log.Debug("State notification not delivered: {Error}", ex.Message);
            }
        }
    }
}
