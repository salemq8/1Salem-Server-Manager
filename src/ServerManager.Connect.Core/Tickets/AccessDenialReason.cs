namespace ServerManager.Connect.Core.Tickets;

/// <summary>
/// Why the host refused a ticket or a connection. This is for the owner's own logs and audit
/// on the host only. It never goes back to the friend: the bridge sends one generic refusal
/// byte and <see cref="AccessDenial.ExternalMessage"/> is the only text a remote party can see,
/// so a probe cannot tell a revoked device from an unknown server or a bad signature.
/// </summary>
public enum AccessDenialReason
{
    None = 0,
    MalformedTicket,
    UnsupportedAlgorithm,
    WrongTokenType,
    UnknownSigningKey,
    InvalidSignature,
    WrongIssuer,
    WrongAudience,
    NotYetValid,
    Expired,
    InvalidLifetime,
    UnsupportedProtocol,
    UnknownServer,
    ServerNotEnabled,
    UnsupportedGame,
    ServerProtocolMismatch,
    WrongPeerNode,
    TicketRevoked,
    DeviceRevoked,
    MembershipRevoked,
    AuthorizationVersionRevoked,
    MalformedPreamble,
    ProofOutsideClockWindow,
    InvalidProof,
    ReplayedNonce,
    ReplayCacheFull,

    /// <summary>
    /// The ticket has already opened <see cref="ReplayCache.DefaultPerTicketLimit"/> connections.
    /// Only this ticket is refused; the friend's next ticket starts afresh.
    /// </summary>
    TicketConnectionLimitReached
}

public static class AccessDenial
{
    /// <summary>The only denial text that may leave the host, whatever the internal reason.</summary>
    public const string ExternalMessage = "Connection refused.";
}
