namespace ServerManager.Contracts;

// 1Salem Connect, owner side (docs/CONNECT_PHASE2_PLAN.md §4, §7). The Agent serves these on
// /api/v1/connect/... and /api/v1/servers/{id}/connect/... to the local client only. Nothing here
// ever carries the OAuth client secret back, an auth key, a ticket or a stored invite secret; the
// one exception is ConnectInviteCreated, which returns a new invite's secret exactly once.

public enum ConnectSetupState
{
    /// <summary>No OAuth client is stored; nothing Connect-related runs.</summary>
    NotSetUp = 0,
    Starting = 1,
    Ready = 2,

    /// <summary>Set up, but a check failed that the owner must fix (policy, Tailnet Lock, pipe in use).</summary>
    NeedsAttention = 3,
    Error = 4
}

public enum ConnectPolicyState
{
    Unknown = 0,
    Safe = 1,

    /// <summary>A rule lets friend devices reach more than the host bridge port (§8).</summary>
    Unsafe = 2,

    /// <summary>A tag or the friend-to-host rule is missing, so Connect cannot work.</summary>
    Incomplete = 3,
    Unverifiable = 4,

    /// <summary>The OAuth client may not read the policy (it lacks policy_file:read).</summary>
    NotPermitted = 5
}

public enum ConnectHostNodeState
{
    NotEnrolled = 0,
    Enrolling = 1,
    Enrolled = 2,

    /// <summary>Tailnet Lock is on for this tailnet, which Connect does not support (§16).</summary>
    TailnetLockUnsupported = 3,
    Failed = 4
}

public sealed record ConnectStatusResponse(
    ConnectSetupState State,
    bool CredentialStored,
    string? ClientIdHint,
    ConnectPolicyState Policy,
    IReadOnlyList<string> PolicyReasons,
    ConnectHostNodeState HostNode,
    string? HostNodeId,
    string? HostBridge,
    bool BridgeRunning,
    bool RevocationChannel,
    int LiveConnections,
    bool BrokerReachable,
    string? OwnerId,
    DateTimeOffset? LastCheckedAtUtc,
    string? ErrorCode);

/// <summary>Sent once from the owner's PasswordBox to the Agent, which DPAPI-protects the secret.</summary>
public sealed record ConnectCredentialRequest(string ClientId, string ClientSecret)
{
    public override string ToString() => $"ConnectCredentialRequest {{ ClientId = {ClientId}, ClientSecret = [REDACTED] }}";
}

public enum ConnectEligibilityIssue
{
    NotMinecraft = 1,
    PortTooLow = 2,
    SensitivePort = 3,
    AgentPort = 4,
    SharedPort = 5,

    /// <summary>server.properties has prevent-proxy-connections=true, which kicks every friend (§17a).</summary>
    PreventProxyConnections = 6,

    /// <summary>server.properties binds a non-loopback server-ip, so the bridge cannot reach it.</summary>
    NonLoopbackServerIp = 7
}

public enum ConnectInviteState
{
    Active = 1,
    Used = 2,
    Revoked = 3,
    Expired = 4
}

public sealed record ConnectInviteItem(
    string InviteId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    ConnectInviteState State);

public sealed record ConnectInviteRequest(int TtlSeconds);

/// <summary>The only response that carries an invite secret; the Agent never stores it.</summary>
public sealed record ConnectInviteCreated(
    string InviteId,
    string Link,
    string Code,
    DateTimeOffset ExpiresAtUtc)
{
    public override string ToString() => $"ConnectInviteCreated {{ InviteId = {InviteId}, Link = [REDACTED], Code = [REDACTED], ExpiresAtUtc = {ExpiresAtUtc:O} }}";
}

public enum ConnectFriendState
{
    Pending = 1,
    Approved = 2
}

public enum ConnectFriendSetupState
{
    /// <summary>Not approved yet.</summary>
    None = 0,

    /// <summary>Approved; the friend's app has not set up this PC yet.</summary>
    WaitingForFriend = 1,

    /// <summary>The friend's PC reported its device; the Agent is checking it (D-1).</summary>
    Checking = 2,
    Ready = 3,

    /// <summary>The reported device failed the check and was rejected; revoke and invite again.</summary>
    Failed = 4
}

public sealed record ConnectFriendItem(
    string MembershipId,
    Guid ServerId,
    string DeviceId,
    string? Nickname,
    ConnectFriendState State,
    ConnectFriendSetupState Setup,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ApprovedAtUtc);

public sealed record ServerConnectResponse(
    Guid ServerId,
    bool AccountReady,
    bool Eligible,
    IReadOnlyList<ConnectEligibilityIssue> Issues,
    bool Enabled,
    IReadOnlyList<ConnectInviteItem> Invites,
    IReadOnlyList<ConnectFriendItem> Friends);

public enum ConnectStepOutcome
{
    NotStarted = 0,
    Done = 1,
    Pending = 2,
    Failed = 3,

    /// <summary>Nothing to do for this step (for example no device was ever confirmed).</summary>
    NotNeeded = 4,

    /// <summary>The friend's device is kept because another of their servers still uses it (P2-6).</summary>
    KeptInUse = 5
}

/// <summary>§12: "Access revoked" only when every applicable step is Done, NotNeeded or KeptInUse.</summary>
public sealed record ConnectRevokeResult(
    bool Completed,
    ConnectStepOutcome Broker,
    ConnectStepOutcome ThisPc,
    ConnectStepOutcome TailnetDevice,
    string? ErrorCode);

public sealed record ConnectNicknameRequest(string? Nickname);

/// <summary>Stable error codes of the Connect endpoints (OperationResult.ErrorCode).</summary>
public static class ConnectErrorCodes
{
    public const string LocalClientOnly = "ConnectLocalClientOnly";
    public const string NotSetUp = "ConnectNotSetUp";
    public const string NotReady = "ConnectNotReady";
    public const string CredentialInvalid = "ConnectCredentialInvalid";
    public const string CredentialRejected = "ConnectCredentialRejected";
    public const string PolicyUnsafe = "ConnectPolicyUnsafe";
    public const string PolicyNotPermitted = "ConnectPolicyNotPermitted";
    public const string TailnetLockUnsupported = "ConnectTailnetLockUnsupported";
    public const string NotEligible = "ConnectNotEligible";
    public const string NotFound = "ConnectNotFound";
    public const string InvalidState = "ConnectInvalidState";
    public const string BrokerUnavailable = "ConnectBrokerUnavailable";
    public const string BrokerRejected = "ConnectBrokerRejected";
    public const string TailnetUnavailable = "ConnectTailnetUnavailable";
    public const string Busy = "ConnectBusy";
    public const string InvalidRequest = "ConnectInvalidRequest";
}
