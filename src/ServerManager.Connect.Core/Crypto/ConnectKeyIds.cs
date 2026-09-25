using System.Security.Cryptography;

namespace ServerManager.Connect.Core.Crypto;

/// <summary>
/// Owner and device ids (contract §5): prefix + the first 26 characters of the RFC 4648 base32
/// encoding (standard alphabet in lowercase, a-z 2-7, unpadded) of SHA-256 over the SPKI DER.
/// The broker accepts only the lowercase form, and ids end up in lowercase tailnet hostnames.
/// An id is derived from the public key, never chosen, so it cannot be claimed without the key
/// and registering the same key twice yields the same id. 26 characters carry 130 bits.
/// </summary>
public static class ConnectKeyIds
{
    public const string OwnerPrefix = "own_";
    public const string DevicePrefix = "dev_";
    public const int HashCharacters = 26;

    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz234567";

    public static string ForOwner(ReadOnlySpan<byte> spki) => OwnerPrefix + HashPart(spki);

    public static string ForDevice(ReadOnlySpan<byte> spki) => DevicePrefix + HashPart(spki);

    public static bool IsOwnerId(string? value) => HasShape(value, OwnerPrefix);

    public static bool IsDeviceId(string? value) => HasShape(value, DevicePrefix);

    private static string HashPart(ReadOnlySpan<byte> spki)
    {
        if (!Es256.IsValidPublicKey(spki))
        {
            throw new ArgumentException("Ids are derived only from P-256 SPKI public keys.", nameof(spki));
        }

        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(spki, digest);
        return Base32(digest)[..HashCharacters];
    }

    private static string Base32(ReadOnlySpan<byte> data)
    {
        var output = new char[(data.Length * 8 + 4) / 5];
        var buffer = 0;
        var bits = 0;
        var position = 0;
        foreach (var value in data)
        {
            // At most 4 unconsumed bits survive each round, so 16 bits always hold the window.
            buffer = ((buffer << 8) | value) & 0xFFFF;
            bits += 8;
            while (bits >= 5)
            {
                output[position++] = Alphabet[(buffer >> (bits - 5)) & 31];
                bits -= 5;
            }
        }

        if (bits > 0)
        {
            output[position] = Alphabet[(buffer << (5 - bits)) & 31];
        }

        return new string(output);
    }

    private static bool HasShape(string? value, string prefix)
    {
        if (value is null ||
            value.Length != prefix.Length + HashCharacters ||
            !value.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var character in value.AsSpan(prefix.Length))
        {
            if (!Alphabet.Contains(character))
            {
                return false;
            }
        }

        return true;
    }
}
