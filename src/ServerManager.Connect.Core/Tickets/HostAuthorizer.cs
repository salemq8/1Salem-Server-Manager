using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using ServerManager.Connect.Core.Crypto;
using ServerManager.Connect.Core.Json;

namespace ServerManager.Connect.Core.Tickets;

/// <summary>
/// The Agent's decision for one bridged connection (contract §9, §10, §11 <c>authorize</c>).
/// It verifies the ticket, then the connection proof within ±60 s, then records the nonce, which
/// also counts the connection against the ticket's limit (<see cref="ReplayCache"/>).
/// On success it returns <c>127.0.0.1:&lt;catalog port&gt;</c>.
/// The endpoint comes only from the Agent's own catalog entry for the ticket's ServerId. The
/// request has no field for an address, and the address is always built from
/// <see cref="IPAddress.Loopback"/>, so neither the friend nor the host transport can steer a
/// connection anywhere else.
/// </summary>
public sealed class HostAuthorizer
{
    public static readonly TimeSpan ProofClockWindow = TimeSpan.FromSeconds(60);

    private readonly TicketVerifier _verifier;
    private readonly ReplayCache _replayCache;

    public HostAuthorizer(TicketVerifier verifier, ReplayCache replayCache)
    {
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _replayCache = replayCache ?? throw new ArgumentNullException(nameof(replayCache));
    }

    public HostAuthorizationResult Authorize(HostAuthorizationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var preamble = request.Preamble;
        var ticket = _verifier.Verify(preamble.Ticket, request.PeerNodeId);
        if (!ticket.IsValid)
        {
            return HostAuthorizationResult.Deny(ticket.Reason);
        }

        var claims = ticket.Claims!;
        var now = _verifier.Clock.GetUtcNow();
        var window = (long)ProofClockWindow.TotalSeconds;
        if (Math.Abs(now.ToUnixTimeSeconds() - preamble.Timestamp) > window)
        {
            return HostAuthorizationResult.Deny(AccessDenialReason.ProofOutsideClockWindow);
        }

        if (!ConnectionProof.Verify(
                Base64Url.Decode(claims.SessionPublicKey),
                claims.TicketId,
                preamble.Nonce,
                preamble.Timestamp,
                claims.ServerIdText,
                preamble.Proof))
        {
            return HostAuthorizationResult.Deny(AccessDenialReason.InvalidProof);
        }

        // Recorded last, so only fully valid connections occupy the cache and an attacker
        // cannot burn a victim's nonce with a forged proof.
        switch (_replayCache.TryRegister(claims, preamble.Nonce))
        {
            case ReplayCheckResult.Replayed:
                return HostAuthorizationResult.Deny(AccessDenialReason.ReplayedNonce);
            case ReplayCheckResult.TicketLimitReached:
                return HostAuthorizationResult.Deny(AccessDenialReason.TicketConnectionLimitReached);
            case ReplayCheckResult.CapacityExceeded:
                return HostAuthorizationResult.Deny(AccessDenialReason.ReplayCacheFull);
        }

        var endpoint = new IPEndPoint(IPAddress.Loopback, ticket.Server!.LocalPort);
        var connectionId = Base64Url.Encode(RandomNumberGenerator.GetBytes(16));
        return HostAuthorizationResult.Allow(endpoint, connectionId, claims);
    }
}

/// <summary>
/// The input of <see cref="HostAuthorizer.Authorize"/>: the preamble the friend sent and the
/// node id WhoIs reported for the connection. There is deliberately no destination and no peer
/// address. The pipe request's <c>peer.addr</c> is for logs only and is not read.
/// </summary>
public sealed record HostAuthorizationRequest
{
    public HostAuthorizationRequest(ConnectionPreamble preamble, string peerNodeId)
    {
        Preamble = preamble ?? throw new ArgumentNullException(nameof(preamble));
        ArgumentException.ThrowIfNullOrEmpty(peerNodeId);
        PeerNodeId = peerNodeId;
    }

    public ConnectionPreamble Preamble { get; }

    public string PeerNodeId { get; }

    /// <summary>
    /// Reads the host pipe's <c>authorize</c> request
    /// <c>{"preamble":{t,n,ts,p},"peer":{"nodeId":…,"addr":…}}</c>. Only <c>preamble</c> and
    /// <c>peer.nodeId</c> are read. Every other member, including anything that looks like an
    /// address, host or port, is ignored.
    /// </summary>
    public static HostAuthorizationRequest Parse(JsonElement request)
    {
        if (!StrictJsonObject.TryCreate(request, out var json) ||
            !json.TryGetObject("preamble", out var preambleJson) ||
            !ConnectionPreamble.TryCreate(preambleJson, out var preamble) ||
            !json.TryGetObject("peer", out var peer) ||
            !peer.TryGetString("nodeId", out var nodeId) ||
            nodeId.Length == 0)
        {
            throw new FormatException("The authorize request needs a {t,n,ts,p} preamble and a peer nodeId.");
        }

        return new HostAuthorizationRequest(preamble, nodeId);
    }
}

public sealed class HostAuthorizationResult
{
    private HostAuthorizationResult(
        AccessDenialReason reason,
        IPEndPoint? endpoint,
        string? connectionId,
        TicketClaims? claims)
    {
        Reason = reason;
        Endpoint = endpoint;
        ConnectionId = connectionId;
        Claims = claims;
    }

    public bool IsAllowed => Reason == AccessDenialReason.None;

    /// <summary>For host logs only. The friend receives <see cref="PreambleCodec.Refused"/>.</summary>
    public AccessDenialReason Reason { get; }

    /// <summary>Always 127.0.0.1 and the catalog port when allowed; null when denied.</summary>
    public IPEndPoint? Endpoint { get; }

    /// <summary>
    /// Random id the Agent hands to the host transport, so a revocation can close this
    /// connection later.
    /// </summary>
    public string? ConnectionId { get; }

    /// <summary>
    /// The verified claims, so the Agent can map a later revocation (device, membership, ticket)
    /// to this connection.
    /// </summary>
    public TicketClaims? Claims { get; }

    internal static HostAuthorizationResult Allow(IPEndPoint endpoint, string connectionId, TicketClaims claims) =>
        new(AccessDenialReason.None, endpoint, connectionId, claims);

    internal static HostAuthorizationResult Deny(AccessDenialReason reason) => new(reason, null, null, null);
}
