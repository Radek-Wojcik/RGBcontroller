using RGBcontroller.Core.Config;
using RGBcontroller.Ipc;
using RGBcontroller.Service;

namespace RGBcontroller.Tests.Service;

public sealed class StripModeTests
{
    [Fact]
    public void SolidTogglesBackToThePreviousMode()
    {
        AmbilightMode? beforeSolid = null, beforeOff = null;
        var solid = LightingHost.NextStripMode(AmbilightMode.Radial, new StripChangeRequest(ToggleSolid: true), ref beforeSolid, ref beforeOff);
        var back = LightingHost.NextStripMode(solid, new StripChangeRequest(ToggleSolid: true), ref beforeSolid, ref beforeOff);

        Assert.Equal(AmbilightMode.Solid, solid);
        Assert.Equal(AmbilightMode.Radial, back);
    }

    [Fact]
    public void RaisingTheBrightnessOfAnOffStripTurnsItBackOn()
    {
        AmbilightMode? beforeSolid = null, beforeOff = null;
        var off = LightingHost.NextStripMode(AmbilightMode.Radial, new StripChangeRequest(CycleMode: true), ref beforeSolid, ref beforeOff);
        Assert.Equal(AmbilightMode.Solid, off);
        off = LightingHost.NextStripMode(off, new StripChangeRequest(CycleMode: true), ref beforeSolid, ref beforeOff);
        Assert.Equal(AmbilightMode.Off, off);

        Assert.Equal(AmbilightMode.Off, LightingHost.NextStripMode(off, new StripChangeRequest(BrightnessDelta: -10), ref beforeSolid, ref beforeOff));
        Assert.Equal(AmbilightMode.Solid, LightingHost.NextStripMode(off, new StripChangeRequest(BrightnessDelta: 10), ref beforeSolid, ref beforeOff));
        Assert.Equal(AmbilightMode.Solid, LightingHost.NextStripMode(off, new StripChangeRequest(BrightnessPercent: 80), ref beforeSolid, ref beforeOff));
    }

    [Fact]
    public void AStripThatStartedOffComesBackInScreenMode()
    {
        AmbilightMode? beforeSolid = null, beforeOff = null;
        Assert.Equal(AmbilightMode.Screen,
            LightingHost.NextStripMode(AmbilightMode.Off, new StripChangeRequest(BrightnessPercent: 100), ref beforeSolid, ref beforeOff));
        Assert.Equal(AmbilightMode.Off,
            LightingHost.NextStripMode(AmbilightMode.Off, new StripChangeRequest(BrightnessPercent: 0), ref beforeSolid, ref beforeOff));
    }
}
