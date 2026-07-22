namespace ServerManager.Contracts;

public enum PlayitRuntimeState
{
    NotInstalled = 0,
    Stopped = 1,
    Starting = 2,
    WaitingForLink = 3,
    Online = 4,
    Stopping = 5,
    Error = 6,
    RunningExternally = 7
}

public enum PlayitTunnelProtocol
{
    Tcp = 1,
    Udp = 2
}

public sealed record PlayitTunnelStatus(
    GameType Game,
    PlayitTunnelProtocol Protocol,
    string LocalHost,
    int LocalPort,
    string? PublicAddress,
    bool IsConfigured,
    bool IsVerified,
    string? ValidationMessage);

public sealed record PlayitStatusResponse(
    bool IsInstalled,
    string? ExecutablePath,
    string? Version,
    PlayitRuntimeState State,
    bool IsRunning,
    bool IsLinked,
    bool IsVerified,
    bool IsEnabled,
    bool IsManagedProcess,
    int? ProcessId,
    string? AgentName,
    string? ClaimUrl,
    DateTimeOffset? LastConnectionAtUtc,
    string? LastError,
    PlayitTunnelStatus Minecraft,
    PlayitTunnelStatus Palworld,
    IReadOnlyList<string> RecentLog);

public sealed record PlayitSettingsRequest(
    bool Enabled,
    string? AgentName,
    string? MinecraftPublicAddress,
    string? PalworldPublicAddress);

public sealed record PlayitActionResponse(
    bool Success,
    string Message,
    PlayitStatusResponse Status);
