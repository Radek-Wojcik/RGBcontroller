using System.Runtime.InteropServices;
using RGBcontroller.Core.Lighting;

namespace RGBcontroller.Service.Power;

/// <summary>Snapshot of the session attached to the physical console.</summary>
internal readonly record struct ConsoleSessionInfo(uint SessionId, string User, SessionKind Kind)
{
    public override string ToString() => $"session {SessionId} {Kind}{(User.Length > 0 ? $" ({User})" : "")}";
}

/// <summary>
/// Reads the console session state from WTS. Session change events only say that something happened (and .NET
/// delivers them on thread-pool threads, possibly out of order), so the state is always re-read from here.
/// </summary>
internal static partial class ConsoleSession
{
    private const uint NoSession = 0xFFFFFFFF;
    private const int WtsSessionInfoEx = 25;
    private const int SessionFlagsOffset = 16;
    private const int UserNameOffset = 86;
    private const int WtsSessionStateLock = 0;

    public static ConsoleSessionInfo Query()
    {
        var sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == NoSession)
        {
            // The console is between sessions (e.g. during "Switch user"): nobody is at a desktop.
            return new(sessionId, "", SessionKind.LogonScreen);
        }

        if (!WTSQuerySessionInformationW(0, sessionId, WtsSessionInfoEx, out var buffer, out _))
        {
            return new(sessionId, "", SessionKind.LogonScreen);
        }

        try
        {
            // WTSINFOEXW: DWORD Level, then the 8-byte aligned WTSINFOEX_LEVEL1_W
            // (SessionId, SessionState, SessionFlags, WinStationName[33], UserName[21], ...).
            var flags = Marshal.ReadInt32(buffer, SessionFlagsOffset);
            var user = Marshal.PtrToStringUni(buffer + UserNameOffset) ?? "";
            var kind = user.Length == 0 ? SessionKind.LogonScreen
                : flags == WtsSessionStateLock ? SessionKind.Locked
                : SessionKind.Unlocked;
            return new(sessionId, user, kind);
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    [LibraryImport("kernel32.dll")]
    private static partial uint WTSGetActiveConsoleSessionId();

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSQuerySessionInformationW(nint server, uint sessionId, int infoClass, out nint buffer, out uint bytes);

    [LibraryImport("wtsapi32.dll")]
    private static partial void WTSFreeMemory(nint memory);
}
