namespace RGBcontroller.Ipc;

/// <summary>
/// IPC protocol identity. Pipe names are fixed (not versioned) so a mismatched client can still
/// connect and be told about the mismatch instead of failing silently.
/// </summary>
public static class IpcProtocol
{
    /// <summary>Breaking changes bump the major version; additive changes bump the minor version.</summary>
    public static ProtocolVersion Current { get; } = new(1, 0);

    /// <summary>JSON-RPC control channel (config, profiles, status, commands).</summary>
    public const string ControlPipeName = "RGBcontroller.Control";

    /// <summary>Binary stream channel for strip colors (Agent to Service) and live preview (Service to UI).</summary>
    public const string FramePipeName = "RGBcontroller.Frames";
}

public readonly record struct ProtocolVersion(int Major, int Minor)
{
    /// <summary>
    /// Compatibility as seen by a server running <paramref name="server"/> when this instance is the client.
    /// Majors must match; a client with a higher minor may call methods the server does not have.
    /// </summary>
    public VersionCompatibility CheckAgainstServer(ProtocolVersion server)
    {
        if (Major < server.Major)
        {
            return VersionCompatibility.ClientTooOld;
        }

        if (Major > server.Major || Minor > server.Minor)
        {
            return VersionCompatibility.ServerTooOld;
        }

        return VersionCompatibility.Compatible;
    }

    public override string ToString() => $"{Major}.{Minor}";
}

public enum VersionCompatibility
{
    Compatible,
    ClientTooOld,
    ServerTooOld,
}
