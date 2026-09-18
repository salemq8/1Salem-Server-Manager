using ServerManager.Contracts;
using ServerManager.Core;

namespace ServerManager.Agent;

public sealed class BackupSchedulerService(
    IScheduleStore scheduleStore,
    IGameServerStore gameServerStore,
    IBackupService backupService,
    ISettingsStore settingsStore,
    ILogger<BackupSchedulerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            do
            {
                await RunDueSchedulesAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task RunDueSchedulesAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var due = await scheduleStore.GetDueAsync(now, cancellationToken);
        foreach (var schedule in due.Where(schedule =>
                     schedule.Kind.Equals("Backup", StringComparison.OrdinalIgnoreCase) &&
                     schedule.ServerId is not null))
        {
            try
            {
                var server = await gameServerStore.GetAsync(
                    schedule.ServerId!.Value,
                    cancellationToken);
                if (server is null)
                {
                    logger.LogWarning(
                        "Backup schedule {ScheduleId} references a missing server.",
                        schedule.Id);
                    continue;
                }

                var settingsKey = $"backup.center.{server.Id:N}";
                var settings = await settingsStore.GetAsync<BackupCenterSettings>(
                    settingsKey,
                    cancellationToken);
                var destination = settings?.DestinationRoot;
                if (string.IsNullOrWhiteSpace(destination))
                {
                    destination = BackupDestinationPolicy.GetDefaultRoot(server);
                }

                // SafeOffline is requested only for Minecraft; a running Palworld server backs
                // up via its own REST-based world save when available, falling back to a
                // safe-offline stop/backup/restart cycle inside BackupService itself only if
                // that REST save isn't available. Either way this call must succeed -- a
                // scheduled backup must never depend on an optional feature being enabled.
                var result = await backupService.CreateAsync(
                    new BackupRequest(
                        server.Id,
                        destination,
                        false,
                        server.Game == GameType.Minecraft,
                        IsScheduled: true),
                    cancellationToken);
                if (settings is not null)
                {
                    var retention = new BackupRetentionSettings(
                        settings.MaximumCount,
                        TimeSpan.FromDays(settings.MaximumAgeDays),
                        settings.MaximumTotalBytes,
                        settings.KeepDaily,
                        settings.KeepWeekly);
                    var backups = await backupService.ListAsync(
                        server.Id,
                        cancellationToken);
                    foreach (var expired in BackupRetentionPolicy.SelectForDeletion(
                                 backups,
                                 retention,
                                 now))
                    {
                        _ = await backupService.DeleteAsync(
                            expired.Id,
                            cancellationToken);
                    }

                    await settingsStore.SetAsync(
                        settingsKey,
                        settings with
                        {
                            LastRunAtUtc = now,
                            NextRunAtUtc = ScheduleExpression.GetNextRun(
                                schedule.CronExpression,
                                now),
                            LastSuccessfulWorldSaveAtUtc =
                                server.Game == GameType.Palworld
                                    ? now
                                    : settings.LastSuccessfulWorldSaveAtUtc,
                            LastRunStatusMessage = result.StatusMessage
                        },
                        cancellationToken);
                }
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "Scheduled backup {ScheduleId} failed.",
                    schedule.Id);

                // A failed scheduled backup must be just as visible as a successful one --
                // silently leaving LastRunAtUtc stale is how this went unnoticed before.
                var settingsKey = $"backup.center.{schedule.ServerId!.Value:N}";
                var settings = await settingsStore.GetAsync<BackupCenterSettings>(
                    settingsKey,
                    cancellationToken);
                if (settings is not null)
                {
                    await settingsStore.SetAsync(
                        settingsKey,
                        settings with
                        {
                            LastRunAtUtc = now,
                            NextRunAtUtc = ScheduleExpression.GetNextRun(
                                schedule.CronExpression,
                                now),
                            LastRunStatusMessage = $"Backup failed: {exception.Message}"
                        },
                        cancellationToken);
                }
            }
            finally
            {
                await scheduleStore.MarkRunAsync(
                    schedule.Id,
                    now,
                    ScheduleExpression.GetNextRun(schedule.CronExpression, now),
                    cancellationToken);
            }
        }
    }
}
