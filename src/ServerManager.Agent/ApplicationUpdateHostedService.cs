using ServerManager.Infrastructure.Updates;

namespace ServerManager.Agent;

public sealed class ApplicationUpdateHostedService(
    ApplicationUpdateCoordinator coordinator,
    ILogger<ApplicationUpdateHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await coordinator.InitializeAsync(stoppingToken);
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await coordinator.CheckAutomaticallyAsync(stoppingToken);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Automatic application update check failed.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromHours(6), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }
}
