using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ServerManager.Connect.Core.Diagnostics;
using ServerManager.Connect.Core.Enrollment;

namespace ServerManager.Infrastructure.Connect;

/// <summary>
/// <see cref="IConnectProvisioner"/> over the Tailscale API, using the owner's OAuth client
/// (contract §7, §12). These are the only requests it can make, all to api.tailscale.com:
/// <list type="bullet">
/// <item><c>POST /api/v2/oauth/token</c>: client-credentials grant; the token is cached until
/// five minutes before it expires (tokens last one hour).</item>
/// <item><c>POST /api/v2/tailnet/-/keys</c>: one friend key, exactly
/// <c>{"capabilities":{"devices":{"create":{"reusable":false,"ephemeral":false,"preauthorized":true,"tags":["tag:1salem-client"]}}},"expirySeconds":86400,"description":…}</c>.</item>
/// <item><c>DELETE /api/v2/tailnet/-/keys/{keyId}</c>, <c>GET</c> and
/// <c>DELETE /api/v2/device/{nodeId}</c>.</item>
/// </list>
/// The API host is fixed; there is no base-URL option that could send the credential anywhere
/// else. Ids are restricted to letters and digits before they go into a path. Without an
/// explicitly configured credential every call throws <see cref="ConnectNotConfiguredException"/>
/// before anything is sent. The caller owns the <see cref="HttpClient"/> and should give it a
/// handler that does not follow redirects.
/// </summary>
public sealed class TailscaleApiProvisioner : IConnectProvisioner
{
    public const string FriendTag = "tag:1salem-client";
    public const long FriendKeyExpirySeconds = 86_400;
    public const int MaxDescriptionLength = 50;

    private const string DescriptionPrefix = "1salem connect ";
    private const int MembershipPrefixLength = 16;
    private const int MaxIdLength = 64;
    private const int MaxLoggedBodyLength = 300;

    private static readonly Uri ApiBase = new("https://api.tailscale.com/api/v2/");
    private static readonly TimeSpan TokenRefreshMargin = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DefaultTokenLifetime = TimeSpan.FromHours(1);

    private readonly HttpClient _http;
    private readonly TailscaleOAuthCredential? _credential;
    private readonly TimeProvider _clock;
    private readonly ILogger<TailscaleApiProvisioner> _logger;
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private string? _accessToken;
    private DateTimeOffset _accessTokenExpiresAt;

    /// <param name="credential">The owner's OAuth client, or null when none has been set up.</param>
    public TailscaleApiProvisioner(
        HttpClient http,
        TailscaleOAuthCredential? credential,
        TimeProvider clock,
        ILogger<TailscaleApiProvisioner> logger)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _credential = credential;
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<EnrollmentSecret> CreateFriendAuthKeyAsync(string membershipId, CancellationToken cancellationToken)
    {
        var credential = RequireCredential();
        var body = BuildFriendKeyRequest(membershipId);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(ApiBase, "tailnet/-/keys"))
        {
            Content = JsonContent(body)
        };
        const string operation = "create a friend auth key";
        using var response = await SendAsync(credential, request, operation, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, operation, cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync(response, operation, ReadFriendKey, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> DeleteAuthKeyAsync(string keyId, CancellationToken cancellationToken)
    {
        var credential = RequireCredential();
        RequireId(keyId, nameof(keyId));
        using var request = new HttpRequestMessage(HttpMethod.Delete, new Uri(ApiBase, "tailnet/-/keys/" + keyId));
        const string operation = "delete an auth key";
        using var response = await SendAsync(credential, request, operation, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        await EnsureSuccessAsync(response, operation, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<ConnectTailnetDevice?> GetDeviceAsync(string nodeId, CancellationToken cancellationToken)
    {
        var credential = RequireCredential();
        RequireId(nodeId, nameof(nodeId));
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(ApiBase, "device/" + nodeId));
        const string operation = "read a device";
        using var response = await SendAsync(credential, request, operation, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, operation, cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync(response, operation, ReadDevice, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> DeleteDeviceAsync(string nodeId, CancellationToken cancellationToken)
    {
        var credential = RequireCredential();
        RequireId(nodeId, nameof(nodeId));
        using var request = new HttpRequestMessage(HttpMethod.Delete, new Uri(ApiBase, "device/" + nodeId));
        const string operation = "delete a device";
        using var response = await SendAsync(credential, request, operation, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        await EnsureSuccessAsync(response, operation, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// "1salem connect " and up to 16 characters of the membership id, with anything but ASCII
    /// letters, digits and hyphens turned into hyphens. The description is visible in the
    /// owner's admin console, so it names what created the key without saying anything more.
    /// </summary>
    internal static string FriendKeyDescription(string membershipId)
    {
        ArgumentException.ThrowIfNullOrEmpty(membershipId);
        var prefix = new string(membershipId
            .Take(MembershipPrefixLength)
            .Select(character => char.IsAsciiLetterOrDigit(character) ? character : '-')
            .ToArray());
        return DescriptionPrefix + prefix;
    }

    internal static byte[] BuildFriendKeyRequest(string membershipId)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("capabilities");
            writer.WriteStartObject("devices");
            writer.WriteStartObject("create");
            writer.WriteBoolean("reusable", false);
            writer.WriteBoolean("ephemeral", false);
            writer.WriteBoolean("preauthorized", true);
            writer.WriteStartArray("tags");
            writer.WriteStringValue(FriendTag);
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteNumber("expirySeconds", FriendKeyExpirySeconds);
            writer.WriteString("description", FriendKeyDescription(membershipId));
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private TailscaleOAuthCredential RequireCredential() => _credential ?? throw new ConnectNotConfiguredException();

    private static void RequireId(string value, string name)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxIdLength || !value.All(char.IsAsciiLetterOrDigit))
        {
            throw new ArgumentException("Expected a Tailscale id of ASCII letters and digits.", name);
        }
    }

    private static ByteArrayContent JsonContent(byte[] body)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }

    private async Task<HttpResponseMessage> SendAsync(
        TailscaleOAuthCredential credential,
        HttpRequestMessage request,
        string operation,
        CancellationToken cancellationToken)
    {
        var token = await GetAccessTokenAsync(credential, cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await SendRawAsync(request, operation, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            // A revoked client or an expired token: the next call mints a fresh one.
            await InvalidateTokenAsync(token).ConfigureAwait(false);
        }

        return response;
    }

    private async Task<HttpResponseMessage> SendRawAsync(HttpRequestMessage request, string operation, CancellationToken cancellationToken)
    {
        try
        {
            return await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            _logger.LogWarning(
                "1Salem Connect could not reach the tailnet API to {Operation}: {Error}",
                operation,
                SecretRedactor.Redact(exception.Message));
            throw new ConnectProvisioningException($"Could not reach the tailnet API to {operation}.");
        }
    }

    private async Task<string> GetAccessTokenAsync(TailscaleOAuthCredential credential, CancellationToken cancellationToken)
    {
        await _tokenGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_accessToken is not null && _clock.GetUtcNow() < _accessTokenExpiresAt - TokenRefreshMargin)
            {
                return _accessToken;
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(ApiBase, "oauth/token"))
            {
                Content = new FormUrlEncodedContent(
                [
                    new KeyValuePair<string, string>("grant_type", "client_credentials"),
                    new KeyValuePair<string, string>("client_id", credential.ClientId),
                    new KeyValuePair<string, string>("client_secret", credential.ClientSecret)
                ])
            };
            const string operation = "obtain an access token";
            using var response = await SendRawAsync(request, operation, cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(response, operation, cancellationToken).ConfigureAwait(false);
            var issued = await ReadJsonAsync(response, operation, ReadAccessToken, cancellationToken).ConfigureAwait(false);
            _accessToken = issued.Token;
            _accessTokenExpiresAt = _clock.GetUtcNow() + issued.Lifetime;
            return issued.Token;
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    private async Task InvalidateTokenAsync(string rejectedToken)
    {
        await _tokenGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (string.Equals(_accessToken, rejectedToken, StringComparison.Ordinal))
            {
                _accessToken = null;
            }
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    /// <summary>
    /// A failure is logged with the status and a short, redacted excerpt of the body, which is
    /// where Tailscale explains what it refused. The exception carries the status only.
    /// </summary>
    private async Task EnsureSuccessAsync(HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var excerpt = SecretRedactor.Redact(body.Length > MaxLoggedBodyLength ? body[..MaxLoggedBodyLength] : body);
        _logger.LogWarning(
            "1Salem Connect: the tailnet API refused to {Operation} (HTTP {Status}): {Body}",
            operation,
            (int)response.StatusCode,
            excerpt);
        throw new ConnectProvisioningException(
            $"The tailnet API refused to {operation} (HTTP {(int)response.StatusCode}).");
    }

    /// <summary>
    /// Parses the body and hands its root object to <paramref name="read"/>, which returns null
    /// when a required member is missing. The body may hold a token or an auth key, so its bytes
    /// are zeroed afterwards. <see cref="JsonDocument"/> reads straight from that buffer rather
    /// than copying it, which is why <paramref name="read"/> runs here, before the buffer is
    /// cleared, instead of the document being returned.
    /// </summary>
    private static async Task<T> ReadJsonAsync<T>(
        HttpResponseMessage response,
        string operation,
        Func<JsonElement, T?> read,
        CancellationToken cancellationToken)
        where T : class
    {
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                return read(document.RootElement) ??
                    throw new ConnectProvisioningException($"The tailnet API returned an unexpected response while trying to {operation}.");
            }
        }
        catch (JsonException)
        {
        }
        finally
        {
            Array.Clear(bytes);
        }

        throw new ConnectProvisioningException($"The tailnet API returned an unreadable response while trying to {operation}.");
    }

    private static AccessToken? ReadAccessToken(JsonElement root)
    {
        if (!TryGetString(root, "access_token", out var token))
        {
            return null;
        }

        var lifetime = root.TryGetProperty("expires_in", out var expiresIn) &&
            expiresIn.ValueKind == JsonValueKind.Number &&
            expiresIn.TryGetInt32(out var seconds) &&
            seconds > 0
                ? TimeSpan.FromSeconds(seconds)
                : DefaultTokenLifetime;
        return new AccessToken(token, lifetime);
    }

    private static EnrollmentSecret? ReadFriendKey(JsonElement root)
    {
        if (!TryGetString(root, "id", out var keyId) || !TryGetString(root, "key", out var authKey))
        {
            return null;
        }

        try
        {
            return new EnrollmentSecret(authKey, keyId);
        }
        catch (ArgumentException)
        {
            throw new ConnectProvisioningException("The tailnet API returned something other than a one-off auth key.");
        }
    }

    private static ConnectTailnetDevice? ReadDevice(JsonElement root)
    {
        if (!TryGetString(root, "nodeId", out var nodeId))
        {
            return null;
        }

        var tags = root.TryGetProperty("tags", out var tagArray) && tagArray.ValueKind == JsonValueKind.Array
            ? tagArray.EnumerateArray()
                .Where(tag => tag.ValueKind == JsonValueKind.String)
                .Select(tag => tag.GetString()!)
                .ToArray()
            : [];
        DateTimeOffset? createdAt = root.TryGetProperty("created", out var created) &&
            created.ValueKind == JsonValueKind.String &&
            created.TryGetDateTimeOffset(out var parsedCreated)
                ? parsedCreated
                : null;
        return new ConnectTailnetDevice(nodeId, tags, createdAt);
    }

    /// <summary>A freshly issued access token. Its <see cref="ToString"/> never prints the token.</summary>
    private sealed record AccessToken(string Token, TimeSpan Lifetime)
    {
        public override string ToString() => $"AccessToken {{ Lifetime = {Lifetime} }}";
    }

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.String ||
            property.GetString() is not { Length: > 0 } text)
        {
            return false;
        }

        value = text;
        return true;
    }
}
