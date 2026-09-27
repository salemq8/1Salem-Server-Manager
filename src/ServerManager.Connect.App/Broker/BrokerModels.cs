namespace ServerManager.Connect.App.Broker;

public enum MembershipState
{
    Pending,
    Approved,
    Rejected,
    Revoked,

    /// <summary>A state this version does not know. Treated as "no access" rather than guessed at.</summary>
    Unknown
}

public enum MembershipNodeState
{
    None,
    Candidate,
    Confirmed,
    Rejected,

    /// <summary>A node state this version does not know. It never grants access.</summary>
    Unknown
}

/// <summary>One of this device's servers, as <c>GET /v1/devices/me/memberships</c> reports it.</summary>
public sealed record Membership(
    string MembershipId,
    string OwnerId,
    string ServerId,
    string ServerLabel,
    MembershipState State,
    string? NodeId,
    MembershipNodeState NodeState = MembershipNodeState.None)
{
    /// <summary>Approved, but no tailnet node is bound yet: the enrollment blob is still to come.</summary>
    public bool NeedsEnrollment => State == MembershipState.Approved && NodeId is null && NodeState == MembershipNodeState.None;

    /// <summary>A reported node remains unusable until the owner has verified and confirmed it.</summary>
    public bool CanConnect =>
        State == MembershipState.Approved && NodeId is not null && NodeState == MembershipNodeState.Confirmed;

    public bool ConfirmationPending =>
        State == MembershipState.Approved && NodeState == MembershipNodeState.Candidate;
}

public sealed record InviteRedemption(string MembershipId, string ServerLabel);

/// <summary>The one-time enrollment pickup: ciphertext only, addressed to this device.</summary>
public sealed record EnrollmentPackage(string MembershipId, string OwnerId, string Ciphertext);

/// <summary>A session ticket. <see cref="ToString"/> never prints the ticket itself.</summary>
public sealed record SessionTicket(string Ticket, DateTimeOffset ExpiresAt, string HostBridge)
{
    public override string ToString() =>
        $"SessionTicket {{ Ticket = [REDACTED], ExpiresAt = {ExpiresAt:u}, HostBridge = {HostBridge} }}";
}
