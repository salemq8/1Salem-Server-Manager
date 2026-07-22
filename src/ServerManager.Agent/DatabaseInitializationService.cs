using ServerManager.Core;

namespace ServerManager.Agent;

public sealed class DatabaseInitializationService(
    IApplicationDatabase database,
    IAuditLogStore auditLog,
    AgentRuntimeState runtimeState,
    ILogger<DatabaseInitializationService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Initializing the Agent database.");
        await database.InitializeAsync(cancellationToken);
        runtimeState.MarkDatabaseReady();
        await auditLog.WriteAsync(
            "Agent",
            "AgentStarted",
            Environment.MachineName,
            true,
            cancellationToken: cancellationToken);
        logger.LogInformation("Agent database initialization completed.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

