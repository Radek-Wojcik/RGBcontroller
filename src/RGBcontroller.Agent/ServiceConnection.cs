using System.IO.Pipes;
using System.Runtime.InteropServices;
using RGBcontroller.Agent.Capture;
using RGBcontroller.Core;
using RGBcontroller.Ipc;
using Serilog;
using StreamJsonRpc;

namespace RGBcontroller.Agent;

/// <summary>
/// The Agent's link to the Service: the control pipe (JSON-RPC, state notifications) and the frame pipe (strip
/// colors). Reconnects with back-off while the Service is stopped or restarting; nothing lighting-critical depends
/// on the Agent, so failures are only logged. Events are raised on the UI thread.
/// </summary>
internal sealed class ServiceConnection : IStripFrameSink, IDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)];
    private static readonly TimeSpan IncompatibleRetry = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan FramePipeRetry = TimeSpan.FromSeconds(1);
    private static readonly string AppVersion = typeof(ServiceConnection).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private readonly SynchronizationContext _ui;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _frameLock = new();
    private Task _loop = Task.CompletedTask;
    private volatile IServiceControl? _service;
    private long _lastSequence;
    private long _settingsRevision;
    private NamedPipeClientStream? _frames;
    private byte[] _frameBuffer = [];
    private uint _frameSequence;
    private long _nextFrameConnect;

    public ServiceConnection(SynchronizationContext ui, ILogger log)
    {
        _ui = ui;
        _log = log.ForContext<ServiceConnection>();
    }

    /// <summary>New state from the Service; null when the connection is lost.</summary>
    public event Action<AgentState?>? StateChanged;

    /// <summary>Strip layout, capture and hotkey settings, sent after every (re)connect.</summary>
    public event Action<AgentSettings>? SettingsChanged;

    public void Start() => _loop = Task.Run(RunAsync);

    /// <summary>Sends a command if connected; failures are logged, never thrown (hotkeys and menu clicks).</summary>
    public void Send(string what, Func<IServiceControl, Task<CommandResult>> command)
    {
        var service = _service;
        if (service is null)
        {
            _log.Information("{What} ignored: the Service is not connected", what);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var result = await command(service).ConfigureAwait(false);
                if (!result.Success)
                {
                    _log.Warning("{What} refused: {Message}", what, result.Message);
                }
            }
            catch (Exception ex) when (ex is RemoteRpcException or ConnectionLostException or ObjectDisposedException or IOException)
            {
                _log.Warning("{What} failed: {Error}", what, ex.Message);
            }
        });
    }

    public void SendColors(ReadOnlySpan<RgbColor> colors) => WriteFrame(FrameType.StripColors, MemoryMarshal.AsBytes(colors));

    public void SendUnavailable() => WriteFrame(FrameType.CaptureUnavailable, []);

    public void Dispose()
    {
        if (_stop.IsCancellationRequested)
        {
            return;
        }

        _stop.Cancel();
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        lock (_frameLock)
        {
            _frames?.Dispose();
            _frames = null;
        }

        _stop.Dispose();
    }

    private async Task RunAsync()
    {
        var attempt = 0;
        while (!_stop.IsCancellationRequested)
        {
            var retry = Backoff[Math.Min(attempt, Backoff.Length - 1)];
            try
            {
                await using var pipe = new NamedPipeClientStream(".", IpcProtocol.ControlPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(ConnectTimeout, _stop.Token).ConfigureAwait(false);
                using var rpc = IpcRpc.CreateConnection(pipe);
                rpc.AddLocalRpcTarget<IServiceEvents>(new Events(this), null);
                var service = rpc.Attach<IServiceControl>();
                rpc.StartListening();

                var hello = await service.HelloAsync(new HelloRequest(ClientKind.Agent, IpcProtocol.Current, AppVersion), _stop.Token).ConfigureAwait(false);
                if (hello.Compatibility != VersionCompatibility.Compatible)
                {
                    _log.Error("Service {Version} is not compatible: {Message}", hello.AppVersion, hello.Message);
                    retry = IncompatibleRetry;
                    throw new IOException("Incompatible Service.");
                }

                Interlocked.Exchange(ref _lastSequence, 0);
                var state = await service.SubscribeAsync(_stop.Token).ConfigureAwait(false);
                Interlocked.Exchange(ref _settingsRevision, state.SettingsRevision);
                var settings = await service.GetAgentSettingsAsync(_stop.Token).ConfigureAwait(false);
                _ui.Post(_ => SettingsChanged?.Invoke(settings), null);
                _service = service;
                Publish(state);
                _log.Information("Connected to RGBcontroller.Service {Version} (IPC {Protocol})", hello.AppVersion, hello.ProtocolVersion);
                attempt = 0;

                await rpc.Completion.WaitAsync(_stop.Token).ConfigureAwait(false);
                _log.Information("Service connection closed");
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or RemoteRpcException or ConnectionLostException or UnauthorizedAccessException)
            {
                if (attempt == 0 && retry != IncompatibleRetry)
                {
                    _log.Information("Service not reachable ({Error}); retrying", ex.Message);
                }

                attempt++;
            }
            catch (OperationCanceledException)
            {
                // The connect timeout elapsed: the Service is not running.
                attempt++;
            }
            finally
            {
                if (_service is not null)
                {
                    _service = null;
                    _ui.Post(_ => StateChanged?.Invoke(null), null);
                }
            }

            try
            {
                await Task.Delay(retry, _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void Publish(AgentState state)
    {
        // Notifications and the subscribe reply can arrive out of order; the sequence keeps the newest.
        var last = Interlocked.Read(ref _lastSequence);
        while (state.Sequence > last)
        {
            var seen = Interlocked.CompareExchange(ref _lastSequence, state.Sequence, last);
            if (seen == last)
            {
                _ui.Post(_ => StateChanged?.Invoke(state), null);
                if (Interlocked.Exchange(ref _settingsRevision, state.SettingsRevision) != state.SettingsRevision)
                {
                    RefreshSettings();
                }

                return;
            }

            last = seen;
        }
    }

    /// <summary>Strip layout, capture or hotkeys changed in the Service (UI edit); fetch them again.</summary>
    private void RefreshSettings()
    {
        var service = _service;
        if (service is null)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var settings = await service.GetAgentSettingsAsync(_stop.Token).ConfigureAwait(false);
                _ui.Post(_ => SettingsChanged?.Invoke(settings), null);
                _log.Information("Settings changed in the Service; reloaded");
            }
            catch (Exception ex) when (ex is RemoteRpcException or ConnectionLostException or ObjectDisposedException or IOException or OperationCanceledException)
            {
                _log.Warning("Could not reload the settings: {Error}", ex.Message);
            }
        });
    }

    /// <summary>Writes one frame on the capture thread; reconnects the frame pipe at most once a second.</summary>
    private void WriteFrame(FrameType type, ReadOnlySpan<byte> payload)
    {
        lock (_frameLock)
        {
            if (_frames is null && !TryConnectFrames())
            {
                return;
            }

            var length = FrameCodec.HeaderSize + payload.Length;
            if (_frameBuffer.Length < length)
            {
                _frameBuffer = new byte[length];
            }

            FrameCodec.WriteHeader(_frameBuffer, new FrameHeader(type, ++_frameSequence, payload.Length));
            payload.CopyTo(_frameBuffer.AsSpan(FrameCodec.HeaderSize));
            try
            {
                _frames!.Write(_frameBuffer, 0, length);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                _log.Information("Frame pipe lost ({Error})", ex.Message);
                _frames!.Dispose();
                _frames = null;
            }
        }
    }

    private bool TryConnectFrames()
    {
        var now = Environment.TickCount64;
        if (now < _nextFrameConnect || _service is null)
        {
            return false;
        }

        _nextFrameConnect = now + (long)FramePipeRetry.TotalMilliseconds;
        var pipe = new NamedPipeClientStream(".", IpcProtocol.FramePipeName, PipeDirection.Out);
        try
        {
            pipe.Connect((int)ConnectTimeout.TotalMilliseconds);
            _frames = pipe;
            _log.Information("Frame pipe connected");
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            pipe.Dispose();
            _log.Warning("Frame pipe not available: {Error}", ex.Message);
            return false;
        }
    }

    /// <summary>Receives the Service's JSON-RPC notifications (RPC thread).</summary>
    private sealed class Events(ServiceConnection owner) : IServiceEvents
    {
        public void StateChanged(AgentState state) => owner.Publish(state);
    }
}
