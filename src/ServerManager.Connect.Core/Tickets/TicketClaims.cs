using System.Diagnostics.CodeAnalysis;
using ServerManager.Connect.Core.Crypto;
using ServerManager.Connect.Core.Json;

namespace ServerManager.Connect.Core.Tickets;

/// <summary>
/// The payload of a session ticket (contract §9). Parsing checks only types and formats. What
/// the values must equal (issuer, audience, time window, catalog, revocation) is decided by
/// <see cref="TicketVerifier"/>, and only after the signature has been verified.
/// </summary>
public sealed record TicketClaims
{
    private const int MaxIdentifierLength = 128;

    public required string Issuer { get; init; }

    /// <summary><c>aud</c>: the owner id (<c>own_…</c>) the ticket was issued for.</summary>
    public required string Audience { get; init; }

    /// <summary><c>jti</c>: 128-bit random id, base64url.</summary>
    public required string TicketId { get; init; }

    /// <summary><c>sub</c>: the friend's device id (<c>dev_…</c>).</summary>
    public required string DeviceId { get; init; }

    /// <summary><c>mid</c>: the membership id (opaque to the host).</summary>
    public required string MembershipId { get; init; }

    /// <summary><c>sid</c>: the ServerId; on the wire always the lowercase "D" GUID form.</summary>
    public required Guid ServerId { get; init; }

    public required string Protocol { get; init; }

    /// <summary><c>nid</c>: the Tailscale StableNodeID the ticket is bound to.</summary>
    public required string NodeId { get; init; }

    /// <summary><c>skp</c>: base64url SPKI of the session public key that signs connection proofs.</summary>
    public required string SessionPublicKey { get; init; }

    /// <summary><c>hb</c>: the host bridge address. Used by the friend transport; the host never dials it.</summary>
    public required string HostBridge { get; init; }

    /// <summary><c>av</c>: the membership authorization version.</summary>
    public required long AuthorizationVersion { get; init; }

    public required long IssuedAt { get; init; }

    public required long NotBefore { get; init; }

    public required long ExpiresAt { get; init; }

    /// <summary>The <c>sid</c> claim exactly as it appears on the wire and in the proof input.</summary>
    public string ServerIdText => ServerId.ToString("D");

    internal static bool TryParse(ReadOnlyMemory<byte> payload, [NotNullWhen(true)] out TicketClaims? claims)
    {
        claims = null;
        if (!StrictJsonObject.TryParse(payload, out var json) ||
            !json.TryGetString("iss", out var issuer) ||
            !json.TryGetString("aud", out var audience) ||
            !json.TryGetString("jti", out var ticketId) ||
            !json.TryGetString("sub", out var deviceId) ||
            !json.TryGetString("mid", out var membershipId) ||
            !json.TryGetString("sid", out var serverId) ||
            !json.TryGetString("proto", out var protocol) ||
            !json.TryGetString("nid", out var nodeId) ||
            !json.TryGetString("skp", out var sessionKey) ||
            !json.TryGetString("hb", out var hostBridge) ||
            !json.TryGetInt64("av", out var authorizationVersion) ||
            !json.TryGetInt64("iat", out var issuedAt) ||
            !json.TryGetInt64("nbf", out var notBefore) ||
            !json.TryGetInt64("exp", out var expiresAt))
        {
            return false;
        }

        if (!Base64Url.IsEncodingOfLength(ticketId, 16) ||
            !ConnectKeyIds.IsDeviceId(deviceId) ||
            !TryParseServerId(serverId, out var parsedServerId) ||
            !IsIdentifier(membershipId) ||
            !IsIdentifier(nodeId) ||
            !IsIdentifier(hostBridge) ||
            protocol.Length == 0 ||
            !Base64Url.TryDecode(sessionKey, out var sessionKeyBytes) ||
            !Es256.IsValidPublicKey(sessionKeyBytes) ||
            authorizationVersion < 0 ||
            issuedAt <= 0 ||
            notBefore <= 0 ||
            expiresAt <= 0)
        {
            return false;
        }

        claims = new TicketClaims
        {
            Issuer = issuer,
            Audience = audience,
            TicketId = ticketId,
            DeviceId = deviceId,
            MembershipId = membershipId,
            ServerId = parsedServerId,
            Protocol = protocol,
            NodeId = nodeId,
            SessionPublicKey = sessionKey,
            HostBridge = hostBridge,
            AuthorizationVersion = authorizationVersion,
            IssuedAt = issuedAt,
            NotBefore = notBefore,
            ExpiresAt = expiresAt
        };
        return true;
    }

    /// <summary>
    /// Only the canonical lowercase form is accepted. The proof signing input embeds the claim
    /// text, so two spellings of the same GUID must not both verify.
    /// </summary>
    private static bool TryParseServerId(string value, out Guid serverId) =>
        Guid.TryParseExact(value, "D", out serverId) &&
        string.Equals(serverId.ToString("D"), value, StringComparison.Ordinal);

    private static bool IsIdentifier(string value) =>
        value.Length is > 0 and <= MaxIdentifierLength && !value.Any(char.IsControl);
}
