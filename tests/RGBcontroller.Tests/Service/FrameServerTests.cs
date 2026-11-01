using System.Collections.Concurrent;
using System.IO.Pipes;
using RGBcontroller.Core;
using RGBcontroller.Ipc;
using RGBcontroller.Service.Ipc;
using Serilog.Core;

namespace RGBcontroller.Tests.Service;

public sealed class FrameServerTests
{
    [Fact]
    public async Task StripColorsReachTheSinkAndADisconnectMeansFallback()
    {
        var ct = TestContext.Current.CancellationToken;
        var sink = new RecordingSink();
        var pipeName = "RGBcontroller.Test." + Guid.NewGuid().ToString("N");
        await using var server = new FrameServer(pipeName, ledCount: 2, sink, Logger.None, _ => true);
        server.Start();

        await using (var client = await ConnectAsync(pipeName, ct))
        {
            await client.WriteAsync(Frame(FrameType.StripColors, [1, 2, 3, 4, 5, 6]), ct);
            Assert.Equal("colors 1,2,3 4,5,6", await sink.Events.NextAsync(ct));

            await client.WriteAsync(Frame(FrameType.CaptureUnavailable, []), ct);
            Assert.Equal("unavailable", await sink.Events.NextAsync(ct));
        }

        Assert.Equal("unavailable", await sink.Events.NextAsync(ct));
    }

    [Fact]
    public async Task AWrongSizedFrameClosesTheConnection()
    {
        var ct = TestContext.Current.CancellationToken;
        var sink = new RecordingSink();
        var pipeName = "RGBcontroller.Test." + Guid.NewGuid().ToString("N");
        await using var server = new FrameServer(pipeName, ledCount: 2, sink, Logger.None, _ => true);
        server.Start();

        var client = await ConnectAsync(pipeName, ct);
        try
        {
            await client.WriteAsync(Frame(FrameType.StripColors, [1, 2, 3]), ct);
        }
        catch (IOException)
        {
            // The server may close the pipe before the write completes.
        }

        Assert.Equal("unavailable", await sink.Events.NextAsync(ct));
        await Assert.ThrowsAnyAsync<IOException>(() => client.WriteAsync(new byte[1], ct).AsTask());
        Assert.Empty(sink.Events.Items);
        try
        {
            client.Dispose();
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task FramesFromOutsideTheConsoleSessionAreIgnored()
    {
        var ct = TestContext.Current.CancellationToken;
        var sink = new RecordingSink();
        var pipeName = "RGBcontroller.Test." + Guid.NewGuid().ToString("N");
        await using var server = new FrameServer(pipeName, ledCount: 1, sink, Logger.None, _ => false);
        server.Start();

        await using (var client = await ConnectAsync(pipeName, ct))
        {
            await client.WriteAsync(Frame(FrameType.StripColors, [9, 9, 9]), ct);
            await client.WriteAsync(Frame(FrameType.CaptureUnavailable, []), ct);
            await client.FlushAsync(ct);
        }

        await Task.Delay(200, ct);
        Assert.Empty(sink.Events.Items);
    }

    private static async Task<NamedPipeClientStream> ConnectAsync(string pipeName, CancellationToken ct)
    {
        var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
        await client.ConnectAsync(5000, ct);
        return client;
    }

    private static byte[] Frame(FrameType type, byte[] payload)
    {
        var frame = new byte[FrameCodec.HeaderSize + payload.Length];
        FrameCodec.WriteHeader(frame, new FrameHeader(type, 1, payload.Length));
        payload.CopyTo(frame, FrameCodec.HeaderSize);
        return frame;
    }

    private sealed class RecordingSink : IScreenFeedSink
    {
        public EventQueue Events { get; } = new();

        public void OnScreenColors(ReadOnlySpan<RgbColor> colors) =>
            Events.Add("colors " + string.Join(" ", colors.ToArray().Select(c => $"{c.R},{c.G},{c.B}")));

        public void OnScreenUnavailable() => Events.Add("unavailable");
    }

    internal sealed class EventQueue
    {
        private readonly BlockingCollection<string> _items = [];

        public IReadOnlyCollection<string> Items => _items.ToArray();

        public void Add(string item) => _items.Add(item);

        public Task<string> NextAsync(CancellationToken ct) =>
            Task.Run(() => _items.TryTake(out var item, 5000, ct) ? item : "timeout", ct);
    }
}
