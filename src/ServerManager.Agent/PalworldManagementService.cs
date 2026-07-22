using System.Collections.Concurrent;
using ServerManager.Contracts;
using ServerManager.Core;
using ServerManager.Infrastructure.Games.Palworld;

namespace ServerManager.Agent;

public sealed class PalworldManagementCache
{
    private readonly ConcurrentDictionary<Guid, PalworldManagementSnapshot> _items =
        new();

    public PalworldManagementSnapshot? Get(Guid serverId) =>
        _items.TryGetValue(serverId, out var snapshot) ? snapshot : null;

    public void Set(PalworldManagementSnapshot snapshot) =>
        _items[snapshot.ServerId] = snapshot;
}

public sealed class PalworldManagementPollingService(
    IGameServerStore serverStore,
    IProcessSupervisor processSupervisor,
    PalworldRestClient restClient,
    PalworldManagementCache cache,
    ILogger<PalworldManagementPollingService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private readonly Dictionary<Guid, FailureState> _failures = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollAllAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "The Palworld management polling pass could not complete.");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    private async Task PollAllAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var servers = await serverStore.ListAsync(cancellationToken);
        var processes = (await processSupervisor.GetAllSnapshotsAsync(cancellationToken))
            .ToDictionary(snapshot => snapshot.ServerId);
        foreach (var server in servers.Where(item => item.Game == GameType.Palworld))
        {
            if (!processes.TryGetValue(server.Id, out var process) ||
                process.State != ServerState.Running)
            {
                _failures.Remove(server.Id);
                var configured = cache.Get(server.Id) ??
                    await restClient.GetSnapshotAsync(server, cancellationToken);
                cache.Set(!configured.RestApiEnabled
                    ? configured with
                    {
                        StatusMessage =
                            "Palworld management is disabled. Start the server after enabling it.",
                        CapturedAtUtc = now
                    }
                    : PalworldRestClient.Unavailable(
                        server.Id,
                        configured.RestApiPort,
                        "Start the Palworld server to load live management data."));
                continue;
            }

            if (_failures.TryGetValue(server.Id, out var failure) &&
                failure.NextAttemptUtc > now)
            {
                continue;
            }

            var snapshot = await restClient.GetSnapshotAsync(server, cancellationToken);
            if (snapshot.State == PalworldManagementState.Unavailable)
            {
                var count = (_failures.TryGetValue(server.Id, out failure)
                    ? failure.Count
                    : 0) + 1;
                var delaySeconds = Math.Min(30, 5 * (1 << Math.Min(3, count - 1)));
                _failures[server.Id] = new FailureState(
                    count,
                    now.AddSeconds(delaySeconds));
                snapshot = snapshot with
                {
                    ConsecutiveFailures = count,
                    StatusMessage =
                        $"{snapshot.StatusMessage} Retrying in {delaySeconds} seconds."
                };
            }
            else
            {
                _failures.Remove(server.Id);
            }

            cache.Set(snapshot);
        }
    }

    private sealed record FailureState(
        int Count,
        DateTimeOffset NextAttemptUtc);
}
