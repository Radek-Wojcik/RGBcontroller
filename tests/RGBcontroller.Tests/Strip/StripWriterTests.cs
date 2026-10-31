using System.Collections.Concurrent;
using RGBcontroller.Core;
using RGBcontroller.Core.Config;
using RGBcontroller.Core.Strip;
using Serilog.Core;

namespace RGBcontroller.Tests.Strip;

public sealed class StripWriterTests
{
    private static readonly StripConfig Config = new() { LedCount = 4, MaxFps = 30 };

    private static readonly StripWriterTimings FastTimings = new(
        Settle: TimeSpan.FromMilliseconds(5),
        KeepAlive: TimeSpan.FromMilliseconds(100),
        Backoff: _ => TimeSpan.FromMilliseconds(10))
    {
        StartupFrameGap = TimeSpan.FromMilliseconds(1),
    };

    [Fact]
    public async Task Connects_SendsStartupBurstThenChanges()
    {
        var ports = new FakePorts { Present = "COM3" };
        await using var writer = new StripWriter(Config, ports, Logger.None, FastTimings);
        writer.SetAll(new RgbColor(255, 0, 0), 100);

        writer.Start();
        await WaitUntilAsync(() => writer.State == StripState.Connected);
        Assert.True(ports.Transports[0].Frames.Count >= 3);
        Assert.Equal(new byte[] { 255, 0, 0 }, ports.Transports[0].Frames.Last()[6..9]);

        writer.SetAll(new RgbColor(0, 0, 255), 100);
        await WaitUntilAsync(() => ports.Transports[0].Frames.Last()[8] == 255);
        Assert.Equal("COM3", writer.PortName);
    }

    [Fact]
    public async Task WaitsForDeviceThenConnects()
    {
        var ports = new FakePorts { Present = null };
        await using var writer = new StripWriter(Config, ports, Logger.None, FastTimings);
        writer.Start();
        await WaitUntilAsync(() => ports.FindCalls >= 3);
        Assert.Equal(StripState.Searching, writer.State);

        ports.Present = "COM7";

        await WaitUntilAsync(() => writer.State == StripState.Connected);
        Assert.Equal("COM7", writer.PortName);
    }

    [Fact]
    public async Task PortLoss_ReconnectsAndKeepsColors()
    {
        var ports = new FakePorts { Present = "COM3" };
        await using var writer = new StripWriter(Config, ports, Logger.None, FastTimings);
        writer.SetAll(new RgbColor(1, 2, 3), 100);
        writer.Start();
        await WaitUntilAsync(() => writer.State == StripState.Connected);

        // CH340 re-enumerates on resume: writes fail, then the port comes back.
        ports.Transports[0].FailWrites = true;
        writer.SetAll(new RgbColor(4, 5, 6), 100);

        await WaitUntilAsync(() => ports.Transports.Count == 2 && writer.State == StripState.Connected);
        Assert.True(ports.Transports[0].Disposed);
        Assert.Equal(new byte[] { 4, 5, 6 }, ports.Transports[1].Frames.Last()[6..9]);
    }

    [Fact]
    public async Task KeepAlive_ResendsStaticFrame()
    {
        var ports = new FakePorts { Present = "COM3" };
        await using var writer = new StripWriter(Config, ports, Logger.None, FastTimings);
        writer.Start();
        await WaitUntilAsync(() => writer.State == StripState.Connected);
        var after = ports.Transports[0].Frames.Count;

        await WaitUntilAsync(() => ports.Transports[0].Frames.Count >= after + 2);
    }

    [Fact]
    public async Task Stop_SendsBlackFlushesAndCloses()
    {
        var ports = new FakePorts { Present = "COM3" };
        var writer = new StripWriter(Config, ports, Logger.None, FastTimings);
        writer.SetAll(new RgbColor(255, 255, 255), 100);
        writer.Start();
        await WaitUntilAsync(() => writer.State == StripState.Connected);

        await writer.DisposeAsync();

        var transport = ports.Transports[0];
        Assert.All(transport.Frames.TakeLast(3), f => Assert.All(f[6..], b => Assert.Equal(0, b)));
        Assert.True(transport.Flushed);
        Assert.True(transport.Disposed);
        Assert.Equal(StripState.Stopped, writer.State);
    }

    [Fact]
    public void EffectiveFps_IsClampedToTheSerialLink()
    {
        var writer = new StripWriter(new StripConfig { LedCount = 113, BaudRate = 115200, MaxFps = 60 }, new FakePorts(), Logger.None);

        // 33.4 FPS link limit x 0.95 = 31.
        Assert.Equal(31, writer.EffectiveMaxFps);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Condition not reached in time.");
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }

    private sealed class FakePorts : IStripPortProvider
    {
        public volatile string? Present;
        public int FindCalls;

        public List<FakeTransport> Transports { get; } = [];

        public string? FindPort()
        {
            Interlocked.Increment(ref FindCalls);
            return Present;
        }

        public IStripTransport Open(string portName)
        {
            var transport = new FakeTransport(portName);
            lock (Transports)
            {
                Transports.Add(transport);
            }

            return transport;
        }
    }

    private sealed class FakeTransport(string portName) : IStripTransport
    {
        private readonly ConcurrentQueue<byte[]> _frames = new();

        public string PortName => portName;

        public volatile bool FailWrites;

        public IReadOnlyCollection<byte[]> Frames => _frames;

        public bool Flushed { get; private set; }

        public bool Disposed { get; private set; }

        public void Write(ReadOnlySpan<byte> frame)
        {
            if (FailWrites)
            {
                throw new IOException("The device is not connected.");
            }

            _frames.Enqueue(frame.ToArray());
        }

        public void Flush() => Flushed = true;

        public void Dispose() => Disposed = true;
    }
}
