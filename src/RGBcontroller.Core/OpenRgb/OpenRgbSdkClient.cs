using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace RGBcontroller.Core.OpenRgb;

/// <summary>
/// Minimal OpenRGB SDK client for protocol 4-5 (OpenRGB 1.0rc2). Written because OpenRGB.NET 3.1.1 stops at
/// protocol 4, has no rescan request, is not thread-safe and can hang when the server dies (spike R2).
/// <para>
/// Requests are serialized: one request is in flight at a time and its reply is matched by packet id and
/// device index. LED writes are fire-and-forget (the protocol has no reply) and reuse one send buffer, so
/// they do not allocate. Any socket error ends the connection: the owner disposes this instance and
/// connects a new one (the supervisor's reconnect loop).
/// </para>
/// </summary>
public sealed class OpenRgbSdkClient : IAsyncDisposable
{
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(5);

    private readonly Socket _socket;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly Lock _sendLock = new();
    private readonly byte[] _headerBuffer = new byte[OpenRgbProtocol.HeaderSize];
    private byte[] _sendBuffer = new byte[4096];
    private Task _readLoop = Task.CompletedTask;
    private PendingRequest? _pending;
    private int _disconnected;

    private OpenRgbSdkClient(Socket socket)
    {
        _socket = socket;
    }

    /// <summary>Negotiated protocol version, min(server, <see cref="OpenRgbProtocol.MaxProtocolVersion"/>).</summary>
    public uint ProtocolVersion { get; private set; }

    /// <summary>Protocol version reported by the server.</summary>
    public uint ServerProtocolVersion { get; private set; }

    public bool IsConnected => Volatile.Read(ref _disconnected) == 0;

    /// <summary>Raised on the read thread for every DEVICE_LIST_UPDATED packet (several per rescan). Device indices are invalid until the list settles.</summary>
    public event Action? DeviceListUpdated;

    /// <summary>Raised once when the connection ends; the argument is the cause, or null for a clean dispose.</summary>
    public event Action<Exception?>? Disconnected;

    public static async Task<OpenRgbSdkClient> ConnectAsync(
        string host, int port, string clientName, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        // SendTimeout keeps a stalled server from blocking the render loop; a timeout ends the connection.
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true, SendTimeout = 2000 };
        using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            connectTimeout.CancelAfter(timeout);
            try
            {
                await socket.ConnectAsync(host, port, connectTimeout.Token).ConfigureAwait(false);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        var client = new OpenRgbSdkClient(socket);
        try
        {
            client._readLoop = Task.Run(client.ReadLoopAsync, CancellationToken.None);
            client.SendSetClientName(clientName);
            await client.NegotiateAsync(timeout, cancellationToken).ConfigureAwait(false);
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<int> GetControllerCountAsync(CancellationToken cancellationToken = default)
    {
        var reply = await RequestAsync(0, OpenRgbPacketId.RequestControllerCount, [], cancellationToken).ConfigureAwait(false);
        return (int)ReadU32(reply, "controller count");
    }

    public async Task<ControllerData> GetControllerDataAsync(int deviceIndex, CancellationToken cancellationToken = default)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, ProtocolVersion);
        var reply = await RequestAsync((uint)deviceIndex, OpenRgbPacketId.RequestControllerData, payload, cancellationToken).ConfigureAwait(false);
        return ControllerDataReader.Parse(reply, ProtocolVersion);
    }

    /// <summary>
    /// Asks the single OpenRGB instance to re-detect all devices (packet 140). Detection runs in the
    /// background on the server and announces itself with <see cref="DeviceListUpdated"/>.
    /// </summary>
    public void RequestRescan() => Send(0, OpenRgbPacketId.RequestRescanDevices, []);

    /// <summary>
    /// Sets the LED count of a resizable zone (ARGB header). Payload: zone index i32, new size i32.
    /// The server ignores sizes outside the zone's min/max. Re-read the controller afterwards.
    /// </summary>
    public void ResizeZone(int deviceIndex, int zoneIndex, int newSize)
    {
        Span<byte> payload = stackalloc byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(payload, zoneIndex);
        BinaryPrimitives.WriteInt32LittleEndian(payload[4..], newSize);
        Send((uint)deviceIndex, OpenRgbPacketId.ResizeZone, payload);
    }

    /// <summary>
    /// Only selects the Direct/Custom/Static mode index on the server; OpenRGB 1.0rc2 does NOT push the mode to the
    /// hardware for this packet (RGBController::SetCustomMode just sets active_mode). Use <see cref="UpdateMode"/>.
    /// </summary>
    public void SetCustomMode(int deviceIndex) => Send((uint)deviceIndex, OpenRgbPacketId.SetCustomMode, []);

    /// <summary>
    /// Activates <paramref name="modeIndex"/> and pushes it to the hardware (RGBController::UpdateMode). Payload as
    /// RGBController::GetModeDescription: u32 size, i32 mode index, then the mode block (name included, the server
    /// stores it). Needed after a cold boot, when devices run their power-on effect.
    /// </summary>
    public void UpdateMode(int deviceIndex, int modeIndex, ControllerMode mode)
    {
        ArgumentNullException.ThrowIfNull(mode);
        var name = Encoding.UTF8.GetBytes(mode.Name + '\0');
        // 12 u32 mode fields: value, flags, speed min/max, brightness min/max, colors min/max, speed, brightness, direction, color mode.
        var size = 4 + 4 + 2 + name.Length + 4 * 12 + 2 + 4 * mode.Colors.Count;
        var payload = new byte[size];
        var offset = 0;
        void U32(uint value)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(offset), value);
            offset += 4;
        }

        U32((uint)size);
        U32(unchecked((uint)modeIndex));
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(offset), (ushort)name.Length);
        offset += 2;
        name.CopyTo(payload, offset);
        offset += name.Length;
        U32(unchecked((uint)mode.Value));
        U32(mode.Flags);
        U32(mode.SpeedMin);
        U32(mode.SpeedMax);
        U32(mode.BrightnessMin);
        U32(mode.BrightnessMax);
        U32(mode.ColorsMin);
        U32(mode.ColorsMax);
        U32(mode.Speed);
        U32(mode.Brightness);
        U32(mode.Direction);
        U32(mode.ColorMode);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(offset), (ushort)mode.Colors.Count);
        offset += 2;
        foreach (var color in mode.Colors)
        {
            U32(color);
        }

        Send((uint)deviceIndex, OpenRgbPacketId.UpdateMode, payload);
    }

    /// <summary>Writes all LEDs of a device. Must pass exactly the device's LED count. Does not allocate.</summary>
    public void UpdateLeds(int deviceIndex, ReadOnlySpan<RgbColor> colors)
    {
        if (colors.Length > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(colors), colors.Length, "Too many LEDs for one packet.");
        }

        var payloadSize = 4 + 2 + colors.Length * 4;
        lock (_sendLock)
        {
            var buffer = EnsureSendBuffer(OpenRgbProtocol.HeaderSize + payloadSize);
            WriteHeader(buffer, (uint)deviceIndex, OpenRgbPacketId.UpdateLeds, payloadSize);
            var payload = buffer.AsSpan(OpenRgbProtocol.HeaderSize, payloadSize);
            BinaryPrimitives.WriteUInt32LittleEndian(payload, (uint)payloadSize);
            BinaryPrimitives.WriteUInt16LittleEndian(payload[4..], (ushort)colors.Length);
            var offset = 6;
            foreach (var color in colors)
            {
                payload[offset] = color.R;
                payload[offset + 1] = color.G;
                payload[offset + 2] = color.B;
                payload[offset + 3] = 0;
                offset += 4;
            }

            SendLocked(buffer.AsSpan(0, OpenRgbProtocol.HeaderSize + payloadSize));
        }
    }

    public async ValueTask DisposeAsync()
    {
        MarkDisconnected(null);
        await _shutdown.CancelAsync().ConfigureAwait(false);
        _socket.Dispose();
        try
        {
            await _readLoop.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // Expected while tearing down the socket.
        }

        _shutdown.Dispose();
        _requestGate.Dispose();
    }

    private async Task NegotiateAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, OpenRgbProtocol.MaxProtocolVersion);
        var reply = await RequestAsync(0, OpenRgbPacketId.RequestProtocolVersion, payload, cancellationToken, timeout).ConfigureAwait(false);
        ServerProtocolVersion = ReadU32(reply, "protocol version");
        ProtocolVersion = Math.Min(ServerProtocolVersion, OpenRgbProtocol.MaxProtocolVersion);
        if (ProtocolVersion < OpenRgbProtocol.MinProtocolVersion)
        {
            throw new OpenRgbProtocolException(
                $"OpenRGB server speaks SDK protocol {ServerProtocolVersion}; at least {OpenRgbProtocol.MinProtocolVersion} is required.");
        }
    }

    private void SendSetClientName(string clientName)
    {
        var bytes = Encoding.UTF8.GetBytes(clientName + '\0');
        Send(0, OpenRgbPacketId.SetClientName, bytes);
    }

    private async Task<byte[]> RequestAsync(
        uint deviceIndex, OpenRgbPacketId id, byte[] payload, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var pending = new PendingRequest(deviceIndex, id);
            Volatile.Write(ref _pending, pending);
            Send(deviceIndex, id, payload);

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
            timeoutSource.CancelAfter(timeout ?? DefaultRequestTimeout);
            try
            {
                return await pending.Reply.Task.WaitAsync(timeoutSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"OpenRGB did not answer {id} for device {deviceIndex}.");
            }
        }
        finally
        {
            Volatile.Write(ref _pending, null);
            _requestGate.Release();
        }
    }

    private void Send(uint deviceIndex, OpenRgbPacketId id, ReadOnlySpan<byte> payload)
    {
        lock (_sendLock)
        {
            var buffer = EnsureSendBuffer(OpenRgbProtocol.HeaderSize + payload.Length);
            WriteHeader(buffer, deviceIndex, id, payload.Length);
            payload.CopyTo(buffer.AsSpan(OpenRgbProtocol.HeaderSize));
            SendLocked(buffer.AsSpan(0, OpenRgbProtocol.HeaderSize + payload.Length));
        }
    }

    private void SendLocked(ReadOnlySpan<byte> packet)
    {
        if (!IsConnected)
        {
            throw new IOException("OpenRGB SDK connection is closed.");
        }

        try
        {
            while (!packet.IsEmpty)
            {
                var sent = _socket.Send(packet, SocketFlags.None);
                packet = packet[sent..];
            }
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            MarkDisconnected(ex);
            throw new IOException("OpenRGB SDK connection lost while sending.", ex);
        }
    }

    private byte[] EnsureSendBuffer(int size)
    {
        if (_sendBuffer.Length < size)
        {
            _sendBuffer = new byte[Math.Max(size, _sendBuffer.Length * 2)];
        }

        return _sendBuffer;
    }

    private static void WriteHeader(Span<byte> buffer, uint deviceIndex, OpenRgbPacketId id, int payloadSize)
    {
        OpenRgbProtocol.Magic.CopyTo(buffer);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[4..], deviceIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[8..], (uint)id);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[12..], (uint)payloadSize);
    }

    private async Task ReadLoopAsync()
    {
        Exception? cause = null;
        try
        {
            var token = _shutdown.Token;
            while (!token.IsCancellationRequested)
            {
                await ReceiveExactlyAsync(_headerBuffer, token).ConfigureAwait(false);
                if (!_headerBuffer.AsSpan(0, 4).SequenceEqual(OpenRgbProtocol.Magic))
                {
                    throw new OpenRgbProtocolException("Received a packet without the ORGB magic.");
                }

                var deviceIndex = BinaryPrimitives.ReadUInt32LittleEndian(_headerBuffer.AsSpan(4));
                var id = (OpenRgbPacketId)BinaryPrimitives.ReadUInt32LittleEndian(_headerBuffer.AsSpan(8));
                var size = BinaryPrimitives.ReadUInt32LittleEndian(_headerBuffer.AsSpan(12));
                if (size > OpenRgbProtocol.MaxPayloadSize)
                {
                    throw new OpenRgbProtocolException($"Packet {id} announces {size} bytes, above the {OpenRgbProtocol.MaxPayloadSize} limit.");
                }

                var payload = size == 0 ? Array.Empty<byte>() : new byte[size];
                await ReceiveExactlyAsync(payload, token).ConfigureAwait(false);

                if (id == OpenRgbPacketId.DeviceListUpdated)
                {
                    DeviceListUpdated?.Invoke();
                    continue;
                }

                var pending = Volatile.Read(ref _pending);
                if (pending is not null && pending.Id == id && pending.DeviceIndex == deviceIndex)
                {
                    pending.Reply.TrySetResult(payload);
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // Disposed by the owner: a clean shutdown, not an error.
        }
        catch (Exception ex)
        {
            cause = ex;
        }
        finally
        {
            MarkDisconnected(cause ?? new IOException("OpenRGB SDK connection closed."));
        }
    }

    private async Task ReceiveExactlyAsync(Memory<byte> buffer, CancellationToken token)
    {
        while (!buffer.IsEmpty)
        {
            var read = await _socket.ReceiveAsync(buffer, SocketFlags.None, token).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("OpenRGB closed the SDK connection.");
            }

            buffer = buffer[read..];
        }
    }

    private void MarkDisconnected(Exception? cause)
    {
        if (Interlocked.Exchange(ref _disconnected, 1) != 0)
        {
            return;
        }

        Volatile.Read(ref _pending)?.Reply.TrySetException(cause ?? new ObjectDisposedException(nameof(OpenRgbSdkClient)));
        Disconnected?.Invoke(cause);
    }

    private static uint ReadU32(byte[] reply, string what) =>
        reply.Length >= 4
            ? BinaryPrimitives.ReadUInt32LittleEndian(reply)
            : throw new OpenRgbProtocolException($"Reply for {what} is {reply.Length} bytes, expected 4.");

    private sealed class PendingRequest(uint deviceIndex, OpenRgbPacketId id)
    {
        public uint DeviceIndex { get; } = deviceIndex;

        public OpenRgbPacketId Id { get; } = id;

        public TaskCompletionSource<byte[]> Reply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
