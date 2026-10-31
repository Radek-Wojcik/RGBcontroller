using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RGBcontroller.Service.Hardware;

/// <summary>
/// One-shot waitable timer with sub-millisecond precision (CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, Windows 10 1803+)
/// without raising the global timer resolution, so a 30 FPS loop does not drift to the 15.6 ms tick.
/// Falls back to a normal waitable timer on older systems.
/// </summary>
internal sealed partial class HighResolutionTimer : WaitHandle
{
    private const uint CreateWaitableTimerHighResolution = 0x2;
    private const uint TimerAllAccess = 0x1F0003;

    public HighResolutionTimer()
    {
        var handle = CreateWaitableTimerExW(IntPtr.Zero, null, CreateWaitableTimerHighResolution, TimerAllAccess);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            handle = CreateWaitableTimerExW(IntPtr.Zero, null, 0, TimerAllAccess);
        }
        else
        {
            IsHighResolution = true;
        }

        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateWaitableTimerEx failed.");
        }

        SafeWaitHandle = handle;
    }

    public bool IsHighResolution { get; }

    /// <summary>Signals the handle once after <paramref name="delay"/>; re-arming replaces a pending due time.</summary>
    public void Arm(TimeSpan delay)
    {
        var dueTime = -Math.Max(1, delay.Ticks); // negative = relative, in 100 ns units (one TimeSpan tick)
        if (!SetWaitableTimer(SafeWaitHandle, in dueTime, 0, IntPtr.Zero, IntPtr.Zero, false))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "SetWaitableTimer failed.");
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeWaitHandle CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWaitableTimer(
        SafeWaitHandle timer, in long dueTime, int period, IntPtr completionRoutine, IntPtr argument, [MarshalAs(UnmanagedType.Bool)] bool resume);
}
