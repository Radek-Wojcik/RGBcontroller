using RGBcontroller.Core.Config;
using RGBcontroller.Ipc;
using RGBcontroller.Service;
using RGBcontroller.Service.Lighting;
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
        Uptime = () => TimeSpan.FromHours(1),
    };

    /// <summary>One static cyan group that claims every zone, like the stage 1 static color.</summary>
    private static readonly LightingConfig StaticCyan = new()
    {
        ActiveProfile = "Test",
        LogonProfile = "Test",
        DefaultGroup = "All",
        Groups = [new GroupConfig { Name = "All" }],
        Profiles = [new ProfileConfig { Name = "Test", Groups = [new GroupEffectConfig { Group = "All", Effect = EffectKind.Static, Palette = "#00E5FF" }] }],
    };

    [Fact]
    public async Task LightsEveryDeviceInDirectModeWithTheStaticColor()
    {
        await using var server = new FakeOpenRgbServer(5,
            ControllerDataWriter.Keyboard(), ControllerDataWriter.Motherboard(), ControllerDataWriter.Mouse());
        await using var run = Start(server, out var supervisor, out var launcher);

        await WaitUntilAsync(() => supervisor.State == ComponentState.Ok && server.LastColors.Count == 3);

        Assert.Equal(1, launcher.StrayKills);
        Assert.Equal(1, launcher.Starts);
        Assert.Equal("RGBcontroller", server.ClientName);
        Assert.All(server.LastColors[0], c => Assert.Equal(((byte)0, (byte)229, (byte)255), c));
        Assert.Equal(126, server.LastColors[0].Length);
    }

    [Fact]
    public async Task PushesDirectModeToHardwareWithUpdateMode()
    {
        // SETCUSTOMMODE only selects the mode inside OpenRGB; after a cold boot the RAM kept its rainbow and the GPU
        // stayed dark until UPDATEMODE made OpenRGB write the mode to the devices.
        await using var server = new FakeOpenRgbServer(5, ControllerDataWriter.Keyboard(), ControllerDataWriter.Mouse());
        await using var run = Start(server, out var supervisor, out _);

        await WaitUntilAsync(() => server.UpdateModeRequests.Count == 2);

        var requests = server.UpdateModeRequests.OrderBy(r => r.Device).ToArray();
        Assert.Equal((0u, 0, "Direct", 1u), requests[0]);
        Assert.Equal((1u, 1, "Direct", 1u), requests[1]);
        Assert.Empty(server.CustomModeRequests);
    }

    [Fact]
    public async Task WaitsForMinimumUptimeBeforeStartingOpenRgb()
    {
        await using var server = new FakeOpenRgbServer(5, ControllerDataWriter.Keyboard());
        var bootedAt = DateTime.UtcNow;
        var timings = FastTimings with { Uptime = () => DateTime.UtcNow - bootedAt };
        var launcher = new FakeLauncher();
        var config = new OpenRgbConfig { Port = server.Port, MinimumUptimeSeconds = 1 };
        var supervisor = new OpenRgbSupervisor(launcher, config, NullSink.Instance, new StartupClock(), Logger.None, timings);
        await using var run = new RunningSupervisor(supervisor);

        await Task.Delay(500, TestContext.Current.CancellationToken);
        Assert.Equal(0, launcher.Starts);
        Assert.Equal(1, launcher.StrayKills);

        await WaitUntilAsync(() => supervisor.State == ComponentState.Ok);
        Assert.True(DateTime.UtcNow - bootedAt >= TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task ResizesConfiguredArgbZoneBeforeLighting()
    {
        await using var server = new FakeOpenRgbServer(5, ControllerDataWriter.Motherboard());
        await using var run = Start(server, out var supervisor, out _);

        await WaitUntilAsync(() => server.LastColors.ContainsKey(0));

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
    public async Task Resume_RescansInTheSameOpenRgbAndResendsModes()
    {
        // After sleep the RAM ran its own rainbow (blinking against our frames) and the GPU stayed dark until the
        // modes were pushed again.
        await using var server = new FakeOpenRgbServer(5, ControllerDataWriter.Keyboard(), ControllerDataWriter.Mouse());
        await using var run = Start(server, out var supervisor, out var launcher);
        var applied = 0;
        supervisor.DevicesApplied += _ => Interlocked.Increment(ref applied);
        await WaitUntilAsync(() => server.UpdateModeRequests.Count == 2);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        var appliedBefore = Volatile.Read(ref applied);

        supervisor.NotifyResumed();

        await WaitUntilAsync(() => Volatile.Read(ref applied) == appliedBefore + 1 && server.UpdateModeRequests.Count == 4);
        Assert.Equal(1, Volatile.Read(ref server.RescanRequests));
        Assert.Equal(1, launcher.Starts);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.Equal(appliedBefore + 1, Volatile.Read(ref applied));
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
        await WaitUntilAsync(() => server.LastColors.ContainsKey(0));

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
        var supervisor = new OpenRgbSupervisor(launcher, config, NullSink.Instance, new StartupClock(), Logger.None,
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
        var config = new OpenRgbConfig { Port = server.Port, DeviceSettleMilliseconds = 100, ResumeRescanDelayMilliseconds = 50 };
        var lighting = new LightingController(StaticCyan, strip: null, stripLedCount: 0, Logger.None);
        lighting.Start();
        supervisor = new OpenRgbSupervisor(launcher, config, lighting, new StartupClock(), Logger.None, timings ?? FastTimings);
        return new RunningSupervisor(supervisor, lighting);
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
        private readonly LightingController? _lighting;

        public RunningSupervisor(OpenRgbSupervisor supervisor, LightingController? lighting = null)
        {
            _lighting = lighting;
            _task = supervisor.RunAsync(_stop.Token);
        }

        public async ValueTask DisposeAsync()
        {
            if (!_stop.IsCancellationRequested)
            {
                await _stop.CancelAsync();
                await _task;
            }

            _lighting?.Dispose();
            _stop.Dispose();
        }
    }

    private sealed class NullSink : IDeviceLightingSink
    {
        public static NullSink Instance { get; } = new();

        public void Attach(RGBcontroller.Core.OpenRgb.OpenRgbSdkClient client, IReadOnlyList<RGBcontroller.Core.OpenRgb.ControllerData> devices)
        {
        }

        public void Detach()
        {
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
