using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using ServerManager.Connect.Core.Crypto;
using ServerManager.Connect.Core.Json;

namespace ServerManager.Connect.Core.Tickets;

/// <summary>
/// The JSON body of the connection preamble (contract §10):
/// <c>{"t":ticket,"n":nonce,"ts":seconds,"p":proof}</c>.
/// The type has no destination field of any kind. Members other than t/n/ts/p are dropped
/// while parsing, so an address smuggled into the preamble or the pipe request cannot reach
/// the authorizer at all.
/// </summary>
public sealed record ConnectionPreamble
{
    public const int NonceLength = 16;

    public ConnectionPreamble(string ticket, string nonce, long timestamp, string proof)
    {
        if (string.IsNullOrEmpty(ticket) || ticket.Length > CompactTicket.MaxLength)
        {
            throw new ArgumentException("The ticket is missing or too long.", nameof(ticket));
        }

        if (!Base64Url.IsEncodingOfLength(nonce, NonceLength))
        {
            throw new ArgumentException("The nonce must be 16 bytes of base64url.", nameof(nonce));
        }

        if (timestamp <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timestamp), "The timestamp must be positive unix seconds.");
        }

        if (!Base64Url.IsEncodingOfLength(proof, Es256.SignatureLength))
        {
            throw new ArgumentException("The proof must be a 64-byte ES256 signature in base64url.", nameof(proof));
        }

        Ticket = ticket;
        Nonce = nonce;
        Timestamp = timestamp;
        Proof = proof;
    }

    public string Ticket { get; }

    public string Nonce { get; }

    public long Timestamp { get; }

    public string Proof { get; }

    /// <summary>
    /// Builds a preamble for a ticket this process was issued (friend side, proof harness).
    /// It reads <c>jti</c> and <c>sid</c> from the ticket without verifying it. That is
    /// acceptable only because the caller is proving possession of its own session key; every
    /// trust decision happens on the host, which verifies everything.
    /// </summary>
    public static ConnectionPreamble Create(string ticket, ECDsa sessionKey, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(sessionKey);
        ArgumentNullException.ThrowIfNull(clock);
        if (!CompactTicket.TryParse(ticket, out var token) || !TicketClaims.TryParse(token.Payload, out var claims))
        {
            throw new FormatException("The ticket is not a well-formed session ticket.");
        }

        var nonce = Base64Url.Encode(RandomNumberGenerator.GetBytes(NonceLength));
        var timestamp = clock.GetUtcNow().ToUnixTimeSeconds();
        var proof = ConnectionProof.Sign(sessionKey, claims.TicketId, nonce, timestamp, claims.ServerIdText);
        return new ConnectionPreamble(ticket, nonce, timestamp, proof);
    }

    /// <summary>Reads the <c>{t,n,ts,p}</c> object, e.g. from the host pipe's <c>authorize</c> request.</summary>
    public static ConnectionPreamble FromJson(JsonElement element) =>
        StrictJsonObject.TryCreate(element, out var json) && TryCreate(json, out var preamble)
            ? preamble
            : throw new FormatException("The preamble is not a valid {t,n,ts,p} object.");

    internal static bool TryCreate(StrictJsonObject json, [NotNullWhen(true)] out ConnectionPreamble? preamble)
    {
        preamble = null;
        if (!json.TryGetString("t", out var ticket) ||
            !json.TryGetString("n", out var nonce) ||
            !json.TryGetInt64("ts", out var timestamp) ||
            !json.TryGetString("p", out var proof))
        {
            return false;
        }

        try
        {
            preamble = new ConnectionPreamble(ticket, nonce, timestamp, proof);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    internal void WriteTo(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteString("t", Ticket);
        writer.WriteString("n", Nonce);
        writer.WriteNumber("ts", Timestamp);
        writer.WriteString("p", Proof);
        writer.WriteEndObject();
    }
}
