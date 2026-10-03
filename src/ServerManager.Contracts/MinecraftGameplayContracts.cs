namespace ServerManager.Contracts;

/// <summary>How the manager can reach a Minecraft server's controls right now.</summary>
public enum MinecraftLiveControl
{
    /// <summary>Running, started by 1Salem and ready: gamerules and player actions apply now.</summary>
    Live = 1,

    /// <summary>Not running: gamerule changes wait for the next start; server.properties changes apply at start.</summary>
    Stopped = 2,

    /// <summary>Started but not ready yet ("Done" not printed).</summary>
    Starting = 3,

    /// <summary>
    /// Running, but started before the Agent last restarted, so the Agent has no console for it.
    /// Live changes need the server restarted from 1Salem.
    /// </summary>
    NoConsole = 4
}

/// <summary>Where a shown gamerule value comes from.</summary>
public enum MinecraftValueSource
{
    /// <summary>Asked of the running server just now.</summary>
    Live = 1,

    /// <summary>Read from the world's level.dat while the server is not answering.</summary>
    WorldFile = 2,

    /// <summary>No world yet and no running server: nothing real to show.</summary>
    Unknown = 3
}

/// <summary>
/// One gamerule as this server has it. <see cref="Supported"/> is false when this server's
/// Minecraft version does not have the rule (it is then not offered). <see cref="ServerName"/> is
/// the exact name this server uses, which can differ between versions.
/// </summary>
public sealed record MinecraftGameRuleState(
    string Key,
    string? ServerName,
    bool Supported,
    bool? Value,
    MinecraftValueSource Source,
    bool? PendingValue);

/// <summary>A server.properties value as saved on disk; it takes effect when the server starts.</summary>
public sealed record MinecraftPropertyState(string Key, string? Value, bool CanApplyLive);

/// <summary>
/// The fall-damage choices that are genuinely available. Vanilla and the Bukkit family only have
/// the boolean fallDamage gamerule, so this is [100, 0] there, and empty when that rule is missing.
/// </summary>
public sealed record MinecraftFallDamageCapability(bool Supported, IReadOnlyList<int> Percentages);

public sealed record MinecraftGameplaySnapshot(
    Guid ServerId,
    string? MinecraftVersion,
    ServerPlatform Platform,
    MinecraftLiveControl Control,
    bool GameRulesKnown,
    IReadOnlyList<MinecraftGameRuleState> GameRules,
    IReadOnlyList<MinecraftPropertyState> Properties,
    MinecraftFallDamageCapability FallDamage,
    DateTimeOffset CapturedAtUtc);

public sealed record MinecraftGameRuleChangeRequest(string Key, bool Value);

public enum MinecraftChangeOutcome
{
    /// <summary>Applied to the running server and read back.</summary>
    AppliedLive = 1,

    /// <summary>Saved and applied automatically the next time 1Salem starts the server.</summary>
    PendingNextStart = 2,

    Failed = 3
}

public sealed record MinecraftChangeResult(
    MinecraftChangeOutcome Outcome,
    string? ErrorCode = null,
    string? Message = null,
    bool? VerifiedValue = null);

/// <summary>server.properties keys (see MinecraftGameplayPropertyPolicy) and their new values.</summary>
public sealed record MinecraftPropertiesChangeRequest(IReadOnlyDictionary<string, string> Values);

public sealed record MinecraftPropertiesChangeResult(
    bool Success,
    string? ErrorCode,
    string? Message,
    bool RestartRequired,
    IReadOnlyList<string> AppliedLive);

public enum MinecraftPlayerAction
{
    Op = 1,
    Deop = 2,
    WhitelistAdd = 3,
    WhitelistRemove = 4,
    Kick = 5,
    Ban = 6,
    Pardon = 7
}

public sealed record MinecraftPlayerActionRequest(MinecraftPlayerAction Action, string Player, bool Confirmed = false);

/// <summary>
/// Players as the server has them. <see cref="OnlineKnown"/> is false when the server is not
/// answering (stopped, starting, or no console); the list is then empty rather than guessed.
/// </summary>
public sealed record MinecraftPlayersState(
    MinecraftLiveControl Control,
    bool OnlineKnown,
    IReadOnlyList<string> Online,
    int? MaxPlayers,
    IReadOnlyList<string> Operators,
    IReadOnlyList<string> Whitelisted,
    IReadOnlyList<string> Banned,
    DateTimeOffset CapturedAtUtc);
