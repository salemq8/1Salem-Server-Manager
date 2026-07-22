using System.IO.Pipes;
using System.Text.Json;
using ServerManager.Contracts;

namespace ServerManager.Infrastructure.Transport;

public sealed class NamedPipeAgentClient
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly string _pipeName;
    private readonly TimeSpan _connectTimeout;

    public NamedPipeAgentClient()
        : this(AgentTransportDefaults.ResolvePipeName())
    {
    }

    public NamedPipeAgentClient(
        string pipeName,
        TimeSpan? connectTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        _pipeName = pipeName;
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(2);
    }

    public async Task<PipeResponse> SendAsync(
        PipeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        await using var pipe = new NamedPipeClientStream(
            ".",
            _pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_connectTimeout);
        await pipe.ConnectAsync(timeout.Token);
        await PipeMessageSerializer.WriteAsync(pipe, request, timeout.Token);
        return await PipeMessageSerializer.ReadAsync<PipeResponse>(pipe, timeout.Token);
    }

    public async Task<AgentStatusResponse> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(
            new PipeRequest(Guid.NewGuid().ToString("N"), NamedPipeOperations.GetStatus),
            cancellationToken);

        if (!response.Success || response.Payload is null)
        {
            throw new InvalidOperationException(response.Error?.WhatFailed ?? "The Agent did not return a status.");
        }

        return response.Payload.Value.Deserialize<AgentStatusResponse>(SerializerOptions)
            ?? throw new InvalidDataException("The Agent returned an invalid status payload.");
    }
}
