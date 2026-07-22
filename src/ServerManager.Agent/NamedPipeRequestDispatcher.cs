using ServerManager.Contracts;

namespace ServerManager.Agent;

public sealed class NamedPipeRequestDispatcher(
    AgentRuntimeState runtimeState,
    AgentOptions options)
{
    public PipeResponse Dispatch(PipeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.Operation switch
        {
            NamedPipeOperations.Ping => PipeResponse.Ok(
                request.Id,
                new HealthResponse("Healthy", runtimeState.Version, DateTimeOffset.UtcNow)),
            NamedPipeOperations.GetStatus => PipeResponse.Ok(
                request.Id,
                runtimeState.ToStatus(options)),
            _ => PipeResponse.Fail(
                request.Id,
                new ApiErrorResponse(
                    "OperationNotAllowed",
                    "The requested local Agent operation is not available.",
                    "The local named-pipe transport exposes only ping and status operations.",
                    request.Operation,
                    "Use a supported dashboard action or update the Client and Agent together.",
                    false))
        };
    }
}
