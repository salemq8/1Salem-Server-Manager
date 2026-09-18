using ServerManager.Core;

namespace ServerManager.Agent;

public sealed class DatabaseInitializationService(
    IApplicationDatabase database,
    IAuditLogStore auditLog,
    IBackupService backupService,
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

        // Must run before any other hosted service adopts, starts, or reports on a managed
        // server, so a restore interrupted by a prior crash or forced shutdown is repaired
        // before anything else observes the affected server's files.
        var recovered = await backupService.RecoverInterruptedRestoresAsync(cancellationToken);
        if (recovered > 0)
        {
            logger.LogWarning(
                "Recovered {Count} interrupted backup restore(s) left behind by a prior crash " +
                "or forced shutdown.",
                recovered);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

