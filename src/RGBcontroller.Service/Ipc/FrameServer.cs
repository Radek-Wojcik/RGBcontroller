using System.IO.Pipes;
using System.Runtime.InteropServices;
using RGBcontroller.Core;
using RGBcontroller.Ipc;
using Serilog;

namespace RGBcontroller.Service.Ipc;

/// <summary>Receives the Agent's screen colors.</summary>
internal interface IScreenFeedSink
{
    /// <summary>Strip colors in LED order, before brightness. Called on a pipe thread; must not block.</summary>
    void OnScreenColors(ReadOnlySpan<RgbColor> colors);

    /// <summary>No screen colors right now (capture lost, Agent gone): show the fallback mode.</summary>
    void OnScreenUnavailable();
}

/// <summary>
/// Server end of <see cref="IpcProtocol.FramePipeName"/> (SPEC 3): the Agent streams strip colors here (at most
/// about 30 Hz, 113 x 3 bytes) and the Service writes them to COM3, so the Service stays the only hardware owner.
/// Only an Agent in the console session drives the strip: an Agent left running in a switched-away session cannot
/// capture and must not override it. The newest console connection wins. Malformed frames end the connection.
/// </summary>
internal sealed class FrameServer : IAsyncDisposable
{
    private const int MaxClients = 4;
    private static readonly TimeSpan BusyRetry = TimeSpan.FromSeconds(2);

    private readonly string _pipeName;
    private readonly int _ledCount;
    private readonly IScreenFeedSink _sink;
    private readonly Func<NamedPipeServerStream, bool> _isConsoleClient;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _sessions = [];
    private readonly Lock _gate = new();
    private Task _acceptLoop = Task.CompletedTask;
    private object? _active;

    /// <param name="isConsoleClient">Whether a connected client runs in the console session; null checks with WTS.</param>
    public FrameServer(string pipeName, int ledCount, IScreenFeedSink sink, ILogger log, Func<NamedPipeServerStream, bool>? isConsoleClient = null)
    {
        _pipeName = pipeName;
        _ledCount = ledCount;
        _sink = sink;
        _isConsoleClient = isConsoleClient ?? (pipe => ServicePipes.ClientSessionId(pipe) == Power.ConsoleSession.ActiveSessionId);
        _log = log.ForContext<FrameServer>();
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
        _log.Information("Frame pipe {Pipe} listening", _pipeName);
        while (!_stop.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = ServicePipes.Create(_pipeName, MaxClients, first);
                first = false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Warning("Frame pipe {Pipe} unavailable: {Error}", _pipeName, ex.Message);
                try
                {
                    await Task.Delay(BusyRetry, _stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
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
                _sessions.Add(Task.Run(() => ServeAsync(pipe)));
            }
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe)
    {
        var token = new object();
        var console = _isConsoleClient(pipe);
        if (console)
        {
            lock (_gate)
            {
                _active = token;
            }

            _log.Information("Agent connected to the frame pipe");
        }
        else
        {
            _log.Information("Frame pipe client outside the console session; its frames are ignored");
        }

        var header = new byte[FrameCodec.HeaderSize];
        var payload = new byte[_ledCount * 3];
        await using (pipe.ConfigureAwait(false))
        {
            try
            {
                while (true)
                {
                    await pipe.ReadExactlyAsync(header, _stop.Token).ConfigureAwait(false);
                    if (!FrameCodec.TryReadHeader(header, out var frame) || !IsAcceptable(frame))
                    {
                        _log.Warning("Malformed frame from the Agent ({Type}, {Length} bytes); closing the connection", frame.Type, frame.PayloadLength);
                        break;
                    }

                    await pipe.ReadExactlyAsync(payload.AsMemory(0, frame.PayloadLength), _stop.Token).ConfigureAwait(false);
                    if (!IsActive(token))
                    {
                        continue;
                    }

                    if (frame.Type == FrameType.StripColors)
                    {
                        _sink.OnScreenColors(MemoryMarshal.Cast<byte, RgbColor>(payload.AsSpan(0, frame.PayloadLength)));
                    }
                    else
                    {
                        _sink.OnScreenUnavailable();
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or EndOfStreamException or OperationCanceledException or ObjectDisposedException)
            {
            }

            var wasActive = false;
            lock (_gate)
            {
                if (_active == token)
                {
                    _active = null;
                    wasActive = true;
                }
            }

            if (wasActive)
            {
                _log.Information("Agent left the frame pipe; the strip shows the fallback");
                _sink.OnScreenUnavailable();
            }
        }
    }

    private bool IsAcceptable(FrameHeader frame) => frame.Type switch
    {
        FrameType.StripColors => frame.PayloadLength == _ledCount * 3,
        FrameType.CaptureUnavailable => frame.PayloadLength == 0,
        _ => false,
    };

    private bool IsActive(object token)
    {
        lock (_gate)
        {
            return _active == token;
        }
    }
}
