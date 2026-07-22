using System.Diagnostics;
using ServerManager.Contracts;

namespace ServerManager.Core;

public sealed record GameServerDefinition(
    Guid Id,
    GameType Game,
    string Name,
    string RootPath,
    int Port,
    string? InstalledVersion,
    DateTimeOffset CreatedAtUtc,
    ServerState State = ServerState.Stopped,
    int? MinimumMemoryMb = null,
    int? MaximumMemoryMb = null,
    string? JavaExecutablePath = null,
    string? PreferredAdapterId = null,
    bool AutoStart = false,
    bool AutoRestart = true,
    ProcessPriorityClass Priority = ProcessPriorityClass.Normal,
    long? CpuAffinityMask = null,
    string? LastError = null,
    DateTimeOffset? LastBackupAtUtc = null,
    string? UpdateStatus = null);

public sealed record InstallationDetection(
    bool IsInstalled,
    string RootPath,
    string? Version,
    IReadOnlyList<string> DetectedFiles,
    IReadOnlyList<string> Warnings);

public sealed record ProcessLaunchSpec(
    string FileName,
    string Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    bool RedirectStandardInput = true,
    bool RedirectStandardOutput = true,
    bool RedirectStandardError = true,
    IReadOnlyList<string>? ArgumentList = null);

public sealed record ProcessSnapshot(
    Guid ServerId,
    int ProcessId,
    ServerState State,
    DateTimeOffset StartedAtUtc,
    long WorkingSetBytes,
    double CpuPercent,
    int? ExitCode,
    long PrivateMemoryBytes = 0,
    long PeakWorkingSetBytes = 0,
    ProcessPriorityClass Priority = ProcessPriorityClass.Normal,
    long? CpuAffinityMask = null,
    IReadOnlyList<string>? Arguments = null,
    int? GameProcessId = null,
    int ChildProcessCount = 0,
    string? RootExecutableName = null,
    string? GameExecutableName = null,
    int ThreadCount = 0);

public sealed record RestartPolicy(
    bool Enabled,
    TimeSpan Delay,
    int MaximumAttempts,
    TimeSpan AttemptWindow)
{
    public static RestartPolicy Disabled { get; } =
        new(false, TimeSpan.FromSeconds(5), 3, TimeSpan.FromMinutes(10));
}

public sealed record ProcessMetricsSnapshot(
    Guid ServerId,
    int ProcessId,
    ServerState State,
    double CpuPercent,
    long WorkingSetBytes,
    long PrivateMemoryBytes,
    DateTimeOffset CapturedAtUtc);

public sealed record UpdateCheckResult(
    string? InstalledVersion,
    string? LatestVersion,
    bool IsUpdateAvailable,
    DateTimeOffset CheckedAtUtc);

public sealed record BackupRequest(
    Guid ServerId,
    string DestinationRoot,
    bool IncludeLogs,
    bool SafeOffline,
    bool IsScheduled = false,
    bool ProtectAfterCreation = false,
    string? DisplayName = null,
    string? Notes = null);

public sealed record BackupResult(
    Guid BackupId,
    string ArchivePath,
    string Sha256,
    long SizeBytes,
    int FileCount,
    DateTimeOffset CreatedAtUtc);

public sealed record ResourcePolicy(
    ResourceMode Mode,
    ProcessPriorityClass MinecraftPriority,
    ProcessPriorityClass PalworldPriority,
    long WindowsReserveBytes,
    long MaximumServerBudgetBytes,
    long? CpuAffinityMask = null,
    bool StopLowerPriorityGame = false,
    long? PalworldWarningThresholdBytes = null,
    long? PalworldCriticalThresholdBytes = null,
    bool CriticalNotificationEnabled = false,
    bool AutoSaveAndRestart = false,
    long? HardMemoryLimitBytes = null,
    bool RestoreBalancedOnServerStop = false,
    bool AllowUnsafeStartupOverride = false);

public sealed record SystemResourceSnapshot(
    DateTimeOffset CapturedAtUtc,
    long TotalMemoryBytes,
    long AvailableMemoryBytes,
    double CpuPercent,
    long SystemDriveFreeBytes,
    ResourcePolicy ActivePolicy,
    IReadOnlyList<string> Warnings);

public sealed record NetworkSnapshot(
    string MachineName,
    string? LocalIpv4,
    bool IsPublicProfile,
    DateTimeOffset CapturedAtUtc,
    IReadOnlyList<NetworkAdapterSnapshot>? Adapters = null,
    string? PreferredAdapterId = null);

public sealed record NetworkAdapterSnapshot(
    string Id,
    string Name,
    string Description,
    string Ipv4,
    bool HasDefaultGateway,
    bool IsPreferred);

public sealed record MinecraftMemoryRecommendation(
    int RecommendedMinimumMemoryMb,
    int RecommendedMaximumMemoryMb,
    int WindowsReserveMemoryMb,
    int MaximumSafeMemoryMb,
    bool IsSafe,
    IReadOnlyList<string> Warnings);

public sealed record MinecraftInstallStep(
    MinecraftCreationStage Stage,
    string Message,
    int Percent);

public sealed record FirewallRuleSpec(
    string Name,
    GameType Game,
    int Port,
    string Protocol,
    string ExecutablePath);

public sealed record ImportRequest(
    GameType Game,
    ImportMode Mode,
    string SourcePath,
    string DestinationPath,
    string? ConfirmationText = null);

public sealed record ImportPlan(
    ImportRequest Request,
    long RequiredBytes,
    int FileCount,
    IReadOnlyList<string> PreservedPaths,
    IReadOnlyList<string> Warnings);

public sealed record JarSwapResult(
    string ActiveJarPath,
    string RollbackJarPath,
    string InstalledSha1);

public sealed record LogEntry(
    DateTimeOffset TimestampUtc,
    string Level,
    string Source,
    string Message,
    bool IsStandardError = false);

public sealed record PairingChallenge(
    string Code,
    DateTimeOffset ExpiresAtUtc);

public sealed record PairingResult(
    Guid ClientId,
    string ClientName,
    string ProtectedCredential,
    string CertificateFingerprint);

public sealed record PairedClientRecord(
    Guid Id,
    string Name,
    string CertificateFingerprint,
    string CredentialHash,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? LastConnectedAtUtc,
    DateTimeOffset? RevokedAtUtc);

public sealed record OperationResult(
    bool Success,
    string? ErrorCode = null,
    string? Message = null)
{
    public static OperationResult Ok() => new(true);

    public static OperationResult Fail(string errorCode, string message) =>
        new(false, errorCode, message);
}

public sealed record JavaRuntimeInfo(
    string ExecutablePath,
    int MajorVersion,
    string VersionText);

public sealed record SteamCmdInstallResult(
    string InstallDirectory,
    string? BuildId,
    string Output);

public sealed record BackupManifestFile(
    string RelativePath,
    long SizeBytes,
    string Sha256);

public sealed record BackupManifest(
    Guid BackupId,
    GameType Game,
    string Version,
    DateTimeOffset CreatedAtUtc,
    string SourcePath,
    IReadOnlyList<string> IncludedPaths,
    int FileCount,
    long TotalBytes,
    string ApplicationVersion,
    IReadOnlyList<BackupManifestFile> Files);

public sealed record BackupVerificationResult(
    bool IsValid,
    string? Error,
    BackupManifest? Manifest);

public sealed record BackupRetentionSettings(
    int MaximumCount,
    TimeSpan MaximumAge,
    long MaximumTotalBytes = long.MaxValue,
    bool KeepDaily = false,
    bool KeepWeekly = false);

public sealed record ManagedFileEntry(
    string Name,
    string RelativePath,
    bool IsDirectory,
    long SizeBytes,
    DateTimeOffset LastWriteAtUtc);

public sealed record ManagedTextFile(
    string RelativePath,
    string Content,
    DateTimeOffset LastWriteAtUtc);
