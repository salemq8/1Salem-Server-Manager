using System.Diagnostics.CodeAnalysis;

namespace ServerManager.Connect.Core.Crypto;

/// <summary>
/// Unpadded base64url (RFC 4648 §5, as used by JWS), the only binary-to-text form on the Connect
/// wire: ticket segments, SPKI keys, nonces, signatures and ids.
/// Decoding is deliberately strict. Padding, whitespace, the standard-alphabet characters '+'
/// and '/', and non-canonical trailing bits are all rejected, so every byte string has exactly
/// one accepted spelling. Without that, one nonce or signature could be presented under several
/// spellings, and anything keyed on the text (replay caches, logs, audit) would see them as
/// different values.
/// </summary>
public static class Base64Url
{
    public static string Encode(ReadOnlySpan<byte> data)
    {
        var standard = Convert.ToBase64String(data);
        var length = standard.Length;
        while (length > 0 && standard[length - 1] == '=')
        {
            length--;
        }

        return string.Create(length, standard, static (destination, source) =>
        {
            for (var index = 0; index < destination.Length; index++)
            {
                destination[index] = source[index] switch
                {
                    '+' => '-',
                    '/' => '_',
                    var character => character
                };
            }
        });
    }

    public static byte[] Decode(string? value) =>
        TryDecode(value, out var bytes)
            ? bytes
            : throw new FormatException("The value is not canonical unpadded base64url.");

    public static bool TryDecode(string? value, [NotNullWhen(true)] out byte[]? bytes)
    {
        bytes = null;
        if (value is null || value.Length % 4 == 1)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!IsAlphabetCharacter(character))
            {
                return false;
            }
        }

        var padding = (4 - value.Length % 4) % 4;
        var standard = string.Create(value.Length + padding, value, static (destination, source) =>
        {
            for (var index = 0; index < destination.Length; index++)
            {
                destination[index] = index >= source.Length
                    ? '='
                    : source[index] switch
                    {
                        '-' => '+',
                        '_' => '/',
                        var character => character
                    };
            }
        });

        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(standard);
        }
        catch (FormatException)
        {
            return false;
        }

        // Convert ignores the unused low bits of the last character, so "AB" and "AC" can decode
        // to the same byte. Re-encoding rejects every spelling except the canonical one.
        if (!string.Equals(Encode(decoded), value, StringComparison.Ordinal))
        {
            return false;
        }

        bytes = decoded;
        return true;
    }

    /// <summary>
    /// True when <paramref name="value"/> is canonical base64url for exactly
    /// <paramref name="byteLength"/> bytes (for example a 16-byte nonce or ticket id).
    /// </summary>
    public static bool IsEncodingOfLength(string? value, int byteLength) =>
        TryDecode(value, out var bytes) && bytes.Length == byteLength;

    private static bool IsAlphabetCharacter(char character) =>
        character is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_';
}
