using RGBcontroller.Core.Config;
using RGBcontroller.Ipc;
using RGBcontroller.Service;
using RGBcontroller.Service.OpenRgb;
using RGBcontroller.Tests.OpenRgb;
using Serilog.Core;

namespace RGBcontroller.Tests.Service;

public sealed class OpenRgbSupervisorTests
{
    private static readonly SupervisorTimings FastTimings = new(
        HealthCheckInterval: TimeSpan.FromMilliseconds(200),
        StableUptime: TimeSpan.FromSeconds(60),
        RestartBackoff: _ => TimeSpan.FromMilliseconds(20))
    {
        ConnectRetryDelay = TimeSpan.FromMilliseconds(20),
        BlackoutGrace = TimeSpan.FromMilliseconds(1),
    };

    [Fact]
    public async Task LightsEveryDeviceInDirectModeWithTheStaticColor()
    {
        await using var server = new FakeOpenRgbServer(5, ControllerDataWriter.Keyboard(), ControllerDataWriter.Motherboard());
        await using var run = Start(server, out var supervisor, out var launcher);

        await WaitUntilAsync(() => supervisor.State == ComponentState.Ok);

        Assert.Equal(1, launcher.StrayKills);
        Assert.Equal(1, launcher.Starts);
        Assert.Equal("RGBcontroller", server.ClientName);
        Assert.Contains(0u, server.CustomModeRequests);
        Assert.Contains(1u, server.CustomModeRequests);
        Assert.All(server.LastColors[0], c => Assert.Equal(((byte)0, (byte)229, (byte)255), c));
        Assert.Equal(126, server.LastColors[0].Length);
    }

    [Fact]
    public async Task ResizesConfiguredArgbZoneBeforeLighting()
    {
        await using var server = new FakeOpenRgbServer(5, ControllerDataWriter.Motherboard());
        await using var run = Start(server, out var supervisor, out _);

        await WaitUntilAsync(() => supervisor.State == ComponentState.Ok);

        Assert.Equal((0u, 1, 20), Assert.Single(server.ResizeRequests));
        Assert.Equal(21, server.LastColors[0].Length);
    }

    [Fact]
    public async Task DeviceListUpdate_ReappliesAfterSettling()
    {
        await using var server = new FakeOpenRgbServer(5, ControllerDataWriter.Keyboard());
        await using var run = Start(server, out var supervisor, out _);
        var applied = 0;
        supervisor.DevicesApplied += _ => Interlocked.Increment(ref applied);
        await WaitUntilAsync(() => supervisor.State == ComponentState.Ok);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        var appliedBefore = Volatile.Read(ref applied);
        var updatesBefore = server.LedUpdates.Count;

        // A rescan or hot-plug: several notices in a burst must lead to one re-apply.
        server.Controllers = [ControllerDataWriter.Keyboard(), ControllerDataWriter.Motherboard()];
        await server.NotifyDeviceListUpdatedAsync();
        await server.NotifyDeviceListUpdatedAsync();
        await server.NotifyDeviceListUpdatedAsync();

        await WaitUntilAsync(() => server.LastColors.ContainsKey(1));
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.Equal(appliedBefore + 1, Volatile.Read(ref applied));
        Assert.Equal(updatesBefore + 2, server.LedUpdates.Count);
    }

    [Fact]
    public async Task OpenRgbExit_RestartsOnceAndRelights()
    {
        await using var server = new FakeOpenRgbServer(5, ControllerDataWriter.Keyboard());
        await using var run = Start(server, out var supervisor, out var launcher);
        await WaitUntilAsync(() => supervisor.State == ComponentState.Ok);

        launcher.Processes[0].Crash();
        server.DropAllClients();

        await WaitUntilAsync(() => launcher.Starts == 2 && supervisor.State == ComponentState.Ok);
        Assert.True(launcher.Processes[0].Killed);
        Assert.Equal(1, launcher.StrayKills);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.Equal(2, launcher.Starts);
    }

    [Fact]
    public async Task HungSdk_IsRestarted()
    {
        await using var server = new FakeOpenRgbServer(5, ControllerDataWriter.Keyboard());
        var timings = FastTimings with { HealthCheckInterval = TimeSpan.FromMilliseconds(50) };
        await using var run = Start(server, out var supervisor, out var launcher, timings);
        await WaitUntilAsync(() => supervisor.State == ComponentState.Ok);

        server.Unresponsive = true;

        // The client's 5 s request timeout fires, the session ends and OpenRGB is restarted.
        await WaitUntilAsync(() => launcher.Starts >= 2, TimeSpan.FromSeconds(10));
        Assert.True(launcher.Processes[0].Killed);
    }

    [Fact]
    public async Task Stop_SendsBlackThenKillsOpenRgb()
    {
        await using var server = new FakeOpenRgbServer(5, ControllerDataWriter.Keyboard());
        var run = Start(server, out var supervisor, out var launcher);
        await WaitUntilAsync(() => supervisor.State == ComponentState.Ok);

        await run.DisposeAsync();

        Assert.All(server.LastColors[0], c => Assert.Equal(((byte)0, (byte)0, (byte)0), c));
        Assert.True(launcher.Processes[0].Killed);
        Assert.Equal(ComponentState.Offline, supervisor.State);
    }

    [Fact]
    public async Task OpenRgbNeverAccepting_IsRestartedAfterStartupTimeout()
    {
        var launcher = new FakeLauncher();
        var config = new OpenRgbConfig { Port = 1, StartupTimeoutSeconds = 5 };
        var supervisor = new OpenRgbSupervisor(launcher, config, new LightingConfig(), new StartupClock(), Logger.None,
            FastTimings with { ConnectAttemptTimeout = TimeSpan.FromMilliseconds(100) });
        using var stop = new CancellationTokenSource();
        var task = supervisor.RunAsync(stop.Token);

        await WaitUntilAsync(() => launcher.Starts >= 2, TimeSpan.FromSeconds(15));

        await stop.CancelAsync();
        await task;
        Assert.True(launcher.Processes[0].Killed);
    }

    private static RunningSupervisor Start(
        FakeOpenRgbServer server, out OpenRgbSupervisor supervisor, out FakeLauncher launcher, SupervisorTimings? timings = null)
    {
        launcher = new FakeLauncher();
        var config = new OpenRgbConfig { Port = server.Port, DeviceSettleMilliseconds = 100 };
        supervisor = new OpenRgbSupervisor(launcher, config, new LightingConfig(), new StartupClock(), Logger.None, timings ?? FastTimings);
        return new RunningSupervisor(supervisor);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Condition not reached in time.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    private sealed class RunningSupervisor : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _task;

        public RunningSupervisor(OpenRgbSupervisor supervisor)
        {
            _task = supervisor.RunAsync(_stop.Token);
        }

        public async ValueTask DisposeAsync()
        {
            if (!_stop.IsCancellationRequested)
            {
                await _stop.CancelAsync();
                await _task;
            }

            _stop.Dispose();
        }
    }

    private sealed class FakeLauncher : IOpenRgbLauncher
    {
        private int _starts;

        public int StrayKills;

        public List<FakeProcess> Processes { get; } = [];

        public int Starts => Volatile.Read(ref _starts);

        public IOpenRgbProcess Start()
        {
            var process = new FakeProcess(1000 + Interlocked.Increment(ref _starts));
            lock (Processes)
            {
                Processes.Add(process);
            }

            return process;
        }

        public int KillStrays()
        {
            Interlocked.Increment(ref StrayKills);
            return 0;
        }
    }

    private sealed class FakeProcess(int id) : IOpenRgbProcess
    {
        private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Id => id;

        public Task Exited => _exit.Task;

        public bool Killed { get; private set; }

        public void Crash() => _exit.TrySetResult();

        public void Kill()
        {
            Killed = true;
            _exit.TrySetResult();
        }

        public void Dispose()
        {
        }
    }
}
