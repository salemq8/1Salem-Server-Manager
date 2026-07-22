namespace ServerManager.Contracts;

public sealed record ConfigurationRestorePointItem(
    string Id,
    Guid ServerId,
    GameType Game,
    DateTimeOffset CreatedAtUtc,
    string ApplicationVersion,
    string? GameVersion,
    string Reason,
    string Summary,
    IReadOnlyDictionary<string, string> OriginalFileHashes,
    IReadOnlyDictionary<string, string> AppliedFileHashes,
    bool KnownWorking,
    string? Label,
    long ProtectedSizeBytes);

public sealed record ConfigurationRestorePointRestoreRequest(
    string RestorePointId,
    bool ConfirmRestart);

public sealed record ConfigurationRestorePointLabelRequest(string? Label);

public sealed record ConfigurationRestorePointActionResponse(
    bool Success,
    string Message,
    bool Restarted = false,
    bool Verified = false,
    bool RolledBack = false,
    string? ErrorCode = null);
