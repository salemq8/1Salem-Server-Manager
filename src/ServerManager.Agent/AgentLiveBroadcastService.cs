using Microsoft.AspNetCore.SignalR;
using ServerManager.Infrastructure.Processes;

namespace ServerManager.Agent;

public sealed class AgentLiveBroadcastService(
    IHubContext<AgentHub> hubContext,
    ProcessMetricsCache metrics,
    ProcessSupervisor processSupervisor,
    ILogger<AgentLiveBroadcastService> logger) : BackgroundService
{
    private readonly Dictionary<Guid, DateTimeOffset> _lastLogTimestamps = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var snapshots = metrics.Snapshot();
                await hubContext.Clients.All.SendAsync(
                    "ProcessStatus",
                    snapshots,
                    stoppingToken);

                foreach (var snapshot in snapshots)
                {
                    var lastTimestamp = _lastLogTimestamps.GetValueOrDefault(
                        snapshot.ServerId,
                        DateTimeOffset.MinValue);
                    var entries = processSupervisor.GetRecentLogs(snapshot.ServerId)
                        .Where(entry => entry.TimestampUtc > lastTimestamp)
                        .TakeLast(100)
                        .Select(AgentHub.Redact)
                        .ToArray();
                    if (entries.Length == 0)
                    {
                        continue;
                    }

                    _lastLogTimestamps[snapshot.ServerId] = entries[^1].TimestampUtc;
                    await hubContext.Clients.Group(AgentHub.GroupName(snapshot.ServerId))
                        .SendAsync("LogBatch", entries, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Live status broadcast failed.");
            }

            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }
}
