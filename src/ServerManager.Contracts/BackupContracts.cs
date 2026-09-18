namespace ServerManager.Contracts;

public sealed record BackupScheduleRequest(
    Guid ServerId,
    string Expression,
    bool Enabled = true,
    string? DestinationRoot = null,
    int MaximumCount = 20,
    int MaximumAgeDays = 30,
    long MaximumTotalBytes = 107_374_182_400,
    bool KeepDaily = true,
    bool KeepWeekly = true);

public sealed record PalworldBackupCreateRequest(
    string? DestinationRoot = null,
    bool IncludeLogs = false,
    bool ProtectAfterCreation = false,
    string? DisplayName = null,
    string? Notes = null);

public sealed record BackupMetadataUpdateRequest(
    string? DisplayName,
    string? Notes,
    bool IsProtected);

public sealed record BackupCenterSettings(
    Guid ServerId,
    bool ScheduleEnabled,
    string ScheduleExpression,
    string DestinationRoot,
    int MaximumCount,
    int MaximumAgeDays,
    long MaximumTotalBytes,
    bool KeepDaily,
    bool KeepWeekly,
    DateTimeOffset? NextRunAtUtc,
    DateTimeOffset? LastRunAtUtc,
    DateTimeOffset? LastSuccessfulWorldSaveAtUtc,
    string? LastRunStatusMessage = null);

public sealed record BackupDestinationValidationResult(
    bool IsValid,
    string Code,
    string Message,
    string ResolvedPath,
    long AvailableBytes);
