using ServerManager.Infrastructure.Connect;

namespace ServerManager.Agent;

/// <summary>Starts the one owner-side Connect host after database initialization.</summary>
public sealed class ConnectHostService(ConnectHost host, ILogger<ConnectHostService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await host.StartAsync(stoppingToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError("1Salem Connect host service stopped: {Error}", exception.Message);
        }
        finally
        {
            await host.StopAsync();
        }
    }
}
