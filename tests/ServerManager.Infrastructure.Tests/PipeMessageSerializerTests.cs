using ServerManager.Contracts;
using ServerManager.Infrastructure.Transport;

namespace ServerManager.Infrastructure.Tests;

public sealed class PipeMessageSerializerTests
{
    [Fact]
    public async Task WriteAndReadAsync_RoundTripsRequest()
    {
        var expected = new PipeRequest("abc123", NamedPipeOperations.GetStatus);
        await using var stream = new MemoryStream();

        await PipeMessageSerializer.WriteAsync(stream, expected);
        stream.Position = 0;
        var actual = await PipeMessageSerializer.ReadAsync<PipeRequest>(stream);

        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Operation, actual.Operation);
    }

    [Fact]
    public async Task ReadAsync_RejectsOversizedMessageBeforeAllocation()
    {
        var bytes = BitConverter.GetBytes(AgentTransportDefaults.MaximumPipeMessageBytes + 1);
        await using var stream = new MemoryStream(bytes);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => PipeMessageSerializer.ReadAsync<PipeRequest>(stream));
    }
}

