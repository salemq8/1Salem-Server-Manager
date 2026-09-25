using System.Net;
using System.Text.Json;
using ServerManager.Connect.App.Broker;

namespace ServerManager.Connect.App.Transport;

/// <summary>
/// Reads the transport's response objects. Anything missing or of the wrong type is
/// <see cref="TransportErrorCodes.InvalidResponse"/>. The local address is checked to be loopback
/// before the UI ever shows it: the app tells a friend to point a game at that address.
/// </summary>
internal static class TransportResponses
{
    public static TransportHello Hello(JsonElement response)
    {
        var mode = String(response, "mode");
        return mode is "fake" or "tsnet"
            ? new TransportHello(Int(response, "v"), mode, String(response, "version"))
            : throw Invalid();
    }

    public static TransportStatus Status(JsonElement response) =>
        new(Objects(response, "nodes", Node), Objects(response, "sessions", Session));

    public static string EnrolledNodeId(JsonElement response)
    {
        var nodeId = String(response, "nodeId");
        return BrokerFormats.IsNodeId(nodeId) ? nodeId : throw Invalid();
    }

    public static TransportOpened Opened(JsonElement response) =>
        new(NonEmpty(response, "sessionId"), LoopbackEndpoint(response, "local"));

    public static TransportDiagnostics Diagnostics(JsonElement response) =>
        new(
            String(response, "mode"),
            String(response, "version"),
            Strings(response, "kids"),
            Objects(response, "nodes", Node),
            Objects(response, "sessions", SessionDetail),
            Strings(response, "log"));

    private static TransportNode Node(JsonElement item)
    {
        string? nodeId = null;
        if (item.TryGetProperty("nodeId", out var value))
        {
            nodeId = value.ValueKind == JsonValueKind.String ? value.GetString() : throw Invalid();
        }

        return new TransportNode(NonEmpty(item, "node"), string.IsNullOrEmpty(nodeId) ? null : nodeId, String(item, "state"));
    }

    private static TransportSession Session(JsonElement item) =>
        new(NonEmpty(item, "sessionId"), String(item, "sid"), LoopbackEndpoint(item, "local"), String(item, "state"));

    private static TransportSessionDetail SessionDetail(JsonElement item) =>
        new(
            NonEmpty(item, "sessionId"),
            String(item, "local"),
            String(item, "state"),
            String(item, "node"),
            String(item, "hostBridge"),
            Int(item, "connections"),
            item.TryGetProperty("expiresAt", out var expiresAt) && expiresAt.TryGetInt64(out var seconds) ? seconds : throw Invalid());

    private static IReadOnlyList<T> Objects<T>(JsonElement response, string name, Func<JsonElement, T> read) =>
        Array(response, name).Select(item => item.ValueKind == JsonValueKind.Object ? read(item) : throw Invalid()).ToList();

    private static IReadOnlyList<string> Strings(JsonElement response, string name) =>
        Array(response, name).Select(item => item.ValueKind == JsonValueKind.String ? item.GetString()! : throw Invalid()).ToList();

    private static JsonElement.ArrayEnumerator Array(JsonElement response, string name) =>
        response.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray()
            : throw Invalid();

    private static string LoopbackEndpoint(JsonElement response, string name)
    {
        var text = String(response, name);
        return IPEndPoint.TryParse(text, out var endpoint) && IPAddress.IsLoopback(endpoint.Address) && endpoint.Port > 0
            ? text
            : throw Invalid();
    }

    private static string NonEmpty(JsonElement response, string name) =>
        String(response, name) is { Length: > 0 } text ? text : throw Invalid();

    private static string String(JsonElement response, string name) =>
        response.ValueKind == JsonValueKind.Object &&
        response.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw Invalid();

    private static int Int(JsonElement response, string name) =>
        response.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : throw Invalid();

    private static TransportException Invalid() => new(TransportErrorCodes.InvalidResponse);
}
