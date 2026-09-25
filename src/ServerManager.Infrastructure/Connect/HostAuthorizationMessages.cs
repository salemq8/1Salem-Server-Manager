using System.Buffers;
using System.Net;
using System.Text.Json;

namespace ServerManager.Infrastructure.Connect;

/// <summary>
/// The lines the Agent writes on the host authorization pipe (contract §11). Responses echo the
/// request id: <c>{"id":n,"ok":true,…}</c> or <c>{"id":n,"ok":false,"error":code}</c>. Events
/// carry no id: <c>{"event":"close","connIds":[…]}</c>.
/// A denial is an ordinary response with <c>"decision":"deny"</c> and nothing else, so the host
/// transport, and through it the friend, learns nothing about why.
/// </summary>
internal static class HostAuthorizationMessages
{
    public const int ProtocolVersion = 1;

    /// <summary>
    /// Keeps one close event far below the 64 KiB line limit: a connection id is 22 characters,
    /// so 500 of them take about 13 KiB.
    /// </summary>
    public const int MaxConnectionIdsPerEvent = 500;

    public static class Errors
    {
        public const string BadRequest = "bad_request";
        public const string HelloRequired = "hello_required";
        public const string UnknownOperation = "unknown_op";
        public const string UnsupportedVersion = "unsupported_version";
    }

    public static byte[] Ok(long id) => Write(writer => WriteHeader(writer, id, ok: true));

    public static byte[] Hello(long id) => Write(writer =>
    {
        WriteHeader(writer, id, ok: true);
        writer.WriteNumber("v", ProtocolVersion);
    });

    public static byte[] Allow(long id, IPEndPoint endpoint, string connectionId) => Write(writer =>
    {
        WriteHeader(writer, id, ok: true);
        writer.WriteString("decision", "allow");
        writer.WriteString("endpoint", endpoint.ToString());
        writer.WriteString("connId", connectionId);
    });

    public static byte[] Deny(long id) => Write(writer =>
    {
        WriteHeader(writer, id, ok: true);
        writer.WriteString("decision", "deny");
    });

    public static byte[] Error(long id, string code) => Write(writer =>
    {
        WriteHeader(writer, id, ok: false);
        writer.WriteString("error", code);
    });

    /// <summary>One or more close events covering every id, each within the line limit.</summary>
    public static IReadOnlyList<byte[]> CloseEvents(IReadOnlyList<string> connectionIds) =>
        connectionIds
            .Chunk(MaxConnectionIdsPerEvent)
            .Select(chunk => Write(writer =>
            {
                writer.WriteString("event", "close");
                writer.WriteStartArray("connIds");
                foreach (var connectionId in chunk)
                {
                    writer.WriteStringValue(connectionId);
                }

                writer.WriteEndArray();
            }))
            .ToArray();

    private static void WriteHeader(Utf8JsonWriter writer, long id, bool ok)
    {
        writer.WriteNumber("id", id);
        writer.WriteBoolean("ok", ok);
    }

    private static byte[] Write(Action<Utf8JsonWriter> writeMembers)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writeMembers(writer);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }
}
