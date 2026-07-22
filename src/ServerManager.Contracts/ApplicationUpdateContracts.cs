namespace ServerManager.Contracts;

public enum ApplicationUpdateChannel
{
    Stable = 1,
    Preview = 2,
    Development = 3
}

public enum ApplicationUpdateStage
{
    Idle = 0,
    Checking = 1,
    Available = 2,
    Downloading = 3,
    Verified = 4,
    ReadyToInstall = 5,
    Installing = 6,
    Succeeded = 7,
    Failed = 8,
    RolledBack = 9,
    FullSetupRequired = 10,
    UpdatePendingRestart = 11,
    InstalledVersionNewer = 12,
    IncompleteComponentUpdate = 13
}

public sealed record ApplicationUpdateManifest(
    string Version,
    string MinimumSupportedVersion,
    string ReleaseChannel,
    string PackageUrl,
    long PackageSize,
    string Sha256,
    string ReleaseNotesUrl,
    DateTimeOffset PublishedAt,
    bool RequiresElevation,
    bool RequiresServiceRestart,
    bool RequiresFullSetup,
    string? RollbackCompatibility = null,
    string? PackageFileName = null,
    string? ReleaseNotes = null,
    string? AgentUpdateMode = null,
    int BuildRevision = 0);

public sealed record ApplicationUpdateHistoryEntry(
    string Version,
    ApplicationUpdateStage Result,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? Message,
    int BuildRevision = 0);

public sealed record ApplicationUpdateStatusResponse(
    string CurrentVersion,
    string? LatestVersion,
    ApplicationUpdateChannel Channel,
    ApplicationUpdateStage Stage,
    bool IsUpdateAvailable,
    bool AutomaticChecksEnabled,
    int DownloadPercent,
    long DownloadedBytes,
    long? PackageSize,
    string? ReleaseNotes,
    DateTimeOffset? LastCheckedAtUtc,
    string? StagedPackagePath,
    string? RollbackStatus,
    string? LastError,
    bool CurrentBuildSigned,
    bool GameServerBusy,
    IReadOnlyList<ApplicationUpdateHistoryEntry> History,
    int CurrentBuildRevision = 0,
    int? LatestBuildRevision = null);

public sealed record ApplicationUpdateSettingsRequest(
    ApplicationUpdateChannel Channel,
    bool AutomaticChecksEnabled);

public sealed record ApplicationUpdateActionRequest(
    bool ApproveWhileGameServerBusy = false);

public sealed record ApplicationUpdateLaunchResponse(
    bool Success,
    string Message,
    string? UpdaterPath,
    IReadOnlyList<string> Arguments,
    ApplicationUpdateStatusResponse Status);
