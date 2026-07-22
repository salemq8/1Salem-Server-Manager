using System.Text.Json;
using ServerManager.Agent;
using ServerManager.Contracts;

namespace ServerManager.Agent.IntegrationTests;

public sealed class NamedPipeRequestDispatcherTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly AgentOptions _options = new(
        Path.GetTempPath(),
        AgentTransportDefaults.PipeName,
        AgentTransportDefaults.LoopbackApiUrl);

    [Fact]
    public void Dispatch_StatusReturnsLocalAgentMetadata()
    {
        var runtime = new AgentRuntimeState();
        runtime.MarkDatabaseReady();
        var dispatcher = new NamedPipeRequestDispatcher(runtime, _options);

        var response = dispatcher.Dispatch(
            new PipeRequest("status-1", NamedPipeOperations.GetStatus));

        Assert.True(response.Success);
        Assert.Null(response.Error);
        var status = response.Payload!.Value.Deserialize<AgentStatusResponse>(SerializerOptions);
        Assert.NotNull(status);
        Assert.Equal(Environment.MachineName, status.MachineName);
        Assert.True(status.DatabaseReady);
        Assert.Equal("NamedPipe", status.Transport);
    }

    [Fact]
    public void Dispatch_PingReturnsHealthyResponse()
    {
        var dispatcher = new NamedPipeRequestDispatcher(new AgentRuntimeState(), _options);

        var response = dispatcher.Dispatch(
            new PipeRequest("ping-1", NamedPipeOperations.Ping));

        Assert.True(response.Success);
        Assert.Equal("Healthy", response.Payload!.Value.GetProperty("status").GetString());
    }

    [Fact]
    public void Dispatch_RejectsUnknownOperation()
    {
        var dispatcher = new NamedPipeRequestDispatcher(new AgentRuntimeState(), _options);

        var response = dispatcher.Dispatch(new PipeRequest("bad-1", "shell.execute"));

        Assert.False(response.Success);
        Assert.Equal("OperationNotAllowed", response.Error?.Code);
    }
}
