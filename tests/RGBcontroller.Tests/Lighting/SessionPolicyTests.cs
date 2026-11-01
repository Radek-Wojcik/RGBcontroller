using RGBcontroller.Core.Config;
using RGBcontroller.Core.Lighting;

namespace RGBcontroller.Tests.Lighting;

public sealed class SessionPolicyTests
{
    private static readonly LightingConfig Config = new() { ActiveProfile = "Cyberpunk" };

    [Theory]
    [InlineData(SessionKind.Unlocked, null, "Cyberpunk")]
    [InlineData(SessionKind.Locked, null, "Cyberpunk")]
    [InlineData(SessionKind.Locked, "Night", "Night")]
    [InlineData(SessionKind.LogonScreen, "Night", DefaultLighting.LogonProfileName)]
    public void Profile_FollowsTheSession(SessionKind session, string? lockProfile, string expected) =>
        Assert.Equal(expected, SessionPolicy.ProfileFor(Config with { LockProfile = lockProfile }, session));

    [Fact]
    public void DisplayOn_TurnsNothingOff()
    {
        var off = SessionPolicy.TurnedOff(Config, new(SessionKind.Locked, DisplayOn: true));

        Assert.False(off.Strip);
        Assert.Empty(off.Devices);
    }

    [Fact]
    public void DisplayOff_UsesTheSpecDefaults()
    {
        // SPEC 4: unlocked idle turns off the strip only; locked also the keyboard and mouse.
        var unlocked = SessionPolicy.TurnedOff(Config, new(SessionKind.Unlocked, DisplayOn: false));
        Assert.True(unlocked.Strip);
        Assert.Empty(unlocked.Devices);

        foreach (var session in new[] { SessionKind.Locked, SessionKind.LogonScreen })
        {
            var locked = SessionPolicy.TurnedOff(Config, new(session, DisplayOn: false));
            Assert.True(locked.Strip);
            Assert.True(locked.Contains(DefaultLighting.Keyboard));
            Assert.True(locked.Contains(DefaultLighting.Mouse.ToUpperInvariant()));
            Assert.False(locked.Contains(DefaultLighting.Gpu));
        }
    }
}
