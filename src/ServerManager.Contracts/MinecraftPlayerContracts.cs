namespace ServerManager.Contracts;

/// <summary>UUID is the identity. Null fields mean the real server has not supplied that fact.</summary>
public sealed record MinecraftPlayerProfile(
    Guid Uuid,
    string? Username,
    bool? IsOnline,
    DateTimeOffset? FirstJoinedAtUtc,
    DateTimeOffset? LastSeenAtUtc,
    DateTimeOffset? SessionStartedAtUtc,
    TimeSpan? TotalPlayTime,
    bool? IsOperator,
    bool? IsWhitelisted,
    bool? IsBanned,
    string? GameMode = null,
    int? PingMilliseconds = null,
    string? Dimension = null);

public sealed record MinecraftPlayerDashboardSnapshot(
    Guid ServerId,
    MinecraftLiveControl Control,
    int? OnlinePlayers,
    int? MaxPlayers,
    bool IsStale,
    DateTimeOffset? LastVerifiedAtUtc,
    bool OnlineIdentitiesKnown,
    IReadOnlyList<MinecraftPlayerProfile> Players,
    DateTimeOffset CapturedAtUtc);

public sealed record MinecraftPlayerAdministrationRequest(Guid Uuid, MinecraftPlayerAction Action, bool Confirmed = false);
