using Microsoft.Extensions.Hosting;
using ServerManager.Contracts;
using ServerManager.Core;
using ServerManager.Infrastructure.Games.Palworld;

namespace ServerManager.Agent;

public sealed class ResourceGovernorService(
    ISystemResourceReader resourceReader,
    IProcessSupervisor processSupervisor,
    IProcessResourceController processResourceController,
    IGameServerStore serverStore,
    ISettingsStore settingsStore,
    GameServerOrchestrator orchestrator,
    PalworldRestClient palworldRestClient,
    IAuditLogStore auditLogStore,
    ILogger<ResourceGovernorService> logger) :
    BackgroundService,
    IResourceGovernor
{
    private const string SettingsKey = "resources.activePolicy";
    private readonly object _sync = new();
    private ResourcePolicy _activePolicy = ResourcePolicyCatalog.Create(
        ResourceMode.Balanced,
        8 * ResourcePolicyCatalog.Gibibyte);
    private SystemResourceSnapshot? _latest;
    private DateTimeOffset? _lastCriticalRestartUtc;

    public ResourcePolicy ActivePolicy
    {
        get
        {
            lock (_sync)
            {
                return _activePolicy;
            }
        }
    }

    public async Task ApplyAsync(
        ResourcePolicy policy,
        CancellationToken cancellationToken = default)
    {
        var system = resourceReader.Capture(ActivePolicy);
        ResourcePolicyCatalog.Validate(policy, system.TotalMemoryBytes);
        lock (_sync)
        {
            _activePolicy = policy;
        }

        await settingsStore.SetAsync(SettingsKey, policy, cancellationToken);
        var failures = await ApplyToRunningServersAsync(
            policy,
            cancellationToken);
        if (failures.Count > 0)
        {
            throw new ResourcePolicyApplyException(failures);
        }

        var registeredGames = (await serverStore.ListAsync(cancellationToken))
            .Select(server => server.Game);
        await auditLogStore.WriteAsync(
            "ResourceGovernor",
            "ResourcePolicyApplied",
            policy.Mode.ToString(),
            true,
            ResourceProfileSummary.Build(policy, registeredGames),
            cancellationToken);
        CaptureLatest();
    }

    public Task<SystemResourceSnapshot> GetSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(CaptureLatest());
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await LoadPolicyAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _ = await ApplyToRunningServersAsync(
                    ActivePolicy,
                    stoppingToken);
                await HandleCriticalPalworldMemoryAsync(stoppingToken);
                await RestoreBalancedIfRequiredAsync(stoppingToken);
                CaptureLatest();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Resource governor sampling failed.");
            }

            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
        }
    }

    private async Task LoadPolicyAsync(CancellationToken cancellationToken)
    {
        var saved = await settingsStore.GetAsync<ResourcePolicy>(SettingsKey, cancellationToken);
        if (saved is null)
        {
            return;
        }

        try
        {
            ResourcePolicyCatalog.Validate(
                saved,
                resourceReader.Capture(ActivePolicy).TotalMemoryBytes);
            lock (_sync)
            {
                _activePolicy = saved;
            }
        }
        catch (ArgumentException exception)
        {
            logger.LogWarning(exception, "Saved resource policy was invalid; using Balanced.");
        }
    }

    private async Task<IReadOnlyList<OperationResult>> ApplyToRunningServersAsync(
        ResourcePolicy policy,
        CancellationToken cancellationToken)
    {
        var failures = new List<OperationResult>();
        var servers = (await serverStore.ListAsync(cancellationToken))
            .ToDictionary(server => server.Id);
        var snapshots = await processSupervisor.GetAllSnapshotsAsync(cancellationToken);
        foreach (var snapshot in snapshots.Where(item => item.State == ServerState.Running))
        {
            if (!servers.TryGetValue(snapshot.ServerId, out var server))
            {
                continue;
            }

            var priority = server.Game == GameType.Minecraft
                ? policy.MinecraftPriority
                : policy.PalworldPriority;
            var result = await processResourceController.ApplyResourcesAsync(
                server.Id,
                priority,
                policy.CpuAffinityMask,
                cancellationToken,
                server.Game == GameType.Palworld
                    ? policy.HardMemoryLimitBytes
                    : null);
            if (!result.Success)
            {
                failures.Add(result);
                logger.LogDebug(
                    "Resource policy was not applied to {ServerId}: {Message}",
                    server.Id,
                    result.Message);
            }
        }

        if (!policy.StopLowerPriorityGame)
        {
            return failures;
        }

        var lowerGame = policy.Mode switch
        {
            ResourceMode.MinecraftPriority => GameType.Palworld,
            ResourceMode.PalworldPriority => GameType.Minecraft,
            _ => (GameType?)null
        };
        if (lowerGame is null)
        {
            return failures;
        }

        foreach (var snapshot in snapshots.Where(item => item.State == ServerState.Running))
        {
            if (servers.TryGetValue(snapshot.ServerId, out var server) &&
                server.Game == lowerGame)
            {
                await orchestrator.StopAsync(server.Id, false, cancellationToken);
            }
        }

        return failures;
    }

    private SystemResourceSnapshot CaptureLatest()
    {
        var snapshot = resourceReader.Capture(ActivePolicy);
        var processSnapshots = processSupervisor.GetAllSnapshotsAsync().GetAwaiter().GetResult();
        var warnings = snapshot.Warnings.ToList();
        var servers = serverStore.ListAsync().GetAwaiter().GetResult()
            .ToDictionary(server => server.Id);
        var activeSnapshots = processSnapshots
            .Where(item =>
                item.State == ServerState.Running &&
                item.ExitCode is null &&
                item.ProcessId > 0 &&
                servers.ContainsKey(item.ServerId))
            .ToArray();
        if (activeSnapshots.Sum(item => item.WorkingSetBytes) >
            snapshot.ActivePolicy.MaximumServerBudgetBytes)
        {
            warnings.Add("Combined server memory usage exceeds the configured budget.");
        }

        if (snapshot.ActivePolicy.Mode == ResourceMode.OneGameAtATime &&
            activeSnapshots.Length > 1)
        {
            warnings.Add("One Game at a Time is active, but multiple servers are running.");
        }

        var palworldMemory = activeSnapshots
            .Where(snapshot =>
                servers.TryGetValue(snapshot.ServerId, out var server) &&
                server.Game == GameType.Palworld)
            .Sum(snapshot => snapshot.WorkingSetBytes);
        if (snapshot.ActivePolicy.PalworldWarningThresholdBytes is { } warning &&
            palworldMemory >= warning)
        {
            warnings.Add(
                $"Palworld process-tree memory exceeded the warning threshold ({palworldMemory / (double)ResourcePolicyCatalog.Gibibyte:F1} GiB).");
        }

        snapshot = snapshot with { Warnings = warnings };
        lock (_sync)
        {
            _latest = snapshot;
            return _latest;
        }
    }

    private async Task HandleCriticalPalworldMemoryAsync(
        CancellationToken cancellationToken)
    {
        var policy = ActivePolicy;
        if (!policy.AutoSaveAndRestart ||
            policy.PalworldCriticalThresholdBytes is not { } threshold ||
            _lastCriticalRestartUtc is { } last &&
            DateTimeOffset.UtcNow - last < TimeSpan.FromMinutes(15))
        {
            return;
        }

        var servers = (await serverStore.ListAsync(cancellationToken))
            .ToDictionary(server => server.Id);
        var snapshots = await processSupervisor.GetAllSnapshotsAsync(
            cancellationToken);
        foreach (var process in snapshots.Where(snapshot =>
                     snapshot.State == ServerState.Running &&
                     snapshot.WorkingSetBytes >= threshold &&
                     servers.TryGetValue(snapshot.ServerId, out var server) &&
                     server.Game == GameType.Palworld))
        {
            var server = servers[process.ServerId];
            var saved = await palworldRestClient.SaveWorldAsync(
                server,
                cancellationToken);
            if (!saved.Success)
            {
                await auditLogStore.WriteAsync(
                    "ResourceGovernor",
                    "PalworldCriticalMemoryRestart",
                    server.Id.ToString(),
                    false,
                    $"World save failed; restart cancelled. {saved.Code}: {saved.Message}",
                    cancellationToken);
                continue;
            }

            await orchestrator.RestartAsync(server.Id, cancellationToken);
            _lastCriticalRestartUtc = DateTimeOffset.UtcNow;
            await auditLogStore.WriteAsync(
                "ResourceGovernor",
                "PalworldCriticalMemoryRestart",
                server.Id.ToString(),
                true,
                $"WorkingSet={process.WorkingSetBytes}; Threshold={threshold}; WorldSaved=True",
                cancellationToken);
        }
    }

    private async Task RestoreBalancedIfRequiredAsync(
        CancellationToken cancellationToken)
    {
        var policy = ActivePolicy;
        if (!policy.RestoreBalancedOnServerStop ||
            policy.Mode is ResourceMode.Balanced or
                ResourceMode.Safe or
                ResourceMode.OneGameAtATime)
        {
            return;
        }

        var servers = (await serverStore.ListAsync(cancellationToken))
            .ToDictionary(server => server.Id);
        var running = await processSupervisor.GetAllSnapshotsAsync(
            cancellationToken);
        var prioritizedGame = policy.Mode == ResourceMode.MinecraftPriority
            ? GameType.Minecraft
            : GameType.Palworld;
        if (running.Any(snapshot =>
                snapshot.State == ServerState.Running &&
                servers.TryGetValue(snapshot.ServerId, out var server) &&
                server.Game == prioritizedGame))
        {
            return;
        }

        var total = resourceReader.Capture(policy).TotalMemoryBytes;
        var balanced = ResourcePolicyCatalog.Create(
            ResourceMode.Balanced,
            total,
            policy.WindowsReserveBytes,
            policy.MaximumServerBudgetBytes,
            policy.CpuAffinityMask,
            false,
            palworldWarningThresholdBytes:
                policy.PalworldWarningThresholdBytes,
            palworldCriticalThresholdBytes:
                policy.PalworldCriticalThresholdBytes,
            criticalNotificationEnabled:
                policy.CriticalNotificationEnabled,
            autoSaveAndRestart:
                policy.AutoSaveAndRestart,
            hardMemoryLimitBytes:
                policy.HardMemoryLimitBytes,
            restoreBalancedOnServerStop: true,
            allowUnsafeStartupOverride:
                policy.AllowUnsafeStartupOverride);
        lock (_sync)
        {
            _activePolicy = balanced;
        }

        await settingsStore.SetAsync(SettingsKey, balanced, cancellationToken);
    }

    public sealed class ResourcePolicyApplyException(
        IReadOnlyList<OperationResult> failures) :
        Exception(string.Join(
            " ",
            failures.Select(failure =>
                $"{failure.ErrorCode}: {failure.Message}")))
    {
        public IReadOnlyList<OperationResult> Failures { get; } = failures;
    }
}
