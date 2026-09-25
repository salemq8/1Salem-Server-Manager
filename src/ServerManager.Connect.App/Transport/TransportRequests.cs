using System.Buffers;
using System.Text.Json;

namespace ServerManager.Connect.App.Transport;

/// <summary>
/// Every request line this app can send on the friend pipe, built in one place so a test can
/// check the complete set of field names. The transport decodes strictly and refuses unknown
/// fields, and none of these carries a destination (§11): the fields are the envelope
/// (<c>id</c>, <c>op</c>) plus exactly the operation's own inputs.
/// </summary>
internal static class TransportRequests
{
    public const int ProtocolVersion = 1;

    public static byte[] Hello(long id) =>
        Build(id, "hello", writer => writer.WriteNumber("v", ProtocolVersion));

    public static byte[] Status(long id) => Build(id, "status", null);

    public static byte[] Enroll(long id, string node, string authKey, string hostname) =>
        Build(id, "enroll", writer =>
        {
            writer.WriteString("node", node);
            writer.WriteString("authKey", authKey);
            writer.WriteString("hostname", hostname);
        });

    public static byte[] Open(long id, string node, string ticket, string sessionKey, int preferredPort) =>
        Build(id, "open", writer =>
        {
            writer.WriteString("node", node);
            writer.WriteString("ticket", ticket);
            writer.WriteString("sessionKey", sessionKey);
            writer.WriteNumber("preferredPort", preferredPort);
        });

    public static byte[] Refresh(long id, string sessionId, string ticket, string sessionKey) =>
        Build(id, "refresh", writer =>
        {
            writer.WriteString("sessionId", sessionId);
            writer.WriteString("ticket", ticket);
            writer.WriteString("sessionKey", sessionKey);
        });

    public static byte[] Close(long id, string sessionId) =>
        Build(id, "close", writer => writer.WriteString("sessionId", sessionId));

    public static byte[] Diag(long id) => Build(id, "diag", null);

    private static byte[] Build(long id, string operation, Action<Utf8JsonWriter>? writeFields)
    {
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", id);
            writer.WriteString("op", operation);
            writeFields?.Invoke(writer);
            writer.WriteEndObject();
        }

        var line = buffer.WrittenSpan.ToArray();

        // The writer's buffer may hold an auth key or a session key; the caller wipes the returned copy.
        buffer.Clear();
        return line;
    }
}
