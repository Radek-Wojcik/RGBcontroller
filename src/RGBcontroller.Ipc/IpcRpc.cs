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
        Configure(formatter.JsonSerializerOptions);
        return formatter;
    }

    /// <summary>Options matching the wire format (camelCase, enums as strings); the UI bridge uses them for the web page.</summary>
    public static JsonSerializerOptions CreateJsonOptions() => Configure(new JsonSerializerOptions());

    private static JsonSerializerOptions Configure(JsonSerializerOptions options)
    {
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    /// <summary>Creates an RPC connection over a duplex stream (a named pipe in production). Call StartListening after attaching targets.</summary>
    public static JsonRpc CreateConnection(Stream duplexStream) =>
        new(new HeaderDelimitedMessageHandler(duplexStream, duplexStream, CreateFormatter()));
}
