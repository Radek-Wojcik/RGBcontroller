using RGBcontroller.Service.Power;

namespace RGBcontroller.Tests.Service;

public sealed class UsbArrivalMonitorTests
{
    [Fact]
    public void RegistersWithWindowsAndUnregistersCleanly()
    {
        // CM_Register_Notification checks the filter size; a wrong struct layout fails here with CR_INVALID_DATA.
        var monitor = new UsbArrivalMonitor(_ => { }, ex => Assert.Fail(ex.Message));
        monitor.Start();
        monitor.Dispose();
        monitor.Dispose();
    }
}
