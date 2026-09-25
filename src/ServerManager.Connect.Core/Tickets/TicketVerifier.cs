using ServerManager.Connect.Core.Crypto;

namespace ServerManager.Connect.Core.Tickets;

/// <summary>
/// Host-side ticket verification, in the order of contract §9: shape, <c>alg</c>, <c>typ</c>,
/// pinned <c>kid</c>, 64-byte P1363 signature, <c>iss</c>, <c>aud</c>, time window (30 s skew),
/// lifetime ≤ 900 s, <c>proto</c>, <c>sid</c> against the catalog, <c>nid</c> against the peer's
/// WhoIs node id, revocation of <c>jti</c>/<c>sub</c>/<c>mid</c>, and the <c>av</c> floor.
/// The final §9 step, the connection proof, needs the preamble, so <see cref="HostAuthorizer"/>
/// runs it after this class. Connections must go through the authorizer, never through this
/// class alone.
/// </summary>
public sealed class TicketVerifier
{
    public const string ExpectedIssuer = "1salem-connect-broker";
    public const string ExpectedType = CompactTicket.ExpectedType;
    public const string SupportedProtocol = "tcp";
    public const long MaxLifetimeSeconds = 900;

    public static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(30);

    private readonly TicketKeySet _keys;
    private readonly string _ownerId;
    private readonly IConnectServerCatalog _catalog;
    private readonly RevocationSet _revocations;
    private readonly TimeProvider _clock;

    public TicketVerifier(
        TicketKeySet keys,
        string ownerId,
        IConnectServerCatalog catalog,
        RevocationSet revocations,
        TimeProvider clock)
    {
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        if (!ConnectKeyIds.IsOwnerId(ownerId))
        {
            throw new ArgumentException("The host must be configured with its own owner id.", nameof(ownerId));
        }

        _ownerId = ownerId;
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _revocations = revocations ?? throw new ArgumentNullException(nameof(revocations));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    internal TimeProvider Clock => _clock;

    /// <param name="ticket">The compact JWS from the preamble.</param>
    /// <param name="peerNodeId">
    /// The StableNodeID that WhoIs reported for the connecting peer. Never a value the peer
    /// asserted about itself (except in the explicitly insecure fake transport).
    /// </param>
    public TicketVerificationResult Verify(string? ticket, string? peerNodeId)
    {
        if (!CompactTicket.TryParse(ticket, out var token) ||
            !TicketHeader.TryParse(token.Header, out var header))
        {
            return TicketVerificationResult.Deny(AccessDenialReason.MalformedTicket);
        }

        if (!string.Equals(header.Algorithm, Es256.Algorithm, StringComparison.Ordinal))
        {
            return TicketVerificationResult.Deny(AccessDenialReason.UnsupportedAlgorithm);
        }

        if (!string.Equals(header.Type, ExpectedType, StringComparison.Ordinal))
        {
            return TicketVerificationResult.Deny(AccessDenialReason.WrongTokenType);
        }

        if (header.KeyId is null || !_keys.TryGet(header.KeyId, out var key))
        {
            return TicketVerificationResult.Deny(AccessDenialReason.UnknownSigningKey);
        }

        if (!header.HasOnlyKnownMembers)
        {
            return TicketVerificationResult.Deny(AccessDenialReason.MalformedTicket);
        }

        if (token.Signature.Length != Es256.SignatureLength ||
            !Es256.Verify(key.Spki, token.SigningInput, token.Signature))
        {
            return TicketVerificationResult.Deny(AccessDenialReason.InvalidSignature);
        }

        // The payload is parsed only after the signature holds, so nothing unauthenticated
        // reaches the claim parser.
        if (!TicketClaims.TryParse(token.Payload, out var claims))
        {
            return TicketVerificationResult.Deny(AccessDenialReason.MalformedTicket);
        }

        var denial = CheckClaims(claims, peerNodeId, out var server);
        return denial == AccessDenialReason.None
            ? TicketVerificationResult.Allow(claims, server!)
            : TicketVerificationResult.Deny(denial);
    }

    private AccessDenialReason CheckClaims(TicketClaims claims, string? peerNodeId, out ConnectServerEntry? server)
    {
        server = null;
        if (!string.Equals(claims.Issuer, ExpectedIssuer, StringComparison.Ordinal))
        {
            return AccessDenialReason.WrongIssuer;
        }

        if (!string.Equals(claims.Audience, _ownerId, StringComparison.Ordinal))
        {
            return AccessDenialReason.WrongAudience;
        }

        var now = _clock.GetUtcNow().ToUnixTimeSeconds();
        var skew = (long)ClockSkew.TotalSeconds;
        if (claims.NotBefore > now + skew || claims.IssuedAt > now + skew)
        {
            return AccessDenialReason.NotYetValid;
        }

        if (claims.ExpiresAt <= now - skew)
        {
            return AccessDenialReason.Expired;
        }

        if (claims.ExpiresAt <= claims.IssuedAt ||
            claims.ExpiresAt <= claims.NotBefore ||
            claims.ExpiresAt - claims.IssuedAt > MaxLifetimeSeconds)
        {
            return AccessDenialReason.InvalidLifetime;
        }

        if (!string.Equals(claims.Protocol, SupportedProtocol, StringComparison.Ordinal))
        {
            return AccessDenialReason.UnsupportedProtocol;
        }

        var entry = _catalog.Find(claims.ServerId);
        if (entry is null || entry.ServerId != claims.ServerId)
        {
            return AccessDenialReason.UnknownServer;
        }

        if (!entry.ConnectEnabled)
        {
            return AccessDenialReason.ServerNotEnabled;
        }

        // Phase 1 bridges Minecraft over TCP only. Palworld is UDP and not implemented (§18).
        if (entry.Game != ConnectGameKind.Minecraft)
        {
            return AccessDenialReason.UnsupportedGame;
        }

        if (entry.Protocol != ConnectProtocol.Tcp)
        {
            return AccessDenialReason.ServerProtocolMismatch;
        }

        if (string.IsNullOrEmpty(peerNodeId) ||
            !string.Equals(claims.NodeId, peerNodeId, StringComparison.Ordinal))
        {
            return AccessDenialReason.WrongPeerNode;
        }

        if (_revocations.IsTicketRevoked(claims.TicketId))
        {
            return AccessDenialReason.TicketRevoked;
        }

        if (_revocations.IsDeviceRevoked(claims.DeviceId))
        {
            return AccessDenialReason.DeviceRevoked;
        }

        if (_revocations.IsMembershipRevoked(claims.MembershipId))
        {
            return AccessDenialReason.MembershipRevoked;
        }

        if (_revocations.IsBelowAuthorizationFloor(claims.MembershipId, claims.AuthorizationVersion))
        {
            return AccessDenialReason.AuthorizationVersionRevoked;
        }

        server = entry;
        return AccessDenialReason.None;
    }
}

/// <summary>
/// The outcome of <see cref="TicketVerifier.Verify"/>. <see cref="Reason"/> is for host logs;
/// only <see cref="AccessDenial.ExternalMessage"/> may be shown to the remote party.
/// </summary>
public sealed class TicketVerificationResult
{
    private TicketVerificationResult(AccessDenialReason reason, TicketClaims? claims, ConnectServerEntry? server)
    {
        Reason = reason;
        Claims = claims;
        Server = server;
    }

    public bool IsValid => Reason == AccessDenialReason.None;

    public AccessDenialReason Reason { get; }

    /// <summary>Set only when the ticket is valid.</summary>
    public TicketClaims? Claims { get; }

    /// <summary>The catalog entry the ticket was checked against. Set only when valid.</summary>
    public ConnectServerEntry? Server { get; }

    internal static TicketVerificationResult Allow(TicketClaims claims, ConnectServerEntry server) =>
        new(AccessDenialReason.None, claims, server);

    internal static TicketVerificationResult Deny(AccessDenialReason reason) => new(reason, null, null);
}
