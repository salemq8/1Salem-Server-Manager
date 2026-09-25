using System.Buffers.Binary;
using System.Text.Json;
using ServerManager.Connect.Core.Json;

namespace ServerManager.Connect.Core.Tickets;

/// <summary>
/// Binary framing of the connection preamble (contract §10):
/// <c>"1SC" 0x01 | uint16 big-endian N (1..4096) | N bytes of UTF-8 JSON</c>, answered by one
/// status byte: <see cref="Accepted"/> (the raw stream follows) or <see cref="Refused"/>
/// (generic, then close). Everything is length-checked before it is buffered, so a peer
/// cannot make the reader allocate more than 4 KiB.
/// </summary>
public static class PreambleCodec
{
    public const int MaxJsonLength = 4096;
    public const byte Accepted = 0x00;
    public const byte Refused = 0x01;

    private const int HeaderLength = 6;

    private static ReadOnlySpan<byte> Magic => "1SC\x01"u8;

    public static byte[] Encode(ConnectionPreamble preamble)
    {
        ArgumentNullException.ThrowIfNull(preamble);
        using var json = new MemoryStream();
        using (var writer = new Utf8JsonWriter(json))
        {
            preamble.WriteTo(writer);
        }

        var body = json.ToArray();
        if (body.Length > MaxJsonLength)
        {
            throw new InvalidOperationException("The preamble JSON exceeds 4096 bytes.");
        }

        var frame = new byte[HeaderLength + body.Length];
        Magic.CopyTo(frame);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(Magic.Length), (ushort)body.Length);
        body.CopyTo(frame.AsSpan(HeaderLength));
        return frame;
    }

    /// <summary>Decodes one complete frame. Trailing bytes are rejected.</summary>
    public static ConnectionPreamble Decode(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < HeaderLength)
        {
            throw new InvalidDataException("The preamble frame is truncated.");
        }

        var length = ReadHeader(frame[..HeaderLength]);
        if (frame.Length != HeaderLength + length)
        {
            throw new InvalidDataException("The preamble frame length does not match its header.");
        }

        return ParseJson(frame[HeaderLength..].ToArray());
    }

    /// <summary>
    /// Reads exactly one frame from <paramref name="stream"/> and nothing more, leaving the
    /// raw stream that follows untouched. The caller enforces the read deadline (10 s on the
    /// host bridge) through <paramref name="cancellationToken"/>.
    /// </summary>
    public static async Task<ConnectionPreamble> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[HeaderLength];
        try
        {
            await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
            var body = new byte[ReadHeader(header)];
            await stream.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);
            return ParseJson(body);
        }
        catch (EndOfStreamException exception)
        {
            throw new InvalidDataException("The connection closed before a full preamble arrived.", exception);
        }
    }

    private static int ReadHeader(ReadOnlySpan<byte> header)
    {
        if (!header[..Magic.Length].SequenceEqual(Magic))
        {
            throw new InvalidDataException("The stream does not start with a 1Salem Connect preamble.");
        }

        int length = BinaryPrimitives.ReadUInt16BigEndian(header[Magic.Length..]);
        if (length is < 1 or > MaxJsonLength)
        {
            throw new InvalidDataException("The preamble length is outside 1..4096.");
        }

        return length;
    }

    private static ConnectionPreamble ParseJson(byte[] body) =>
        StrictJsonObject.TryParse(body, out var json) && ConnectionPreamble.TryCreate(json, out var preamble)
            ? preamble
            : throw new InvalidDataException("The preamble JSON is not a valid {t,n,ts,p} object.");
}
