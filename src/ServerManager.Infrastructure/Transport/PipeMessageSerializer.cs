using System.Buffers.Binary;
using System.Text.Json;
using ServerManager.Contracts;

namespace ServerManager.Infrastructure.Transport;

public static class PipeMessageSerializer
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static async Task WriteAsync<T>(
        Stream stream,
        T message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var payload = JsonSerializer.SerializeToUtf8Bytes(message, SerializerOptions);
        if (payload.Length is 0 or > AgentTransportDefaults.MaximumPipeMessageBytes)
        {
            throw new InvalidDataException($"Pipe message length {payload.Length} is outside the allowed range.");
        }

        var length = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, payload.Length);
        await stream.WriteAsync(length, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async Task<T> ReadAsync<T>(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var lengthBuffer = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(lengthBuffer, cancellationToken);
        var length = BinaryPrimitives.ReadInt32LittleEndian(lengthBuffer);
        if (length is <= 0 or > AgentTransportDefaults.MaximumPipeMessageBytes)
        {
            throw new InvalidDataException($"Pipe message length {length} is outside the allowed range.");
        }

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        return JsonSerializer.Deserialize<T>(payload, SerializerOptions)
            ?? throw new InvalidDataException("The pipe message did not contain a valid payload.");
    }
}

