using ServerManager.Contracts;

namespace ServerManager.Core;

public interface IGameServerProvider
{
    GameType Game { get; }

    Task<InstallationDetection> DetectInstallationAsync(
        string rootPath,
        CancellationToken cancellationToken = default);

    ProcessLaunchSpec CreateLaunchSpec(GameServerDefinition server);

    Task PrepareForStartAsync(
        GameServerDefinition server,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    Task CleanupAfterStopAsync(
        GameServerDefinition server,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

public interface IProcessSupervisor
{
    Task<ProcessSnapshot> StartAsync(
        GameServerDefinition server,
        ProcessLaunchSpec launchSpec,
        CancellationToken cancellationToken = default);

    Task<ProcessSnapshot?> AdoptAsync(
        GameServerDefinition server,
        ProcessLaunchSpec launchSpec,
        int? expectedProcessId = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<ProcessSnapshot?>(null);

    Task<OperationResult> StopAsync(
        Guid serverId,
        bool force,
        CancellationToken cancellationToken = default);

    Task<ProcessSnapshot> RestartAsync(
        GameServerDefinition server,
        ProcessLaunchSpec launchSpec,
        CancellationToken cancellationToken = default);

    Task<ProcessSnapshot?> GetSnapshotAsync(
        Guid serverId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProcessSnapshot>> GetAllSnapshotsAsync(
        CancellationToken cancellationToken = default);

    void ConfigureRestartPolicy(Guid serverId, RestartPolicy policy);
}

public interface IUpdateService
{
    Task<UpdateCheckResult> CheckAsync(
        GameServerDefinition server,
        CancellationToken cancellationToken = default);

    Task<OperationResult> UpdateAsync(
        GameServerDefinition server,
        CancellationToken cancellationToken = default);
}

public interface IBackupService
{
    Task<BackupResult> CreateAsync(
        BackupRequest request,
        CancellationToken cancellationToken = default);

    Task<OperationResult> RestoreAsync(
        Guid backupId,
        CancellationToken cancellationToken = default);

    Task<BackupVerificationResult> VerifyAsync(
        Guid backupId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BackupRecord>> ListAsync(
        Guid serverId,
        CancellationToken cancellationToken = default);

    Task<OperationResult> DeleteAsync(
        Guid backupId,
        CancellationToken cancellationToken = default);

    Task<OperationResult> UpdateMetadataAsync(
        Guid backupId,
        string? displayName,
        string? notes,
        bool isProtected,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Completes any restore transactions left behind by a crash or forced shutdown that
    /// occurred after live files were swapped but before the restore committed. Safe to call
    /// on every Agent startup; returns the number of interrupted restores it repaired.
    /// </summary>
    Task<int> RecoverInterruptedRestoresAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(0);
}

public interface IResourceGovernor
{
    ResourcePolicy ActivePolicy { get; }

    Task ApplyAsync(
        ResourcePolicy policy,
        CancellationToken cancellationToken = default);

    Task<SystemResourceSnapshot> GetSnapshotAsync(
        CancellationToken cancellationToken = default);
}

public interface IProcessResourceController
{
    Task<OperationResult> ApplyResourcesAsync(
        Guid serverId,
        System.Diagnostics.ProcessPriorityClass priority,
        long? cpuAffinityMask,
        CancellationToken cancellationToken = default,
        long? hardMemoryLimitBytes = null);
}

public interface ISystemResourceReader
{
    SystemResourceSnapshot Capture(ResourcePolicy activePolicy);
}

/// <summary>
/// Serializes conflicting operations against the same registered server (Start, Stop, Restart,
/// Backup, Restore, ...) across every service that shares one instance of this coordinator, so
/// two callers can never mutate the same server's live process or files at the same time.
/// Locking is per-server, not global -- operations against different servers never wait on
/// each other.
/// </summary>
public interface IServerOperationCoordinator
{
    /// <summary>
    /// Waits (up to <paramref name="timeout"/>) for exclusive access to <paramref name="serverId"/>,
    /// then returns a handle that releases it on disposal. Throws <see cref="ServerBusyException"/>
    /// if another operation against the same server is still in flight when the timeout elapses.
    /// <paramref name="operationName"/> (e.g. "Start", "Restore") is surfaced in that exception so a
    /// caller blocked behind another in-flight operation knows what it's waiting on.
    /// </summary>
    Task<IAsyncDisposable> AcquireAsync(
        Guid serverId,
        string operationName,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Thrown when a caller could not obtain exclusive access to a server within the allotted time
/// because another operation (Start/Stop/Restart/Backup/Restore/...) against the same server is
/// still in flight.
/// </summary>
public sealed class ServerBusyException(Guid serverId, string operationInProgress)
    : InvalidOperationException(
        $"Another operation ({operationInProgress}) is already in progress for this server. Wait for it to finish and try again.")
{
    public Guid ServerId { get; } = serverId;
}

public interface INetworkService
{
    Task<NetworkSnapshot> GetSnapshotAsync(
        CancellationToken cancellationToken = default);

    Task<OperationResult> SetPreferredAdapterAsync(
        string? adapterId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(OperationResult.Fail(
            "PreferredAdapterUnsupported",
            "Preferred adapter selection is not supported."));

    Task<PortTestResponse> TestPortAsync(
        int port,
        string protocol,
        CancellationToken cancellationToken = default);
}

public interface IFirewallService
{
    Task<OperationResult> EnsureRuleAsync(
        FirewallRuleSpec rule,
        CancellationToken cancellationToken = default);

    Task<OperationResult> RemoveRuleAsync(
        string ruleName,
        CancellationToken cancellationToken = default);
}

public interface IFileImportService
{
    Task<ImportPlan> PlanAsync(
        ImportRequest request,
        CancellationToken cancellationToken = default);

    Task<OperationResult> ExecuteAsync(
        ImportPlan plan,
        CancellationToken cancellationToken = default);
}

public interface ILogStreamService
{
    IAsyncEnumerable<LogEntry> StreamAsync(
        Guid serverId,
        CancellationToken cancellationToken = default);
}

public interface IConsoleService
{
    Task<OperationResult> SendCommandAsync(
        Guid serverId,
        string command,
        CancellationToken cancellationToken = default);
}

public interface ISecretStore
{
    string Protect(string plaintext);

    string Unprotect(string protectedValue);
}

public interface ISettingsStore
{
    Task<T?> GetAsync<T>(
        string key,
        CancellationToken cancellationToken = default);

    Task SetAsync<T>(
        string key,
        T value,
        CancellationToken cancellationToken = default);
}

public interface IPairingService
{
    Task<PairingChallenge> CreateChallengeAsync(
        CancellationToken cancellationToken = default);

    Task<PairingResult> CompleteAsync(
        string code,
        string clientName,
        string clientAddress,
        CancellationToken cancellationToken = default);

    Task RevokeAsync(
        Guid clientId,
        CancellationToken cancellationToken = default);

    Task RenameAsync(
        Guid clientId,
        string name,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PairedClientRecord>> ListClientsAsync(
        CancellationToken cancellationToken = default);

    Task<PairedClientRecord?> ValidateCredentialAsync(
        string credential,
        CancellationToken cancellationToken = default);
}

public interface IApplicationDatabase
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
}

public interface IAuditLogStore
{
    Task WriteAsync(
        string actor,
        string action,
        string target,
        bool succeeded,
        string? detail = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AuditLogRecord>> ListRecentAsync(
        string targetPrefix,
        int limit,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AuditLogRecord>>([]);
}

public interface IGameServerStore
{
    Task<IReadOnlyList<GameServerDefinition>> ListAsync(
        CancellationToken cancellationToken = default);

    Task<GameServerDefinition?> GetAsync(
        Guid serverId,
        CancellationToken cancellationToken = default);

    Task UpsertAsync(
        GameServerDefinition server,
        ServerState state,
        CancellationToken cancellationToken = default);

    Task SetStateAsync(
        Guid serverId,
        ServerState state,
        CancellationToken cancellationToken = default);

    Task SetStateWithErrorAsync(
        Guid serverId,
        ServerState state,
        string? lastError,
        CancellationToken cancellationToken = default) =>
        SetStateAsync(serverId, state, cancellationToken);

    /// <summary>
    /// Removes one server's registration (and the database rows that belong to it) without
    /// touching any file on disk. Returns false when no such server was registered. Stores
    /// that cannot remove registrations say so rather than pretending to.
    /// </summary>
    Task<bool> DeleteRegistrationAsync(
        Guid serverId,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This server store cannot remove registrations.");
}

public interface IMinecraftVersionCatalog
{
    Task<IReadOnlyList<MinecraftVersionDescriptor>> GetReleasesAsync(
        CancellationToken cancellationToken = default);

    Task<MinecraftVersionDescriptor> GetVersionAsync(
        string version,
        CancellationToken cancellationToken = default);
}

public interface IMinecraftInstaller
{
    Task<MinecraftInstallResult> InstallAsync(
        MinecraftInstallRequest request,
        CancellationToken cancellationToken = default);

    Task<MinecraftInstallResult> InstallWithProgressAsync(
        MinecraftInstallRequest request,
        IProgress<MinecraftInstallStep> progress,
        CancellationToken cancellationToken = default) =>
        InstallAsync(request, cancellationToken);
}

public interface IJavaRuntimeLocator
{
    Task<JavaRuntimeInfo?> FindAsync(
        int minimumMajorVersion,
        CancellationToken cancellationToken = default);

    Task<JavaRuntimeInfo?> InspectAsync(
        string executablePath,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<JavaRuntimeInfo?>(null);
}

public interface IJavaRuntimeInstaller
{
    Task<JavaRuntimeInfo> InstallAsync(
        int majorVersion,
        CancellationToken cancellationToken = default);
}

public interface ISteamCmdService
{
    Task<string> EnsureInstalledAsync(CancellationToken cancellationToken = default);

    Task<SteamCmdInstallResult> InstallOrUpdateAsync(
        string destinationPath,
        int appId,
        CancellationToken cancellationToken = default);

    Task<string?> GetLatestBuildIdAsync(
        int appId,
        CancellationToken cancellationToken = default);
}

public interface IPalworldInstaller
{
    Task<PalworldInstallResult> InstallAsync(
        PalworldInstallRequest request,
        CancellationToken cancellationToken = default);
}

public interface IBackupStore
{
    Task UpsertAsync(BackupRecord backup, CancellationToken cancellationToken = default);

    Task<BackupRecord?> GetAsync(Guid backupId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BackupRecord>> ListAsync(
        Guid serverId,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid backupId, CancellationToken cancellationToken = default);
}

public interface IScheduleStore
{
    Task UpsertAsync(
        ScheduleRecord schedule,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ScheduleRecord>> ListAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ScheduleRecord>> GetDueAsync(
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);

    Task MarkRunAsync(
        Guid scheduleId,
        DateTimeOffset lastRunUtc,
        DateTimeOffset nextRunUtc,
        CancellationToken cancellationToken = default);
}

public interface IClientStore
{
    Task UpsertAsync(
        PairedClientRecord client,
        CancellationToken cancellationToken = default);

    Task<PairedClientRecord?> FindByCredentialHashAsync(
        string credentialHash,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PairedClientRecord>> ListAsync(
        CancellationToken cancellationToken = default);

    Task RenameAsync(
        Guid clientId,
        string name,
        CancellationToken cancellationToken = default);

    Task RevokeAsync(
        Guid clientId,
        DateTimeOffset revokedAtUtc,
        CancellationToken cancellationToken = default);

    Task MarkConnectedAsync(
        Guid clientId,
        DateTimeOffset connectedAtUtc,
        CancellationToken cancellationToken = default);
}

public interface IRegisteredFileService
{
    Task<IReadOnlyList<ManagedFileEntry>> ListAsync(
        Guid serverId,
        string relativePath,
        CancellationToken cancellationToken = default);

    Task<ManagedTextFile> ReadTextAsync(
        Guid serverId,
        string relativePath,
        CancellationToken cancellationToken = default);

    Task<OperationResult> WriteTextAsync(
        Guid serverId,
        string relativePath,
        string content,
        CancellationToken cancellationToken = default);

    Task<OperationResult> RenameAsync(
        Guid serverId,
        string relativePath,
        string newName,
        CancellationToken cancellationToken = default);

    Task<OperationResult> DeleteAsync(
        Guid serverId,
        string relativePath,
        string confirmationText,
        CancellationToken cancellationToken = default);
}
