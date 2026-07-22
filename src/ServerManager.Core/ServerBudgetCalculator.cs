using ServerManager.Contracts;

namespace ServerManager.Core;

public static class ServerBudgetCalculator
{
    public const long StartupSafetyAllowanceBytes = 512L * 1024 * 1024;
    public const long DefaultPalworldStartBytes = 4L * 1024 * 1024 * 1024;

    public static ServerStartBudgetSnapshot Evaluate(
        GameServerDefinition target,
        ResourcePolicy policy,
        SystemResourceSnapshot system,
        IReadOnlyCollection<GameServerDefinition> registeredServers,
        IReadOnlyCollection<ProcessSnapshot> processSnapshots,
        DateTimeOffset? capturedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(registeredServers);
        ArgumentNullException.ThrowIfNull(processSnapshots);

        var registered = registeredServers.ToDictionary(server => server.Id);
        var active = processSnapshots
            .Where(snapshot =>
                snapshot.ServerId != target.Id &&
                snapshot.State == ServerState.Running &&
                snapshot.ExitCode is null &&
                snapshot.ProcessId > 0 &&
                snapshot.WorkingSetBytes > 0 &&
                registered.ContainsKey(snapshot.ServerId))
            .Select(snapshot => new ManagedServerBudgetItem(
                snapshot.ServerId,
                registered[snapshot.ServerId].Game,
                snapshot.State,
                snapshot.ProcessId,
                snapshot.WorkingSetBytes))
            .ToArray();
        var activeUsage = active.Sum(item => item.WorkingSetBytes);
        var estimatedStart = target.Game == GameType.Minecraft
            ? Math.Max(
                ResourcePolicyCatalog.Gibibyte,
                (target.MaximumMemoryMb ?? 4096) * 1024L * 1024)
            : DefaultPalworldStartBytes;
        var safeCapacity = Math.Max(
            0,
            system.TotalMemoryBytes -
            policy.WindowsReserveBytes -
            activeUsage -
            StartupSafetyAllowanceBytes);
        var budgetRemaining = Math.Max(
            0,
            policy.MaximumServerBudgetBytes - activeUsage);
        var allowance = Math.Min(safeCapacity, budgetRemaining);
        var expectedRemaining = Math.Max(
            0,
            system.AvailableMemoryBytes - estimatedStart);
        var safe = estimatedStart <= allowance;
        var reason = safe
            ? $"Start is within policy: estimated {GiB(estimatedStart):F2} GiB, " +
              $"allowance {GiB(allowance):F2} GiB, reserve " +
              $"{GiB(policy.WindowsReserveBytes):F2} GiB, active managed usage " +
              $"{GiB(activeUsage):F2} GiB."
            : $"Start blocked: estimated {GiB(estimatedStart):F2} GiB exceeds " +
              $"the {GiB(allowance):F2} GiB safe allowance. Total " +
              $"{GiB(system.TotalMemoryBytes):F2} GiB - reserve " +
              $"{GiB(policy.WindowsReserveBytes):F2} GiB - active managed usage " +
              $"{GiB(activeUsage):F2} GiB - startup allowance " +
              $"{GiB(StartupSafetyAllowanceBytes):F2} GiB; configured server budget " +
              $"{GiB(policy.MaximumServerBudgetBytes):F2} GiB.";

        return new ServerStartBudgetSnapshot(
            target.Id,
            target.Game,
            safe,
            reason,
            system.TotalMemoryBytes,
            system.AvailableMemoryBytes,
            policy.WindowsReserveBytes,
            activeUsage,
            StartupSafetyAllowanceBytes,
            policy.MaximumServerBudgetBytes,
            safeCapacity,
            budgetRemaining,
            estimatedStart,
            allowance,
            expectedRemaining,
            policy.AllowUnsafeStartupOverride,
            !safe && policy.AllowUnsafeStartupOverride,
            active,
            capturedAtUtc ?? DateTimeOffset.UtcNow);
    }

    private static double GiB(long bytes) =>
        bytes / (double)ResourcePolicyCatalog.Gibibyte;
}
