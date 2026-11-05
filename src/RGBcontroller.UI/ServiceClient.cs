using System.IO;
using System.IO.Pipes;
using RGBcontroller.Ipc;
using Serilog;
using StreamJsonRpc;

namespace RGBcontroller.UI;

/// <summary>
/// The UI's control connection to the Service: hello as <see cref="ClientKind.UI"/>, subscribe to state changes,
/// reconnect with back-off while the Service is stopped or restarting. Events are raised on the thread pool.
/// </summary>
internal sealed class ServiceClient : IAsyncDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)];
    private static readonly string AppVersion = typeof(ServiceClient).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private readonly ILogger _log;
    private readonly CancellationTokenSource _stop = new();
    private Task _loop = Task.CompletedTask;
    private volatile IServiceControl? _service;

    public ServiceClient(ILogger log) => _log = log.ForContext<ServiceClient>();

    /// <summary>The connected Service, or null.</summary>
    public IServiceControl? Service => _service;

    /// <summary>Connected (with the Service version) or lost (null and the reason).</summary>
    public event Action<string?, string?>? ConnectionChanged;

    public event Action<AgentState>? StateChanged;

    public void Start() => _loop = Task.Run(RunAsync);

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _loop.ConfigureAwait(false);
        _stop.Dispose();
    }

    private async Task RunAsync()
    {
        var attempt = 0;
        string? lastError = null;
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeClientStream(".", IpcProtocol.ControlPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(ConnectTimeout, _stop.Token).ConfigureAwait(false);
                using var rpc = IpcRpc.CreateConnection(pipe);
                rpc.AddLocalRpcTarget<IServiceEvents>(new Events(this), null);
                var service = rpc.Attach<IServiceControl>();
                rpc.StartListening();

                var hello = await service.HelloAsync(new HelloRequest(ClientKind.UI, IpcProtocol.Current, AppVersion), _stop.Token).ConfigureAwait(false);
                if (hello.Compatibility != VersionCompatibility.Compatible)
                {
                    throw new IOException(hello.Message ?? $"The Service {hello.AppVersion} is not compatible.");
                }

                var state = await service.SubscribeAsync(_stop.Token).ConfigureAwait(false);
                _service = service;
                attempt = 0;
                lastError = null;
                _log.Information("Connected to RGBcontroller.Service {Version} (IPC {Protocol})", hello.AppVersion, hello.ProtocolVersion);
                ConnectionChanged?.Invoke(hello.AppVersion, null);
                StateChanged?.Invoke(state);
                await rpc.Completion.WaitAsync(_stop.Token).ConfigureAwait(false);
                lastError = "The Service closed the connection.";
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                lastError = "The RGBcontroller Service is not running.";
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or RemoteRpcException or ConnectionLostException or UnauthorizedAccessException)
            {
                lastError = ex.Message;
            }
            finally
            {
                _service = null;
            }

            if (attempt++ == 0)
            {
                _log.Information("Service not connected: {Error}", lastError);
            }

            ConnectionChanged?.Invoke(null, lastError);
            try
            {
                await Task.Delay(Backoff[Math.Min(attempt - 1, Backoff.Length - 1)], _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private sealed class Events(ServiceClient owner) : IServiceEvents
    {
        public void StateChanged(AgentState state) => owner.StateChanged?.Invoke(state);
    }
}
