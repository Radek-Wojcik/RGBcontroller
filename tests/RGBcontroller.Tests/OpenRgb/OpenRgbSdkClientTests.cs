using System.Buffers.Binary;
using RGBcontroller.Core;
using RGBcontroller.Core.OpenRgb;

namespace RGBcontroller.Tests.OpenRgb;

public sealed class OpenRgbSdkClientTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task Connect_NegotiatesProtocol5AndSendsClientName()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = new FakeOpenRgbServer(5, ControllerDataWriter.Keyboard());

        await using var client = await OpenRgbSdkClient.ConnectAsync("127.0.0.1", server.Port, "RGBcontroller", Timeout, ct);

        Assert.Equal(5u, client.ProtocolVersion);
        Assert.Equal(5u, server.ClientProtocolVersion);
        Assert.Equal("RGBcontroller", server.ClientName);
    }

    [Fact]
    public async Task Connect_NewerServer_UsesOurMaximum()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = new FakeOpenRgbServer(6, ControllerDataWriter.Keyboard(5));

        await using var client = await OpenRgbSdkClient.ConnectAsync("127.0.0.1", server.Port, "t", Timeout, ct);

        Assert.Equal(6u, client.ServerProtocolVersion);
        Assert.Equal(5u, client.ProtocolVersion);
        Assert.Equal("Razer Ornata Chroma V2", (await client.GetControllerDataAsync(0, ct)).Name);
    }

    [Fact]
    public async Task Connect_TooOldServer_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = new FakeOpenRgbServer(3);

        await Assert.ThrowsAsync<OpenRgbProtocolException>(() =>
            OpenRgbSdkClient.ConnectAsync("127.0.0.1", server.Port, "t", Timeout, ct));
    }

    [Theory]
    [InlineData(4u)]
    [InlineData(5u)]
    public async Task GetControllerData_ParsesFullDescription(uint protocol)
    {
        var ct = TestContext.Current.CancellationToken;
        var expected = ControllerDataWriter.Keyboard(protocol);
        await using var server = new FakeOpenRgbServer(protocol, expected);
        await using var client = await OpenRgbSdkClient.ConnectAsync("127.0.0.1", server.Port, "t", Timeout, ct);

        Assert.Equal(1, await client.GetControllerCountAsync(ct));
        var data = await client.GetControllerDataAsync(0, ct);

        Assert.Equal(expected.Name, data.Name);
        Assert.Equal(expected.Location, data.Location);
        Assert.Equal(["Direct", "Static"], data.Modes.Select(m => m.Name));
        var zone = Assert.Single(data.Zones);
        Assert.Equal(126u, zone.LedsCount);
        Assert.Equal((6u, 21u), (zone.MatrixHeight, zone.MatrixWidth));
        Assert.Equal(126, zone.Matrix.Count);
        Assert.Equal("Main", Assert.Single(zone.Segments).Name);
        Assert.Equal(126, data.Leds.Count);
        Assert.Equal(126, data.ColorCount);
        Assert.Equal(expected.Zones[0].Flags, zone.Flags);
        Assert.Equal(expected.LedAlternateNames, data.LedAlternateNames);
        Assert.Equal(expected.Flags, data.Flags);
    }

    [Fact]
    public void Parse_RejectsTruncatedAndPaddedData()
    {
        var bytes = ControllerDataWriter.Write(ControllerDataWriter.Keyboard(), 5);

        var truncated = bytes[..^4];
        BinaryPrimitives.WriteUInt32LittleEndian(truncated, (uint)truncated.Length);
        Assert.Throws<OpenRgbProtocolException>(() => ControllerDataReader.Parse(truncated, 5));

        byte[] padded = [.. bytes, 0, 0];
        BinaryPrimitives.WriteUInt32LittleEndian(padded, (uint)padded.Length);
        Assert.Throws<OpenRgbProtocolException>(() => ControllerDataReader.Parse(padded, 5));

        // Protocol 5 data read as protocol 4 must not silently succeed.
        Assert.Throws<OpenRgbProtocolException>(() => ControllerDataReader.Parse(bytes, 4));
    }

    [Fact]
    public async Task UpdateLeds_SendsSizeCheckedPacketInRgbOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = new FakeOpenRgbServer(5, ControllerDataWriter.Keyboard());
        await using var client = await OpenRgbSdkClient.ConnectAsync("127.0.0.1", server.Port, "t", Timeout, ct);

        client.SetCustomMode(0);
        client.UpdateLeds(0, [new RgbColor(1, 2, 3), new RgbColor(250, 251, 252)]);
        // A round trip after the fire-and-forget writes guarantees the server has processed them.
        await client.GetControllerCountAsync(ct);

        Assert.Equal([0u], server.CustomModeRequests);
        var (device, payload) = Assert.Single(server.LedUpdates);
        Assert.Equal(0u, device);
        Assert.Equal(4 + 2 + 2 * 4, payload.Length);
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(4)));
        Assert.Equal(new byte[] { 1, 2, 3, 0, 250, 251, 252, 0 }, payload[6..]);
        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task UpdateLeds_DoesNotAllocateAfterWarmUp()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = new FakeOpenRgbServer(5, ControllerDataWriter.Keyboard());
        await using var client = await OpenRgbSdkClient.ConnectAsync("127.0.0.1", server.Port, "t", Timeout, ct);
        var colors = new RgbColor[126];
        client.UpdateLeds(0, colors);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
        {
            client.UpdateLeds(0, colors);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public async Task RequestRescan_RaisesDeviceListUpdatedOnEachServerNotice()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = new FakeOpenRgbServer(5, ControllerDataWriter.Keyboard()) { DeviceListUpdatesPerRescan = 3 };
        await using var client = await OpenRgbSdkClient.ConnectAsync("127.0.0.1", server.Port, "t", Timeout, ct);
        var updates = 0;
        var allSeen = new TaskCompletionSource();
        client.DeviceListUpdated += () =>
        {
            if (Interlocked.Increment(ref updates) == 3)
            {
                allSeen.TrySetResult();
            }
        };

        client.RequestRescan();

        await allSeen.Task.WaitAsync(Timeout, ct);
        Assert.Equal(1, server.RescanRequests);
        Assert.Equal(1, await client.GetControllerCountAsync(ct));
    }

    [Fact]
    public async Task UnresponsiveServer_RequestTimesOutInsteadOfHanging()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = new FakeOpenRgbServer(5, ControllerDataWriter.Keyboard());
        await using var client = await OpenRgbSdkClient.ConnectAsync("127.0.0.1", server.Port, "t", Timeout, ct);
        server.Unresponsive = true;

        await Assert.ThrowsAsync<TimeoutException>(() => client.GetControllerCountAsync(ct));
    }

    [Fact]
    public async Task ServerDrop_RaisesDisconnectedAndFailsLaterCalls()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = new FakeOpenRgbServer(5, ControllerDataWriter.Keyboard());
        await using var client = await OpenRgbSdkClient.ConnectAsync("127.0.0.1", server.Port, "t", Timeout, ct);
        var disconnected = new TaskCompletionSource<Exception?>();
        client.Disconnected += cause => disconnected.TrySetResult(cause);

        server.DropAllClients();

        Assert.NotNull(await disconnected.Task.WaitAsync(Timeout, ct));
        Assert.False(client.IsConnected);
        Assert.Throws<IOException>(() => client.UpdateLeds(0, [default]));
        await Assert.ThrowsAnyAsync<Exception>(() => client.GetControllerCountAsync(ct));
    }

    [Fact]
    public async Task Connect_NothingListening_FailsWithinTimeout()
    {
        var ct = TestContext.Current.CancellationToken;
        int port;
        await using (var server = new FakeOpenRgbServer())
        {
            port = server.Port;
        }

        await Assert.ThrowsAnyAsync<Exception>(() =>
            OpenRgbSdkClient.ConnectAsync("127.0.0.1", port, "t", Timeout, ct));
    }
}
