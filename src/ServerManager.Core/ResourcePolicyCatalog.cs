using System.Diagnostics;
using ServerManager.Contracts;

namespace ServerManager.Core;

public static class ResourcePolicyCatalog
{
    public const long Gibibyte = 1024L * 1024 * 1024;
    public const long DisplayRoundingToleranceBytes = Gibibyte / 20;

    public static ResourcePolicy Create(
        ResourceMode mode,
        long totalMemoryBytes,
        long? windowsReserveBytes = null,
        long? maximumServerBudgetBytes = null,
        long? cpuAffinityMask = null,
        bool stopLowerPriorityGame = false,
        ProcessPriorityClass? minecraftPriority = null,
        ProcessPriorityClass? palworldPriority = null,
        long? palworldWarningThresholdBytes = null,
        long? palworldCriticalThresholdBytes = null,
        bool criticalNotificationEnabled = false,
        bool autoSaveAndRestart = false,
        long? hardMemoryLimitBytes = null,
        bool restoreBalancedOnServerStop = false,
        bool allowUnsafeStartupOverride = false)
    {
        if (totalMemoryBytes < 2 * Gibibyte)
        {
            throw new ArgumentOutOfRangeException(
                nameof(totalMemoryBytes),
                "At least 2 GiB of total memory must be reported.");
        }

        var reserve = windowsReserveBytes ?? mode switch
        {
            ResourceMode.Safe =>
                Math.Max(4 * Gibibyte, totalMemoryBytes / 4),
            ResourceMode.Performance =>
                Math.Max(2 * Gibibyte, totalMemoryBytes * 15 / 100),
            ResourceMode.OneGameAtATime =>
                Math.Max(3 * Gibibyte, totalMemoryBytes / 5),
            _ => Math.Max(2 * Gibibyte, totalMemoryBytes / 5)
        };
        var budget = maximumServerBudgetBytes ??
            Math.Max(Gibibyte, totalMemoryBytes - reserve);
        if (reserve + budget > totalMemoryBytes &&
            reserve + budget <=
            totalMemoryBytes + DisplayRoundingToleranceBytes)
        {
            budget = Math.Max(1, totalMemoryBytes - reserve);
        }
        var priorities = mode switch
        {
            ResourceMode.Balanced =>
                (ProcessPriorityClass.Normal, ProcessPriorityClass.Normal),
            ResourceMode.MinecraftPriority =>
                (ProcessPriorityClass.AboveNormal, ProcessPriorityClass.BelowNormal),
            ResourceMode.PalworldPriority =>
                (ProcessPriorityClass.BelowNormal, ProcessPriorityClass.AboveNormal),
            ResourceMode.OneGameAtATime =>
                (ProcessPriorityClass.Normal, ProcessPriorityClass.Normal),
            ResourceMode.Safe =>
                (ProcessPriorityClass.Normal, ProcessPriorityClass.Normal),
            ResourceMode.Performance =>
                (ProcessPriorityClass.Normal, ProcessPriorityClass.AboveNormal),
            ResourceMode.Custom =>
                (ProcessPriorityClass.Normal, ProcessPriorityClass.Normal),
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
        if (mode == ResourceMode.Custom)
        {
            priorities = (
                minecraftPriority ?? ProcessPriorityClass.Normal,
                palworldPriority ?? ProcessPriorityClass.Normal);
        }

        var warning = palworldWarningThresholdBytes ??
                      Math.Max(Gibibyte, budget * 3 / 4);
        var critical = palworldCriticalThresholdBytes ??
                       Math.Max(warning, budget * 9 / 10);
        var policy = new ResourcePolicy(
            mode,
            priorities.Item1,
            priorities.Item2,
            reserve,
            budget,
            cpuAffinityMask,
            stopLowerPriorityGame,
            warning,
            critical,
            criticalNotificationEnabled,
            autoSaveAndRestart,
            hardMemoryLimitBytes,
            restoreBalancedOnServerStop,
            allowUnsafeStartupOverride);
        Validate(policy, totalMemoryBytes);
        return policy;
    }

    public static void Validate(ResourcePolicy policy, long totalMemoryBytes)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.MinecraftPriority == ProcessPriorityClass.RealTime ||
            policy.PalworldPriority == ProcessPriorityClass.RealTime)
        {
            throw new ArgumentException(
                "Realtime process priority is never permitted.",
                nameof(policy));
        }

        if (policy.WindowsReserveBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(policy),
                "The Windows memory reserve must be greater than zero.");
        }

        if (policy.MaximumServerBudgetBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(policy),
                "The server memory budget must be greater than zero.");
        }

        if (policy.WindowsReserveBytes + policy.MaximumServerBudgetBytes >
            totalMemoryBytes + DisplayRoundingToleranceBytes)
        {
            throw new ArgumentException(
                "The Windows reserve and server budget cannot exceed installed memory.",
                nameof(policy));
        }

        if (policy.CpuAffinityMask is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(policy),
                "A CPU affinity mask must select at least one processor.");
        }

        if (policy.PalworldWarningThresholdBytes is <= 0 ||
            policy.PalworldWarningThresholdBytes >
            policy.MaximumServerBudgetBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(policy),
                "The Palworld warning threshold must be greater than zero and cannot exceed the total server budget.");
        }

        if (policy.PalworldCriticalThresholdBytes is <= 0 ||
            policy.PalworldCriticalThresholdBytes >
            policy.MaximumServerBudgetBytes ||
            policy.PalworldCriticalThresholdBytes <
            policy.PalworldWarningThresholdBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(policy),
                "The critical threshold must be at least the warning threshold and cannot exceed the total server budget.");
        }

        if (policy.HardMemoryLimitBytes is not null &&
            (policy.HardMemoryLimitBytes < Gibibyte ||
             policy.HardMemoryLimitBytes > policy.MaximumServerBudgetBytes))
        {
            throw new ArgumentOutOfRangeException(
                nameof(policy),
                "The hard memory limit must be at least 1 GiB and cannot exceed the total server budget.");
        }
    }
}
