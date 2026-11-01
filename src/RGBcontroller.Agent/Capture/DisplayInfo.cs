using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace RGBcontroller.Agent.Capture;

/// <summary>Monitor facts the capture pipeline needs from Windows: physical size (EDID) and the HDR SDR white level.</summary>
internal static unsafe partial class DisplayInfo
{
    private const uint EddGetDeviceInterfaceName = 1;
    private const uint QdcOnlyActivePaths = 2;
    private const uint DeviceInfoGetSourceName = 1;
    private const uint DeviceInfoGetSdrWhiteLevel = 11;

    /// <summary>Width of the visible screen in cm from the monitor's EDID, or null when Windows does not expose it.</summary>
    /// <param name="gdiName">\\.\DISPLAY1 as DXGI reports it.</param>
    public static double? ScreenWidthCm(string gdiName)
    {
        var device = new DisplayDevice { Size = (uint)sizeof(DisplayDevice) };
        if (!EnumDisplayDevicesW(gdiName, 0, ref device, EddGetDeviceInterfaceName))
        {
            return null;
        }

        // \\?\DISPLAY#AOC3201#5&2b3a...&UID4352#{e6f07b5f-...}: model and instance of the monitor device node.
        var parts = new string(device.DeviceId).TrimEnd('\0').Split('#');
        if (parts.Length < 3)
        {
            return null;
        }

        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\DISPLAY\{parts[1]}\{parts[2]}\Device Parameters");
        if (key?.GetValue("EDID") is not byte[] { Length: >= 128 } edid)
        {
            return null;
        }

        // The first detailed timing descriptor has the image size in mm; bytes 21-22 the rounded size in cm.
        var millimeters = edid[66] | ((edid[68] & 0xF0) << 4);
        return millimeters > 0 ? millimeters / 10.0 : edid[21] > 0 ? edid[21] : null;
    }

    /// <summary>The Windows "SDR content brightness" of an HDR monitor in nits, or null when unknown.</summary>
    public static double? SdrWhiteNits(string gdiName)
    {
        if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out var pathCount, out var modeCount) != 0)
        {
            return null;
        }

        var paths = new PathInfo[pathCount];
        var modes = new ModeInfo[modeCount];
        fixed (PathInfo* p = paths)
        fixed (ModeInfo* m = modes)
        {
            if (QueryDisplayConfig(QdcOnlyActivePaths, ref pathCount, p, ref modeCount, m, IntPtr.Zero) != 0)
            {
                return null;
            }
        }

        for (var i = 0; i < pathCount; i++)
        {
            var source = new SourceDeviceName
            {
                Header = new DeviceInfoHeader(DeviceInfoGetSourceName, (uint)sizeof(SourceDeviceName), paths[i].SourceAdapterId, paths[i].SourceId),
            };
            if (DisplayConfigGetDeviceInfo(&source.Header) != 0
                || !string.Equals(new string(source.GdiDeviceName).TrimEnd('\0'), gdiName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var white = new SdrWhiteLevel
            {
                Header = new DeviceInfoHeader(DeviceInfoGetSdrWhiteLevel, (uint)sizeof(SdrWhiteLevel), paths[i].TargetAdapterId, paths[i].TargetId),
            };
            if (DisplayConfigGetDeviceInfo(&white.Header) != 0)
            {
                return null;
            }

            // 1000 means 80 nits.
            return white.Level / 1000.0 * 80;
        }

        return null;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public uint Size;
        public fixed char DeviceName[32];
        public fixed char DeviceString[128];
        public uint StateFlags;
        public fixed char DeviceId[128];
        public fixed char DeviceKey[128];
    }

    /// <summary>DISPLAYCONFIG_PATH_INFO; LUIDs are two 32-bit halves, so the struct is 4-byte packed (72 bytes).</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PathInfo
    {
        public long SourceAdapterId;
        public uint SourceId;
        public uint SourceModeInfoIdx;
        public uint SourceStatusFlags;
        public long TargetAdapterId;
        public uint TargetId;
        public uint TargetModeInfoIdx;
        public uint OutputTechnology;
        public uint Rotation;
        public uint Scaling;
        public uint RefreshNumerator;
        public uint RefreshDenominator;
        public uint ScanLineOrdering;
        public int TargetAvailable;
        public uint TargetStatusFlags;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct ModeInfo;

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private readonly struct DeviceInfoHeader(uint type, uint size, long adapterId, uint id)
    {
        public readonly uint Type = type;
        public readonly uint Size = size;
        public readonly long AdapterId = adapterId;
        public readonly uint Id = id;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct SourceDeviceName
    {
        public DeviceInfoHeader Header;
        public fixed char GdiDeviceName[32];
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct SdrWhiteLevel
    {
        public DeviceInfoHeader Header;
        public uint Level;
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumDisplayDevicesW(string device, uint index, ref DisplayDevice displayDevice, uint flags);

    [LibraryImport("user32.dll")]
    private static partial int GetDisplayConfigBufferSizes(uint flags, out int pathCount, out int modeCount);

    [LibraryImport("user32.dll")]
    private static partial int QueryDisplayConfig(uint flags, ref int pathCount, PathInfo* paths, ref int modeCount, ModeInfo* modes, IntPtr topologyId);

    [LibraryImport("user32.dll")]
    private static partial int DisplayConfigGetDeviceInfo(DeviceInfoHeader* header);
}
