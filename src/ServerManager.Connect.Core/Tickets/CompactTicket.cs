using System.Diagnostics.CodeAnalysis;
using System.Text;
using ServerManager.Connect.Core.Crypto;
using ServerManager.Connect.Core.Json;

namespace ServerManager.Connect.Core.Tickets;

/// <summary>
/// The three segments of a compact JWS, decoded but not yet trusted.
/// </summary>
internal sealed class CompactTicket
{
    /// <summary>A ticket always travels inside a preamble, whose JSON is capped at 4096 bytes.</summary>
    public const int MaxLength = PreambleCodec.MaxJsonLength;

    public const string ExpectedType = "1salem-ticket+jwt";

    private CompactTicket(byte[] signingInput, byte[] header, byte[] payload, byte[] signature)
    {
        SigningInput = signingInput;
        Header = header;
        Payload = payload;
        Signature = signature;
    }

    /// <summary>ASCII bytes of <c>header.payload</c>, exactly as received.</summary>
    public byte[] SigningInput { get; }

    public byte[] Header { get; }

    public byte[] Payload { get; }

    public byte[] Signature { get; }

    /// <summary>
    /// Exact three-part shape: two dots, three non-empty canonical base64url segments, nothing
    /// else. An unsigned "alg":"none" token (empty third segment) fails here.
    /// </summary>
    public static bool TryParse(string? ticket, [NotNullWhen(true)] out CompactTicket? value)
    {
        value = null;
        if (string.IsNullOrEmpty(ticket) || ticket.Length > MaxLength)
        {
            return false;
        }

        var parts = ticket.Split('.');
        if (parts.Length != 3 ||
            parts.Any(part => part.Length == 0) ||
            !Base64Url.TryDecode(parts[0], out var header) ||
            !Base64Url.TryDecode(parts[1], out var payload) ||
            !Base64Url.TryDecode(parts[2], out var signature))
        {
            return false;
        }

        var signingInput = Encoding.ASCII.GetBytes(ticket[..(parts[0].Length + 1 + parts[1].Length)]);
        value = new CompactTicket(signingInput, header, payload, signature);
        return true;
    }
}

/// <summary>
/// The protected header. Exactly <c>alg</c>, <c>typ</c> and <c>kid</c> are allowed. Anything
/// else (<c>crit</c>, <c>jwk</c>, <c>jku</c>, <c>x5u</c>, <c>b64</c>…) would ask the verifier
/// to change behaviour, and the contract's header has none of it.
/// </summary>
internal sealed record TicketHeader(string Algorithm, string? Type, string? KeyId, bool HasOnlyKnownMembers)
{
    private static readonly HashSet<string> KnownMembers = new(StringComparer.Ordinal) { "alg", "typ", "kid" };

    public static bool TryParse(byte[] header, [NotNullWhen(true)] out TicketHeader? value)
    {
        value = null;
        if (!StrictJsonObject.TryParse(header, out var json) || !json.TryGetString("alg", out var algorithm))
        {
            return false;
        }

        // typ and kid are read leniently here so the verifier can report which check failed;
        // a missing or non-string value simply never matches.
        json.TryGetString("typ", out var type);
        json.TryGetString("kid", out var keyId);
        value = new TicketHeader(algorithm, type, keyId, json.Names.All(KnownMembers.Contains));
        return true;
    }
}
