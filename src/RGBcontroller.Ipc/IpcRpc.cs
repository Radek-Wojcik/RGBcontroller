using System.Text.Json;
using System.Text.Json.Serialization;
using StreamJsonRpc;

namespace RGBcontroller.Ipc;

/// <summary>Shared JSON-RPC setup so the Service, Agent and UI serialize identically.</summary>
public static class IpcRpc
{
    public static IJsonRpcMessageFormatter CreateFormatter()
    {
        var formatter = new SystemTextJsonFormatter();
        formatter.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        formatter.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        return formatter;
    }

    /// <summary>Creates an RPC connection over a duplex stream (a named pipe in production). Call StartListening after attaching targets.</summary>
    public static JsonRpc CreateConnection(Stream duplexStream) =>
        new(new HeaderDelimitedMessageHandler(duplexStream, duplexStream, CreateFormatter()));
}
