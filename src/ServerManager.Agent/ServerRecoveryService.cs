using ServerManager.Contracts;
using ServerManager.Core;

namespace ServerManager.Agent;

public sealed class ServerRecoveryService(
    IGameServerStore serverStore,
    INetworkService networkService,
    GameServerOrchestrator orchestrator,
    PlayitStartupGate playitStartup,
    ILogger<ServerRecoveryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await playitStartup.Ready.WaitAsync(stoppingToken);
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            var servers = await serverStore.ListAsync(stoppingToken);
            foreach (var server in servers)
            {
                if (!ServerRecoveryPolicy.ShouldAutoStart(
                        server.AutoStart,
                        Directory.Exists(server.RootPath)))
                {
                    if (server.State is not ServerState.Stopped and
                        not ServerState.NotInstalled)
                    {
                        await serverStore.SetStateWithErrorAsync(
                            server.Id,
                            ServerRecoveryPolicy.ReconcileAfterAgentRestart(
                                server.State,
                                false),
                            server.State is ServerState.Running or ServerState.Starting
                                ? "The Agent restarted; the previous managed process is no longer attached."
                                : server.LastError,
                            stoppingToken);
                    }

                    continue;
                }

                await WaitForNetworkAsync(stoppingToken);
                try
                {
                    logger.LogInformation(
                        "Starting auto-start server {ServerId} ({Game}).",
                        server.Id,
                        server.Game);
                    await orchestrator.StartAsync(server.Id, stoppingToken);
                }
                catch (Exception exception)
                {
                    logger.LogError(
                        exception,
                        "Auto-start failed for server {ServerId}.",
                        server.Id);
                    await serverStore.SetStateWithErrorAsync(
                        server.Id,
                        ServerState.Error,
                        $"Auto-start failed: {exception.Message}",
                        stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task WaitForNetworkAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var snapshot = await networkService.GetSnapshotAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(snapshot.LocalIpv4))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        }

        logger.LogWarning(
            "No LAN IPv4 address was detected before auto-start; continuing with local server start.");
    }
}
