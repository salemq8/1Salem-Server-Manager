using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using ServerManager.Connect.Core.Crypto;
using ServerManager.Connect.Core.Json;

namespace ServerManager.Connect.Core.Tickets;

/// <summary>
/// One broker ticket-signing key as published by <c>GET /v1/keys</c>: <c>{kid, alg, spki}</c>.
/// The algorithm is pinned per key. Only ES256 keys can exist, so a ticket header can never
/// talk a verifier into a different algorithm for a known kid.
/// </summary>
public sealed class TicketSigningKey
{
    public const int MaxKeyIdLength = 128;

    private readonly byte[] _spki;

    public TicketSigningKey(string keyId, string algorithm, ReadOnlySpan<byte> spki)
    {
        if (string.IsNullOrEmpty(keyId) || keyId.Length > MaxKeyIdLength)
        {
            throw new ArgumentException("A ticket key id must be 1-128 characters.", nameof(keyId));
        }

        if (!string.Equals(algorithm, Es256.Algorithm, StringComparison.Ordinal))
        {
            throw new ArgumentException("Ticket signing keys must be ES256.", nameof(algorithm));
        }

        if (!Es256.IsValidPublicKey(spki))
        {
            throw new ArgumentException("A ticket signing key must be a P-256 SPKI public key.", nameof(spki));
        }

        KeyId = keyId;
        Algorithm = algorithm;
        _spki = spki.ToArray();
    }

    public string KeyId { get; }

    public string Algorithm { get; }

    public ReadOnlySpan<byte> Spki => _spki;
}

/// <summary>
/// The set of broker keys a verifier trusts, looked up by <c>kid</c>. Keys are pinned in
/// configuration; a ticket can never introduce a key of its own (no <c>jwk</c>, <c>jku</c> or
/// <c>x5u</c>).
/// </summary>
public sealed class TicketKeySet
{
    private readonly Dictionary<string, TicketSigningKey> _keys = new(StringComparer.Ordinal);

    public TicketKeySet(IEnumerable<TicketSigningKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        foreach (var key in keys)
        {
            if (!_keys.TryAdd(key.KeyId, key))
            {
                throw new ArgumentException($"Duplicate ticket key id '{key.KeyId}'.", nameof(keys));
            }
        }

        if (_keys.Count == 0)
        {
            throw new ArgumentException("At least one ticket signing key must be pinned.", nameof(keys));
        }
    }

    /// <summary>Parses the <c>{"keys":[{"kid","alg","spki"}]}</c> document of <c>GET /v1/keys</c>.</summary>
    public static TicketKeySet Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (!StrictJsonObject.TryParse(Encoding.UTF8.GetBytes(json), out var root) ||
            !root.TryGetArray("keys", out var entries))
        {
            throw new FormatException("A ticket key set must be an object with a 'keys' array.");
        }

        var keys = new List<TicketSigningKey>();
        foreach (var entry in entries)
        {
            if (!StrictJsonObject.TryCreate(entry, out var key) ||
                !key.TryGetString("kid", out var keyId) ||
                !key.TryGetString("alg", out var algorithm) ||
                !key.TryGetString("spki", out var spki) ||
                !Base64Url.TryDecode(spki, out var spkiBytes))
            {
                throw new FormatException("Each ticket key needs string 'kid', 'alg' and base64url 'spki'.");
            }

            try
            {
                keys.Add(new TicketSigningKey(keyId, algorithm, spkiBytes));
            }
            catch (ArgumentException exception)
            {
                throw new FormatException(exception.Message, exception);
            }
        }

        try
        {
            return new TicketKeySet(keys);
        }
        catch (ArgumentException exception)
        {
            throw new FormatException(exception.Message, exception);
        }
    }

    internal bool TryGet(string keyId, [NotNullWhen(true)] out TicketSigningKey? key) =>
        _keys.TryGetValue(keyId, out key);

    /// <summary>The same document shape as <c>GET /v1/keys</c>.</summary>
    public string ToJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("keys");
            foreach (var key in _keys.Values.OrderBy(key => key.KeyId, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("kid", key.KeyId);
                writer.WriteString("alg", key.Algorithm);
                writer.WriteString("spki", Base64Url.Encode(key.Spki));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
