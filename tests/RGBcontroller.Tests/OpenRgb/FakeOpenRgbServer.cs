using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using RGBcontroller.Core.OpenRgb;

namespace RGBcontroller.Tests.OpenRgb;

/// <summary>
/// In-process stand-in for the OpenRGB 1.0rc2 SDK server (loopback TCP). Implements the subset the
/// client uses, including the server-side size check that drops clients on a malformed UPDATELEDS.
/// </summary>
internal sealed class FakeOpenRgbServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<Socket, SemaphoreSlim> _clients = new();
    private readonly Lock _controllersLock = new();
    private readonly Task _acceptLoop;
    private List<ControllerData> _controllers;

    public FakeOpenRgbServer(uint serverProtocolVersion = 5, params ControllerData[] controllers)
    {
        ServerProtocolVersion = serverProtocolVersion;
        _controllers = [.. controllers];
        _listener.Start();
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public uint ServerProtocolVersion { get; }

    public IReadOnlyList<ControllerData> Controllers
    {
        get { lock (_controllersLock) { return [.. _controllers]; } }
        set { lock (_controllersLock) { _controllers = [.. value]; } }
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public string? ClientName { get; private set; }

    public uint? ClientProtocolVersion { get; private set; }

    public int ConnectedClients => _clients.Count;

    public int RescanRequests;

    /// <summary>How many DEVICE_LIST_UPDATED packets a rescan produces (OpenRGB sends one per registered controller plus one after cleanup).</summary>
    public int DeviceListUpdatesPerRescan { get; set; } = 3;

    /// <summary>When true the server never answers requests (a hung OpenRGB).</summary>
    public bool Unresponsive { get; set; }

    public ConcurrentQueue<(uint Device, byte[] Payload)> LedUpdates { get; } = new();

    public ConcurrentQueue<uint> CustomModeRequests { get; } = new();

    public ConcurrentQueue<(uint Device, int Zone, int Size)> ResizeRequests { get; } = new();

    /// <summary>Colors (R, G, B) of the last UPDATELEDS per device.</summary>
    public ConcurrentDictionary<uint, (byte R, byte G, byte B)[]> LastColors { get; } = new();

    public void DropAllClients()
    {
        foreach (var client in _clients.Keys)
        {
            client.Dispose();
        }
    }

    /// <summary>Pushes DEVICE_LIST_UPDATED to every client, as OpenRGB does on hot-plug or rescan.</summary>
    public async Task NotifyDeviceListUpdatedAsync()
    {
        foreach (var (socket, sendLock) in _clients)
        {
            await SendAsync(socket, sendLock, 0, OpenRgbPacketId.DeviceListUpdated, []);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        DropAllClients();
        try
        {
            await _acceptLoop;
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }

        _stop.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            var socket = await _listener.AcceptSocketAsync(_stop.Token);
            var sendLock = new SemaphoreSlim(1, 1);
            _clients[socket] = sendLock;
            _ = Task.Run(() => ServeAsync(socket, sendLock));
        }
    }

    private async Task ServeAsync(Socket socket, SemaphoreSlim sendLock)
    {
        var header = new byte[OpenRgbProtocol.HeaderSize];
        try
        {
            while (true)
            {
                await ReadExactlyAsync(socket, header);
                Assert.Equal("ORGB"u8.ToArray(), header[..4]);
                var device = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
                var id = (OpenRgbPacketId)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
                var size = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
                var payload = new byte[size];
                await ReadExactlyAsync(socket, payload);

                if (Unresponsive)
                {
                    continue;
                }

                switch (id)
                {
                    case OpenRgbPacketId.SetClientName:
                        ClientName = Encoding.UTF8.GetString(payload).TrimEnd('\0');
                        break;
                    case OpenRgbPacketId.RequestProtocolVersion:
                        ClientProtocolVersion = BinaryPrimitives.ReadUInt32LittleEndian(payload);
                        await SendAsync(socket, sendLock, 0, id, U32(ServerProtocolVersion));
                        break;
                    case OpenRgbPacketId.RequestControllerCount:
                        await SendAsync(socket, sendLock, 0, id, U32((uint)Controllers.Count));
                        break;
                    case OpenRgbPacketId.RequestControllerData:
                        var version = payload.Length == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(payload) : 0;
                        await SendAsync(socket, sendLock, device, id, ControllerDataWriter.Write(Controllers[(int)device], version));
                        break;
                    case OpenRgbPacketId.RequestRescanDevices:
                        Interlocked.Increment(ref RescanRequests);
                        for (var i = 0; i < DeviceListUpdatesPerRescan; i++)
                        {
                            await SendAsync(socket, sendLock, 0, OpenRgbPacketId.DeviceListUpdated, []);
                        }

                        break;
                    case OpenRgbPacketId.ResizeZone:
                        Resize(device, BinaryPrimitives.ReadInt32LittleEndian(payload), BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(4)));
                        break;
                    case OpenRgbPacketId.UpdateLeds:
                        if (BinaryPrimitives.ReadUInt32LittleEndian(payload) != size)
                        {
                            // OpenRGB 1.0rc2 closes the connection on a size mismatch (NetworkServer.cpp, listen_done).
                            socket.Dispose();
                            return;
                        }

                        LedUpdates.Enqueue((device, payload));
                        var count = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(4));
                        var colors = new (byte, byte, byte)[count];
                        for (var i = 0; i < count; i++)
                        {
                            colors[i] = (payload[6 + i * 4], payload[7 + i * 4], payload[8 + i * 4]);
                        }

                        LastColors[device] = colors;
                        break;
                    case OpenRgbPacketId.SetCustomMode:
                        CustomModeRequests.Enqueue(device);
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            // Client went away or the test dropped it.
        }
        finally
        {
            _clients.TryRemove(socket, out _);
        }
    }

    private void Resize(uint device, int zoneIndex, int size)
    {
        ResizeRequests.Enqueue((device, zoneIndex, size));
        lock (_controllersLock)
        {
            var data = _controllers[(int)device];
            var zone = data.Zones[zoneIndex];
            if (size < zone.LedsMin || size > zone.LedsMax)
            {
                return;
            }

            var zones = data.Zones.ToArray();
            zones[zoneIndex] = zone with { LedsCount = (uint)size };
            var ledCount = zones.Sum(z => (int)z.LedsCount);
            var leds = Enumerable.Range(0, ledCount).Select(i => new ControllerLed($"LED {i}", (uint)i)).ToArray();
            _controllers[(int)device] = data with { Zones = zones, Leds = leds, ColorCount = ledCount };
        }
    }

    private static async Task SendAsync(Socket socket, SemaphoreSlim sendLock, uint device, OpenRgbPacketId id, byte[] payload)
    {
        var packet = new byte[OpenRgbProtocol.HeaderSize + payload.Length];
        "ORGB"u8.CopyTo(packet);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), device);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), (uint)id);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12), (uint)payload.Length);
        payload.CopyTo(packet, OpenRgbProtocol.HeaderSize);
        await sendLock.WaitAsync();
        try
        {
            await socket.SendAsync(packet);
        }
        finally
        {
            sendLock.Release();
        }
    }

    private static async Task ReadExactlyAsync(Socket socket, byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await socket.ReceiveAsync(buffer.AsMemory(offset));
            if (read == 0)
            {
                throw new IOException("Client closed the connection.");
            }

            offset += read;
        }
    }

    private static byte[] U32(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes;
    }
}
