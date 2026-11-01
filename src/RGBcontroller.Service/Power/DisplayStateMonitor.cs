using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace RGBcontroller.Service.Power;

/// <summary>GUID_CONSOLE_DISPLAY_STATE values.</summary>
internal enum DisplayState
{
    Off = 0,
    On = 1,
    Dimmed = 2,
}

/// <summary>
/// Console display on/off/dimmed through <c>PowerSettingRegisterNotification</c> with a callback (spike R4).
/// ServiceBase passes power events on without their POWERBROADCAST_SETTING payload, so the service status handle route
/// is unusable; the callback route needs neither a window nor the status handle and also works in console mode.
/// Windows calls back once right after registration with the current state.
/// </summary>
internal sealed unsafe partial class DisplayStateMonitor : IDisposable
{
    private const uint DeviceNotifyCallback = 2;
    private const uint PbtPowerSettingChange = 0x8013;
    private static readonly Guid ConsoleDisplayState = new("6FE69556-704A-47A0-8F24-C28D936FDA47");

    private readonly Action<DisplayState> _changed;
    private readonly Action<Exception> _failed;
    private GCHandle _self;
    private DeviceNotifySubscribeParameters* _parameters;
    private nint _registration;

    /// <param name="changed">Runs on a system thread; keep it short.</param>
    /// <param name="failed">Receives exceptions thrown by <paramref name="changed"/> (they must not reach native code).</param>
    public DisplayStateMonitor(Action<DisplayState> changed, Action<Exception> failed)
    {
        _changed = changed;
        _failed = failed;
    }

    /// <exception cref="Win32Exception">Registration failed.</exception>
    public void Start()
    {
        _self = GCHandle.Alloc(this);
        _parameters = (DeviceNotifySubscribeParameters*)NativeMemory.Alloc((nuint)sizeof(DeviceNotifySubscribeParameters));
        _parameters->Callback = &OnNotify;
        _parameters->Context = GCHandle.ToIntPtr(_self);
        var guid = ConsoleDisplayState;
        var error = PowerSettingRegisterNotification(&guid, DeviceNotifyCallback, (nint)_parameters, out _registration);
        if (error != 0)
        {
            Dispose();
            throw new Win32Exception((int)error, "PowerSettingRegisterNotification(GUID_CONSOLE_DISPLAY_STATE) failed.");
        }
    }

    public void Dispose()
    {
        if (_registration != 0)
        {
            _ = PowerSettingUnregisterNotification(_registration);
            _registration = 0;
        }

        if (_parameters is not null)
        {
            NativeMemory.Free(_parameters);
            _parameters = null;
        }

        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint OnNotify(nint context, uint type, nint setting)
    {
        if (type != PbtPowerSettingChange || setting == 0 || GCHandle.FromIntPtr(context).Target is not DisplayStateMonitor monitor)
        {
            return 0;
        }

        var data = (PowerBroadcastSetting*)setting;
        if (data->PowerSetting != ConsoleDisplayState || data->DataLength < sizeof(uint))
        {
            return 0;
        }

        try
        {
            monitor._changed((DisplayState)(*(uint*)&data->Data));
        }
        catch (Exception ex)
        {
            monitor._failed(ex);
        }

        return 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceNotifySubscribeParameters
    {
        public delegate* unmanaged[Stdcall]<nint, uint, nint, uint> Callback;
        public nint Context;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerBroadcastSetting
    {
        public Guid PowerSetting;
        public uint DataLength;
        public byte Data;
    }

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerSettingRegisterNotification(Guid* settingGuid, uint flags, nint recipient, out nint registrationHandle);

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerSettingUnregisterNotification(nint registrationHandle);
}
