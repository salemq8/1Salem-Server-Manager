using ServerManager.Core;

namespace ServerManager.Agent;

public sealed class ProcessMetricsService(
    IProcessSupervisor processSupervisor,
    ProcessMetricsCache cache,
    ILogger<ProcessMetricsService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            do
            {
                cache.Replace(
                    await processSupervisor.GetAllSnapshotsAsync(stoppingToken));
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogDebug("Process metrics sampling stopped.");
        }
    }
}
