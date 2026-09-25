using System.Text.Json;
using ServerManager.Connect.Core.Json;

namespace ServerManager.Connect.Core.Pipes;

/// <summary>
/// Pipe framing (contract §11): one UTF-8 JSON object per line, terminated by <c>\n</c>, at most
/// <see cref="MaxLineBytes"/> bytes before the newline.
/// </summary>
public static class JsonLines
{
    public const int MaxLineBytes = 64 * 1024;

    private static readonly JsonDocumentOptions Options = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 16
    };

    /// <summary>
    /// Writes one message and flushes it. The bytes must be a single JSON object without raw
    /// line breaks (compact <see cref="Utf8JsonWriter"/> or <see cref="JsonSerializer"/> output
    /// qualifies; both escape newlines inside strings). The line is written in one call, but a
    /// stream shared by several writers still needs the caller to serialize writes.
    /// </summary>
    public static async Task WriteAsync(Stream stream, ReadOnlyMemory<byte> utf8Json, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (utf8Json.Length is 0 or > MaxLineBytes)
        {
            throw new ArgumentException("A pipe message must be 1 to 65536 bytes.", nameof(utf8Json));
        }

        if (utf8Json.Span.IndexOfAny((byte)'\n', (byte)'\r') >= 0 || !TryParseObject(utf8Json, out _))
        {
            throw new ArgumentException("A pipe message must be one JSON object on a single line.", nameof(utf8Json));
        }

        var line = new byte[utf8Json.Length + 1];
        utf8Json.CopyTo(line);
        line[^1] = (byte)'\n';
        await stream.WriteAsync(line, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Parses one line: a JSON object with no duplicate top-level member names, the same rule
    /// the ticket and preamble parsers apply, so <c>"op"</c> twice cannot mean two things.
    /// </summary>
    internal static bool TryParseObject(ReadOnlyMemory<byte> utf8Json, out JsonElement value)
    {
        value = default;
        try
        {
            using var document = JsonDocument.Parse(utf8Json, Options);
            if (!StrictJsonObject.TryCreate(document.RootElement, out _))
            {
                return false;
            }

            value = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            // Invalid UTF-8 inside a string.
            return false;
        }
    }
}

/// <summary>
/// Reads JSON lines from one pipe connection. It buffers at most one line plus its newline, so
/// a peer that never sends <c>\n</c> costs 64 KiB and then an <see cref="InvalidDataException"/>,
/// not unbounded memory. One reader per connection; not thread-safe.
/// </summary>
public sealed class JsonLineReader
{
    private readonly Stream _stream;
    private readonly byte[] _buffer = new byte[JsonLines.MaxLineBytes + 1];
    private int _start;
    private int _end;

    public JsonLineReader(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    }

    /// <summary>
    /// The next message, or null when the peer closed the connection cleanly between messages.
    /// Throws <see cref="InvalidDataException"/> for a line over 64 KiB, a line that is not one
    /// JSON object, or a connection closed in the middle of a line. After that the connection
    /// should be dropped: the framing can no longer be trusted.
    /// </summary>
    public async Task<JsonElement?> ReadAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var newline = Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start);
            if (newline >= 0)
            {
                var line = _buffer.AsMemory(_start, newline - _start);
                _start = newline + 1;
                return JsonLines.TryParseObject(line, out var message)
                    ? message
                    : throw new InvalidDataException("A pipe message is not a single JSON object.");
            }

            if (_end - _start > JsonLines.MaxLineBytes)
            {
                throw new InvalidDataException("A pipe message exceeds 64 KiB.");
            }

            if (_start > 0)
            {
                Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
                _end -= _start;
                _start = 0;
            }

            var read = await _stream.ReadAsync(_buffer.AsMemory(_end), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return _end == 0
                    ? null
                    : throw new InvalidDataException("The pipe closed in the middle of a message.");
            }

            _end += read;
        }
    }
}
