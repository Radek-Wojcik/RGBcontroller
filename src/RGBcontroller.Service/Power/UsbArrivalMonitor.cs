using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace RGBcontroller.Service.Power;

/// <summary>
/// Reports USB devices that appear (SPEC 4, device hot-plug) through <c>CM_Register_Notification</c>, which needs no
/// window and works in Session 0. The owner's keyboard, mouse and strip hang on the monitor's USB hub: when the
/// monitor powers its hub back on they re-enumerate in their factory lighting, and only a rescan inside OpenRGB
/// gets them back. The callback passes the device's symbolic link (e.g. <c>\\?\USB#VID_1532&amp;PID_025D#...</c>).
/// </summary>
internal sealed unsafe partial class UsbArrivalMonitor : IDisposable
{
    private const int FilterTypeDeviceInterface = 0;
    private const int ActionDeviceInterfaceArrival = 0;
    private const int MaxDeviceIdLength = 200;

    /// <summary>GUID_DEVINTERFACE_USB_DEVICE: one notice per USB device, not per HID collection.</summary>
    private static readonly Guid UsbDeviceInterface = new("A5DCBF10-6530-11D2-901F-00C04FB951ED");

    private readonly Action<string> _arrived;
    private readonly Action<Exception> _failed;
    private GCHandle _self;
    private nint _registration;

    /// <param name="arrived">Runs on a system thread with the device's symbolic link; keep it short.</param>
    /// <param name="failed">Receives exceptions thrown by <paramref name="arrived"/> (they must not reach native code).</param>
    public UsbArrivalMonitor(Action<string> arrived, Action<Exception> failed)
    {
        _arrived = arrived;
        _failed = failed;
    }

    /// <exception cref="Win32Exception">Registration failed.</exception>
    public void Start()
    {
        _self = GCHandle.Alloc(this);
        var filter = new NotifyFilter
        {
            Size = (uint)sizeof(NotifyFilter),
            FilterType = FilterTypeDeviceInterface,
            ClassGuid = UsbDeviceInterface,
        };
        var result = CM_Register_Notification(&filter, GCHandle.ToIntPtr(_self), &OnNotify, out _registration);
        if (result != 0)
        {
            Dispose();
            throw new Win32Exception($"CM_Register_Notification(GUID_DEVINTERFACE_USB_DEVICE) failed with CONFIGRET {result}.");
        }
    }

    public void Dispose()
    {
        if (_registration != 0)
        {
            // Waits for callbacks in flight, so the handle below is no longer used afterwards.
            _ = CM_Unregister_Notification(_registration);
            _registration = 0;
        }

        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint OnNotify(nint notification, nint context, int action, NotifyEventData* data, uint size)
    {
        if (action != ActionDeviceInterfaceArrival || data is null || size <= (uint)sizeof(NotifyEventData)
            || GCHandle.FromIntPtr(context).Target is not UsbArrivalMonitor monitor)
        {
            return 0;
        }

        try
        {
            var chars = (int)((size - (uint)sizeof(NotifyEventData)) / sizeof(char));
            var link = new ReadOnlySpan<char>(data + 1, chars);
            var end = link.IndexOf('\0');
            monitor._arrived(new string(end >= 0 ? link[..end] : link));
        }
        catch (Exception ex)
        {
            monitor._failed(ex);
        }

        return 0;
    }

    /// <summary>CM_NOTIFY_FILTER for a device interface class; the union is sized by its largest member (an instance id).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 16 + MaxDeviceIdLength * 2)]
    private struct NotifyFilter
    {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(4)] public uint Flags;
        [FieldOffset(8)] public int FilterType;
        [FieldOffset(12)] public uint Reserved;
        [FieldOffset(16)] public Guid ClassGuid;
    }

    /// <summary>Head of CM_NOTIFY_EVENT_DATA for a device interface (24 bytes); the symbolic link follows it.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyEventData
    {
        public int FilterType;
        public uint Reserved;
        public Guid ClassGuid;
    }

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Register_Notification(
        NotifyFilter* filter, nint context, delegate* unmanaged[Stdcall]<nint, nint, int, NotifyEventData*, uint, uint> callback, out nint notification);

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Unregister_Notification(nint notification);
}
