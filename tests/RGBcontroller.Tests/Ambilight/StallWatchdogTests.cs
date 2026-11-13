using RGBcontroller.Core.Ambilight;

namespace RGBcontroller.Tests.Ambilight;

public sealed class StallWatchdogTests
{
    private sealed class Clock
    {
        public TimeSpan Now;

        public TimeSpan Read() => Now;
    }

    [Fact]
    public void IsStalled_FalseWhileTheWorkerKeepsBeating()
    {
        var clock = new Clock();
        var watchdog = new StallWatchdog(TimeSpan.FromSeconds(20), clock.Read);

        clock.Now = TimeSpan.FromSeconds(15);
        watchdog.Beat("capture");
        clock.Now = TimeSpan.FromSeconds(30);

        Assert.False(watchdog.IsStalled(out var idle, out var stage));
        Assert.Equal(TimeSpan.FromSeconds(15), idle);
        Assert.Equal("capture", stage);
    }

    [Fact]
    public void IsStalled_TrueAfterTheLimitAndNamesTheStage()
    {
        var clock = new Clock();
        var watchdog = new StallWatchdog(TimeSpan.FromSeconds(20), clock.Read);
        watchdog.Beat("map staging texture");

        clock.Now = TimeSpan.FromSeconds(20.5);

        Assert.True(watchdog.IsStalled(out var idle, out var stage));
        Assert.Equal("map staging texture", stage);
        Assert.True(idle > TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void IsStalled_ExactlyAtTheLimitIsStillFine()
    {
        var clock = new Clock();
        var watchdog = new StallWatchdog(TimeSpan.FromSeconds(20), clock.Read);

        clock.Now = TimeSpan.FromSeconds(20);

        Assert.False(watchdog.IsStalled(out _, out _));
    }

    [Fact]
    public void Beat_RecoversAStalledWatchdog()
    {
        var clock = new Clock();
        var watchdog = new StallWatchdog(TimeSpan.FromSeconds(5), clock.Read);
        clock.Now = TimeSpan.FromSeconds(60);
        Assert.True(watchdog.IsStalled(out _, out _));

        watchdog.Beat("wait");

        Assert.False(watchdog.IsStalled(out _, out var stage));
        Assert.Equal("wait", stage);
    }

    [Theory]
    [InlineData("--restart-after 123 0", true, 123, 0)]
    [InlineData("--restart-after 4567 2", true, 4567, 2)]
    [InlineData("--restart-after 0 1", false, 0, 0)]
    [InlineData("--restart-after -5 1", false, 0, 0)]
    [InlineData("--restart-after abc 1", false, 0, 0)]
    [InlineData("--restart-after 12", false, 0, 0)]
    [InlineData("--capture-probe 12 3", false, 0, 0)]
    [InlineData("", false, 0, 0)]
    public void TryParse_AcceptsOnlyTheRestartForm(string line, bool expected, int pid, int count)
    {
        var args = line.Length == 0 ? [] : line.Split(' ');

        Assert.Equal(expected, AgentRestart.TryParse(args, out var restart));
        Assert.Equal(pid, restart.PreviousProcessId);
        Assert.Equal(count, restart.Count);
    }

    [Fact]
    public void ToArguments_RoundTripsThroughTryParse()
    {
        var restart = new AgentRestart(321, 2);

        Assert.True(AgentRestart.TryParse(restart.ToArguments().Split(' '), out var parsed));
        Assert.Equal(restart, parsed);
    }

    [Fact]
    public void MayRestart_StopsAfterThreeQuickRestartsButAStableRunStartsOver()
    {
        Assert.True(AgentRestart.MayRestart(0, TimeSpan.FromSeconds(30)));
        Assert.True(AgentRestart.MayRestart(2, TimeSpan.FromMinutes(1)));
        Assert.False(AgentRestart.MayRestart(3, TimeSpan.FromMinutes(1)));
        Assert.True(AgentRestart.MayRestart(3, TimeSpan.FromMinutes(11)));
    }

    [Fact]
    public void NextCount_CountsUpUnlessTheRunWasStable()
    {
        Assert.Equal(3, AgentRestart.NextCount(2, TimeSpan.FromMinutes(2)));
        Assert.Equal(1, AgentRestart.NextCount(2, TimeSpan.FromMinutes(30)));
    }
}
