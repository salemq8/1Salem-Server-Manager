using System.Diagnostics;

namespace ServerManager.Contracts;

public sealed record ResourceProfileRequest(
    ResourceMode Mode,
    long? WindowsReserveBytes = null,
    long? MaximumServerBudgetBytes = null,
    long? CpuAffinityMask = null,
    bool StopLowerPriorityGame = false,
    ProcessPriorityClass? MinecraftPriority = null,
    ProcessPriorityClass? PalworldPriority = null,
    long? PalworldWarningThresholdBytes = null,
    long? PalworldCriticalThresholdBytes = null,
    bool CriticalNotificationEnabled = false,
    bool AutoSaveAndRestart = false,
    long? HardMemoryLimitBytes = null,
    string? HardLimitConfirmation = null,
    bool OverrideUnsafeBudget = false,
    bool RestoreBalancedOnServerStop = false,
    bool AllowUnsafeStartupOverride = false);

public enum ResourceProfileApplyState
{
    Applying = 1,
    Active = 2,
    Failed = 3,
    PermissionDenied = 4,
    ServerNotRunning = 5
}

public sealed record ResourceProcessVerification(
    Guid ServerId,
    GameType Game,
    bool IsRunning,
    int? RootProcessId,
    int? GameProcessId,
    ProcessPriorityClass? ActualPriority,
    long? ActualCpuAffinityMask,
    bool Verified,
    string Message);

public sealed record ResourceProfileApplyResponse(
    bool Success,
    ResourceProfileApplyState State,
    string Message,
    ResourceMode ActiveMode,
    IReadOnlyList<ResourceProcessVerification> Processes,
    DateTimeOffset AppliedAtUtc);

public sealed record MemoryPerformancePolicySnapshot(
    long TotalSystemMemoryBytes,
    long UsedSystemMemoryBytes,
    long AvailableSystemMemoryBytes,
    long WindowsReserveBytes,
    long AgentMemoryBytes,
    long PalworldWorkingSetBytes,
    long PalworldPrivateMemoryBytes,
    long PalworldPeakMemoryBytes,
    long MinecraftConfiguredMemoryBytes,
    long? WarningThresholdBytes,
    long? CriticalThresholdBytes,
    long MaximumServerBudgetBytes,
    long? HardMemoryLimitBytes,
    ResourceMode ActiveProfile,
    ProcessPriorityClass PalworldPriority,
    ProcessPriorityClass MinecraftPriority,
    long? CpuAffinityMask,
    int? PalworldRootProcessId,
    int? PalworldGameProcessId,
    int PalworldPlayers,
    bool AutoSaveAndRestart,
    bool CriticalNotificationEnabled,
    bool RestoreBalancedOnServerStop,
    IReadOnlyList<string> Recommendations,
    IReadOnlyList<string> Warnings,
    bool AllowUnsafeStartupOverride,
    bool HasPalworldServer,
    bool HasMinecraftServer,
    bool IsPalworldRunning,
    bool IsMinecraftRunning,
    long ActiveManagedServerMemoryBytes,
    string ActiveProfileSummary);

public sealed record ManagedServerBudgetItem(
    Guid ServerId,
    GameType Game,
    ServerState State,
    int ProcessId,
    long WorkingSetBytes);

public sealed record ServerStartBudgetSnapshot(
    Guid ServerId,
    GameType Game,
    bool IsSafe,
    string Reason,
    long TotalPhysicalMemoryBytes,
    long AvailableMemoryBytes,
    long WindowsReserveBytes,
    long ActiveManagedServerMemoryBytes,
    long StartupSafetyAllowanceBytes,
    long MaximumServerBudgetBytes,
    long SafeAvailableCapacityBytes,
    long BudgetRemainingBytes,
    long EstimatedServerStartBytes,
    long EstimatedServerAllowanceBytes,
    long ExpectedRemainingMemoryBytes,
    bool AllowUnsafeStartupOverride,
    bool CanStartAnywayOnce,
    IReadOnlyList<ManagedServerBudgetItem> ActiveServers,
    DateTimeOffset CapturedAtUtc);
