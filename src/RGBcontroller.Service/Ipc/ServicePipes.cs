using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace RGBcontroller.Service.Ipc;

/// <summary>Named pipe instances with the Service's ACL (SPEC 3) and the identity of the connected client.</summary>
internal static partial class ServicePipes
{
    /// <summary>
    /// SYSTEM and Administrators full control, interactive users read/write (tray, UI, command line without elevation),
    /// and the creating account, which must be able to add instances (SYSTEM for the Service, the developer in console
    /// mode and tests).
    /// </summary>
    public static NamedPipeServerStream Create(string pipeName, int maxInstances, bool firstInstance)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        using var self = WindowsIdentity.GetCurrent();
        security.AddAccessRule(new PipeAccessRule(self.User!, PipeAccessRights.FullControl, AccessControlType.Allow));

        var options = PipeOptions.Asynchronous | (firstInstance ? PipeOptions.FirstPipeInstance : PipeOptions.None);
        return NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, maxInstances, PipeTransmissionMode.Byte, options, 0, 0, security);
    }

    /// <summary>Windows session of the process at the other end of a connected pipe, or null when it cannot be read.</summary>
    public static uint? ClientSessionId(NamedPipeServerStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        return GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var processId) && ProcessIdToSessionId(processId, out var sessionId)
            ? sessionId
            : null;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ProcessIdToSessionId(uint processId, out uint sessionId);
}
