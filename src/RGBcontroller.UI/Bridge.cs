using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using RGBcontroller.Ipc;
using StreamJsonRpc;

namespace RGBcontroller.UI;

/// <summary>
/// Translates the web page's messages into Service calls. The page posts <c>{"id": 1, "method": "getConfig",
/// "params": {...}}</c> and receives <c>{"id": 1, "result": ...}</c> or <c>{"id": 1, "error": "..."}</c>; Service
/// state changes arrive as <c>{"event": "state", "data": ...}</c>. Only the methods listed here exist: the page can do
/// nothing the IPC surface does not already allow (SPEC 9), and every argument is validated again by the Service.
/// </summary>
internal sealed class Bridge(Func<IServiceControl?> service)
{
    private static readonly JsonSerializerOptions Json = IpcRpc.CreateJsonOptions();

    /// <summary>Handles one request and returns the reply to post back to the page.</summary>
    public async Task<string> HandleAsync(string message, CancellationToken cancellationToken)
    {
        JsonNode? id = null;
        try
        {
            var request = JsonNode.Parse(message) as JsonObject ?? throw new JsonException("A request is a JSON object.");
            id = request["id"]?.DeepClone();
            var method = request["method"]?.GetValue<string>() ?? throw new JsonException("The request has no method.");
            var parameters = request["params"];
            var target = service() ?? throw new BridgeException("The RGBcontroller Service is not connected.");
            var result = await InvokeAsync(target, method, parameters, cancellationToken).ConfigureAwait(false);
            return Reply(id, "result", JsonSerializer.SerializeToNode(result, Json));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or BridgeException
                                       or RemoteRpcException or ConnectionLostException or ObjectDisposedException or IOException)
        {
            return Reply(id, "error", JsonValue.Create(ex.Message));
        }
    }

    /// <summary>An event for the page, e.g. "state" with the Service's <see cref="AgentState"/>.</summary>
    public static string Event(string name, object? data) =>
        new JsonObject { ["event"] = name, ["data"] = JsonSerializer.SerializeToNode(data, Json) }.ToJsonString();

    private static async Task<object?> InvokeAsync(IServiceControl s, string method, JsonNode? p, CancellationToken ct) => method switch
    {
        "getStatus" => await s.GetStatusAsync(ct).ConfigureAwait(false),
        "getState" => await s.SubscribeAsync(ct).ConfigureAwait(false),
        "getProfiles" => await s.GetProfilesAsync(ct).ConfigureAwait(false),
        "setActiveProfile" => await s.SetActiveProfileAsync(Arg<string>(p, "name"), ct).ConfigureAwait(false),
        "stepProfile" => await s.StepProfileAsync(Arg<int>(p, "delta"), ct).ConfigureAwait(false),
        "tuneGroup" => await s.TuneGroupAsync(Params<GroupTuningRequest>(p), ct).ConfigureAwait(false),
        "changeStrip" => await s.ChangeStripAsync(Params<StripChangeRequest>(p), ct).ConfigureAwait(false),
        "setAllOff" => await s.SetAllOffAsync(Arg<bool>(p, "off"), ct).ConfigureAwait(false),
        "showStripTest" => await s.ShowStripTestAsync(Arg<int>(p, "seconds"), ct).ConfigureAwait(false),
        "getAgentSettings" => await s.GetAgentSettingsAsync(ct).ConfigureAwait(false),
        "getConfig" => await s.GetConfigAsync(ct).ConfigureAwait(false),
        "updateConfig" => await s.UpdateConfigAsync(Params<ConfigUpdateRequest>(p), ct).ConfigureAwait(false),
        "getDevices" => await s.GetDevicesAsync(ct).ConfigureAwait(false),
        "getStripColors" => await s.GetStripColorsAsync(ct).ConfigureAwait(false),
        "reopenStrip" => await s.ReopenStripAsync(ct).ConfigureAwait(false),
        _ => throw new BridgeException($"Unknown method '{method}'."),
    };

    private static T Params<T>(JsonNode? parameters) =>
        parameters.Deserialize<T>(Json) ?? throw new JsonException($"Missing parameters for {typeof(T).Name}.");

    private static T Arg<T>(JsonNode? parameters, string name)
    {
        var value = parameters?[name] ?? throw new JsonException($"Missing parameter '{name}'.");
        return value.Deserialize<T>(Json) ?? throw new JsonException($"Parameter '{name}' is null.");
    }

    private static string Reply(JsonNode? id, string kind, JsonNode? payload) =>
        new JsonObject { ["id"] = id, [kind] = payload }.ToJsonString();

    private sealed class BridgeException(string message) : Exception(message);
}
