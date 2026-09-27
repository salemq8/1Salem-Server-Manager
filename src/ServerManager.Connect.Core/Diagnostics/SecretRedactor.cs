using System.Text.RegularExpressions;

namespace ServerManager.Connect.Core.Diagnostics;

/// <summary>
/// Scrubs Connect secrets from text before it is logged, shown in Diagnostics or copied to a
/// support report. It covers Tailscale keys (<c>tskey-auth-</c>, <c>tskey-client-</c>,
/// <c>tskey-api-</c>…), compact JWS tickets, invite secrets (including the URL-fragment form),
/// <c>Authorization</c> and <c>X-1S-Sig</c> header values, and private keys as PEM, base64
/// PKCS#8/SEC1 DER, or JWK <c>"d"</c>.
/// Redaction is best-effort defence in depth. Code must still not log secrets on purpose.
/// Every pattern runs on the non-backtracking engine, so hostile log text cannot make
/// redaction slow.
/// </summary>
public static class SecretRedactor
{
    public const string Marker = "[REDACTED]";

    private const RegexOptions Exact = RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
    private const RegexOptions AnyCase = Exact | RegexOptions.IgnoreCase;

    private static readonly Regex PemPrivateKey = new(
        @"-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z0-9 ]*PRIVATE KEY-----",
        Exact);

    // A truncated PEM block (the END line cut off by a log limit) is redacted to the end.
    private static readonly Regex PemPrivateKeyUnterminated = new(
        @"-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----[\s\S]*",
        Exact);

    // Base64 or base64url DER: PKCS#8 (SEQUENCE, INTEGER 0, SEQUENCE AlgorithmIdentifier) and
    // SEC1 "EC PRIVATE KEY" (SEQUENCE, INTEGER 1, OCTET STRING 32). Public SPKI never matches.
    private static readonly Regex DerPrivateKey = new(
        @"\b(?:MI[A-Za-z0-9+/_\-]{2,4}AgEAM|MHcCAQEE)[A-Za-z0-9+/_\-]+=*",
        Exact);

    private static readonly Regex CompactJws = new(
        @"\bey[A-Za-z0-9_\-]+\.ey[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]*",
        Exact);

    private static readonly Regex TailscaleKey = new(
        @"\btskey-(?:(auth|client|api)-)?[A-Za-z0-9_\-]+",
        AnyCase);

    private static readonly Regex AuthorizationHeader = new(
        @"((?:Proxy-)?Authorization\\?""?\s*[:=]\s*\\?""?)[^\r\n"",;\\]+",
        AnyCase);

    private static readonly Regex SignatureHeader = new(
        @"(X-1S-Sig\\?""?\s*[:=]\s*\\?""?)[A-Za-z0-9_\-]+",
        AnyCase);

    private static readonly Regex SensitiveJsonMember = new(
        @"(\\?""(?:secret|inviteSecret|authKey|auth_key|clientSecret|client_secret|sessionKey|session_key|privateKey|private_key|protectedKey|pkcs8|password|d)\\?""\s*:\s*\\?"")[^""\\]*",
        AnyCase);

    private static readonly Regex SensitiveQueryValue = new(
        @"\b((?:secret|authKey|clientSecret|client_secret|sessionKey|privateKey|password)=)[^&\s"",;]+",
        AnyCase);

    // Invite links carry the secret in the fragment (https://connect.1salem.app/i#<secret>);
    // any other URL fragment that looks like a token is treated the same way.
    private static readonly Regex InviteFragment = new(
        @"(/i#)[A-Za-z0-9_\-]+",
        Exact);

    private static readonly Regex TokenFragment = new(
        @"(https?://[^\s#]+#)[A-Za-z0-9_\-]{20,}",
        AnyCase);

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        var result = PemPrivateKey.Replace(text, "[REDACTED PRIVATE KEY]");
        result = PemPrivateKeyUnterminated.Replace(result, "[REDACTED PRIVATE KEY]");
        result = DerPrivateKey.Replace(result, "[REDACTED PRIVATE KEY]");
        result = CompactJws.Replace(result, "[REDACTED TICKET]");
        result = TailscaleKey.Replace(result, match => match.Groups[1].Success
            ? $"tskey-{match.Groups[1].Value}-{Marker}"
            : $"tskey-{Marker}");
        result = AuthorizationHeader.Replace(result, "$1" + Marker);
        result = SignatureHeader.Replace(result, "$1" + Marker);
        result = SensitiveJsonMember.Replace(result, "$1" + Marker);
        result = SensitiveQueryValue.Replace(result, "$1" + Marker);
        result = InviteFragment.Replace(result, "$1" + Marker);
        return TokenFragment.Replace(result, "$1" + Marker);
    }
}
