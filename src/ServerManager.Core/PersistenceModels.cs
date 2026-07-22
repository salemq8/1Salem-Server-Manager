using ServerManager.Contracts;

namespace ServerManager.Core;

public sealed record SettingRecord(
    string Key,
    string JsonValue,
    DateTimeOffset UpdatedAtUtc);

public sealed record AgentRecord(
    string Id,
    string MachineName,
    string CertificateFingerprint,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? LastSeenAtUtc);

public sealed record ClientRecord(
    Guid Id,
    string Name,
    string CertificateFingerprint,
    string ProtectedCredential,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? LastConnectedAtUtc,
    DateTimeOffset? RevokedAtUtc);

public sealed record ServerRuntimeSettingsRecord(
    Guid ServerId,
    string JsonValue,
    DateTimeOffset UpdatedAtUtc);

public sealed record UpdateStateRecord(
    Guid ServerId,
    string? InstalledVersion,
    string? LatestVersion,
    DateTimeOffset? LastCheckedAtUtc,
    DateTimeOffset? LastUpdatedAtUtc,
    bool AutoUpdateEnabled,
    string? LastResult);

public sealed record BackupRecord(
    Guid Id,
    Guid ServerId,
    string ArchivePath,
    string Version,
    string Sha256,
    long SizeBytes,
    int FileCount,
    BackupStatus Status,
    DateTimeOffset CreatedAtUtc,
    bool IsProtected = false,
    string? DisplayName = null,
    string? Notes = null,
    bool IsScheduled = false);

public sealed record ScheduleRecord(
    Guid Id,
    Guid? ServerId,
    string Kind,
    string CronExpression,
    bool Enabled,
    DateTimeOffset? NextRunAtUtc,
    DateTimeOffset? LastRunAtUtc);

public sealed record AuditLogRecord(
    long Id,
    DateTimeOffset TimestampUtc,
    string Actor,
    string Action,
    string Target,
    bool Succeeded,
    string? Detail);

public sealed record CrashHistoryRecord(
    long Id,
    Guid ServerId,
    DateTimeOffset CrashedAtUtc,
    int? ExitCode,
    int RestartAttempt,
    string? Detail);
