using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ServerManager.Connect.Core.Crypto;
using ServerManager.Connect.Core.Identity;

namespace ServerManager.Connect.Core.Broker;

/// <summary>
/// Signs owner and device requests to the broker (contract §5). The canonical string is UTF-8,
/// with lines joined by a single <c>\n</c> and no trailing newline:
/// <code>
/// 1SALEM-REQ-V1
/// {METHOD}
/// {PATH}            path only, no query string
/// {X-1S-Time}
/// {X-1S-Nonce}
/// {base64url(SHA-256(raw body bytes))}
/// </code>
/// The query string is not signed, so it must never carry anything that needs protecting. The
/// only query parameter in v1 is <c>since</c> on the revocation feed.
/// </summary>
public sealed class SignedRequestSigner
{
    public const string Version = "1SALEM-REQ-V1";
    public const string KeyHeader = "X-1S-Key";
    public const string TimeHeader = "X-1S-Time";
    public const string NonceHeader = "X-1S-Nonce";
    public const string SignatureHeader = "X-1S-Sig";

    private const int NonceLength = 16;

    private readonly ConnectIdentity _identity;
    private readonly TimeProvider _clock;

    public SignedRequestSigner(ConnectIdentity identity, TimeProvider clock)
    {
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public static string BuildCanonicalString(
        string method,
        string path,
        long unixSeconds,
        string nonce,
        ReadOnlySpan<byte> body)
    {
        ValidateMethod(method);
        ValidatePath(path);
        if (!Base64Url.IsEncodingOfLength(nonce, NonceLength))
        {
            throw new ArgumentException("The nonce must be 16 bytes of base64url.", nameof(nonce));
        }

        return string.Join(
            '\n',
            Version,
            method,
            path,
            unixSeconds.ToString(CultureInfo.InvariantCulture),
            nonce,
            Base64Url.Encode(SHA256.HashData(body)));
    }

    /// <summary>Signs a request with a fresh nonce and the current time.</summary>
    public SignedRequestHeaders Sign(string method, string path, ReadOnlySpan<byte> body)
    {
        var time = _clock.GetUtcNow().ToUnixTimeSeconds();
        var nonce = Base64Url.Encode(RandomNumberGenerator.GetBytes(NonceLength));
        var canonical = BuildCanonicalString(method, path, time, nonce, body);
        var signature = _identity.Sign(Encoding.UTF8.GetBytes(canonical));
        return new SignedRequestHeaders(
            _identity.KeyId,
            time.ToString(CultureInfo.InvariantCulture),
            nonce,
            Base64Url.Encode(signature));
    }

    /// <summary>
    /// Signs <paramref name="request"/> in place. The body hash is taken from the request's own
    /// content bytes, so the signature always covers exactly what is sent. The path is taken
    /// from the request URI, without its query.
    /// </summary>
    public async Task SignAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RequestUri is not { IsAbsoluteUri: true } uri)
        {
            throw new ArgumentException("Broker requests need an absolute URI.", nameof(request));
        }

        var body = request.Content is null
            ? []
            : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var headers = Sign(request.Method.Method, uri.AbsolutePath, body);
        foreach (var (name, value) in headers.ToHeaders())
        {
            request.Headers.Remove(name);
            request.Headers.TryAddWithoutValidation(name, value);
        }
    }

    private static void ValidateMethod(string method)
    {
        if (string.IsNullOrEmpty(method) || !method.All(character => character is >= 'A' and <= 'Z'))
        {
            throw new ArgumentException("The method must be an upper-case HTTP method such as POST.", nameof(method));
        }
    }

    private static void ValidatePath(string path)
    {
        if (string.IsNullOrEmpty(path) ||
            path[0] != '/' ||
            path.IndexOfAny(['?', '#']) >= 0 ||
            path.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
        {
            throw new ArgumentException("The path must start with '/' and contain no query, fragment or whitespace.", nameof(path));
        }
    }
}

public sealed record SignedRequestHeaders(string KeyId, string Time, string Nonce, string Signature)
{
    public IEnumerable<KeyValuePair<string, string>> ToHeaders()
    {
        yield return new(SignedRequestSigner.KeyHeader, KeyId);
        yield return new(SignedRequestSigner.TimeHeader, Time);
        yield return new(SignedRequestSigner.NonceHeader, Nonce);
        yield return new(SignedRequestSigner.SignatureHeader, Signature);
    }
}
