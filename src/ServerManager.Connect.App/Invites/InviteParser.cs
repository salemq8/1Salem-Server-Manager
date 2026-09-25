using System.Diagnostics.CodeAnalysis;
using ServerManager.Connect.Core.Crypto;

namespace ServerManager.Connect.App.Invites;

/// <summary>
/// Accepts what a friend pastes: the link <c>https://connect.1salem.app/i#&lt;secret&gt;</c> or the
/// bare code (contract §6). Either way the result is the 32-byte secret as canonical base64url.
/// A link must carry the secret in its fragment, the part a browser never sends to a server; a
/// secret anywhere else in a link means the link is not one of ours, and it is refused rather
/// than guessed at. The host of a link does not matter: the secret only ever goes to the
/// configured broker.
/// </summary>
public static class InviteParser
{
    public const int SecretBytes = 32;

    private const int MaxInputLength = 2048;

    public static bool TryParse(string? input, [NotNullWhen(true)] out string? secret)
    {
        secret = null;
        var text = input?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > MaxInputLength)
        {
            return false;
        }

        var candidate = text;
        if (text.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(text, UriKind.Absolute, out var link) ||
                (link.Scheme != Uri.UriSchemeHttps && link.Scheme != Uri.UriSchemeHttp) ||
                link.Fragment.Length <= 1)
            {
                return false;
            }

            candidate = link.Fragment[1..];
        }

        if (!Base64Url.IsEncodingOfLength(candidate, SecretBytes))
        {
            return false;
        }

        secret = candidate;
        return true;
    }
}
