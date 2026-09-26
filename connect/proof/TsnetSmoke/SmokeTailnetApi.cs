using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using ServerManager.Connect.Core.Diagnostics;
using ServerManager.Connect.Core.Enrollment;
using ServerManager.Infrastructure.Connect;

namespace TsnetSmoke;

/// <summary>
/// A device as <c>GET device/{nodeId}</c> and <c>GET tailnet/-/devices</c> report it, with the
/// default fields the cleanup and the Tailnet Lock check need. A field the API left out is null.
/// </summary>
internal sealed record SmokeDevice(
    string NodeId,
    string Hostname,
    IReadOnlyList<string> Tags,
    DateTimeOffset? Created,
    IReadOnlyList<string> Addresses,
    bool? IsEphemeral,
    bool? KeyExpiryDisabled,
    string? TailnetLockError,
    string? TailnetLockKey);

/// <summary>One answer of the API: its HTTP status, and the device when there is one (null on 404).</summary>
internal sealed record DeviceAnswer(int Status, SmokeDevice? Device);

/// <summary>
/// The Tailscale API calls this harness needs beyond the production
/// <see cref="TailscaleApiProvisioner"/>, which deliberately cannot make them: a key with the host
/// tag, a device's full default fields (addresses, isEphemeral, tailnetLockError), device and key
/// deletions that report their exact HTTP status (the cleanup records every status, because some
/// are undocumented), and the tailnet's devices filtered by tag (to show afterwards that nothing
/// of the run is left). Same fixed host and token endpoint as the provisioner, no base-URL option,
/// and the caller's HttpClient follows no redirects. Paths and field names are those of the
/// Tailscale API reference (https://tailscale.com/api): <c>POST tailnet/-/keys</c>
/// (<c>capabilities.devices.create</c>, <c>expirySeconds</c>, <c>description</c> → <c>id</c>,
/// <c>key</c>), <c>GET</c>/<c>DELETE device/{nodeId}</c>, <c>DELETE tailnet/-/keys/{keyId}</c> and
/// <c>GET tailnet/-/devices?tags=</c> (exact-match filter).
/// </summary>
internal sealed class SmokeTailnetApi
{
    /// <summary>
    /// One day, the shortest lifetime an auth key can have: Tailscale allows 1 to 90 days
    /// (https://tailscale.com/kb/1085/auth-keys, "Key expiry"). The key is used within minutes and
    /// deleted at the end of the run; this bounds it if the cleanup cannot run.
    /// </summary>
    public const long KeyExpirySeconds = 86_400;

    private const int MaxIdLength = 64;
    private const int MaxLoggedBodyLength = 300;

    private static readonly Uri ApiBase = new("https://api.tailscale.com/api/v2/");

    // Tokens last an hour; one is minted again well before that.
    private static readonly TimeSpan TokenReuse = TimeSpan.FromMinutes(50);

    private readonly HttpClient _http;
    private readonly SmokeCredential _credential;
    private string? _token;
    private DateTimeOffset _tokenIssuedAt;

    public SmokeTailnetApi(HttpClient http, SmokeCredential credential)
    {
        _http = http;
        _credential = credential;
    }

    /// <summary>
    /// A one-off, pre-authorized, non-ephemeral key with exactly <paramref name="tags"/>. The key
    /// id goes to <paramref name="minted"/> before the key itself is checked, so a key that exists
    /// but turns out unusable is still deleted by the cleanup.
    /// </summary>
    public async Task<EnrollmentSecret> CreateKeyAsync(IReadOnlyList<string> tags, string description, Action<string> minted)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            capabilities = new
            {
                devices = new { create = new { reusable = false, ephemeral = false, preauthorized = true, tags } },
            },
            expirySeconds = KeyExpirySeconds,
            description,
        });
        using var response = await SendAsync(HttpMethod.Post, "tailnet/-/keys", body, "create an auth key", notFoundIsAnswer: false);

        // EnrollmentSecret accepts a one-off tskey-auth- key only and never prints it.
        return await ReadAsync(response, root =>
        {
            var keyId = Text(root, "id");
            minted(keyId);
            return new EnrollmentSecret(Text(root, "key"), keyId);
        });
    }

    public async Task<DeviceAnswer> GetDeviceAsync(string nodeId)
    {
        RequireId(nodeId);
        using var response = await SendAsync(HttpMethod.Get, "device/" + nodeId, null, "read a device", notFoundIsAnswer: true);
        var status = (int)response.StatusCode;
        return response.StatusCode == HttpStatusCode.NotFound
            ? new DeviceAnswer(status, null)
            : new DeviceAnswer(status, await ReadAsync(response, ReadDevice));
    }

    /// <returns>The HTTP status: a success, or 404 when the device is already gone.</returns>
    public async Task<int> DeleteDeviceAsync(string nodeId)
    {
        RequireId(nodeId);
        using var response = await SendAsync(HttpMethod.Delete, "device/" + nodeId, null, "delete a device", notFoundIsAnswer: true);
        return (int)response.StatusCode;
    }

    /// <returns>
    /// The HTTP status: a success, or 404, which the API may give for a one-off key that was used
    /// and so revoked automatically (undocumented, hence recorded).
    /// </returns>
    public async Task<int> DeleteKeyAsync(string keyId)
    {
        RequireId(keyId);
        using var response = await SendAsync(HttpMethod.Delete, "tailnet/-/keys/" + keyId, null, "delete an auth key", notFoundIsAnswer: true);
        return (int)response.StatusCode;
    }

    /// <summary>The devices carrying <paramref name="tag"/>: an exact-match filter, so the rest of the tailnet is never read.</summary>
    public async Task<IReadOnlyList<SmokeDevice>> ListDevicesAsync(string tag)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            "tailnet/-/devices?tags=" + Uri.EscapeDataString(tag),
            null,
            "list the tailnet's devices with " + tag,
            notFoundIsAnswer: false);
        return await ReadAsync(response, root => root.GetProperty("devices").EnumerateArray().Select(ReadDevice).ToList());
    }

    private static SmokeDevice ReadDevice(JsonElement device) => new(
        Text(device, "nodeId"),
        OptionalText(device, "hostname") ?? string.Empty,
        Strings(device, "tags"),
        OptionalText(device, "created") is { } created && DateTimeOffset.TryParse(created, out var parsed) ? parsed : null,
        Strings(device, "addresses"),
        Bool(device, "isEphemeral"),
        Bool(device, "keyExpiryDisabled"),
        OptionalText(device, "tailnetLockError"),
        OptionalText(device, "tailnetLockKey"));

    private static void RequireId(string value)
    {
        if (value.Length is 0 or > MaxIdLength || !value.All(char.IsAsciiLetterOrDigit))
        {
            throw new ArgumentException("Expected a Tailscale id of ASCII letters and digits.", nameof(value));
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, byte[]? body, string operation, bool notFoundIsAnswer)
    {
        using var request = new HttpRequestMessage(method, new Uri(ApiBase, path));
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync());
        var response = await _http.SendAsync(request);
        if (!(notFoundIsAnswer && response.StatusCode == HttpStatusCode.NotFound))
        {
            await EnsureSuccessAsync(response, operation);
        }

        return response;
    }

    private async Task<string> TokenAsync()
    {
        if (_token is not null && DateTimeOffset.UtcNow - _tokenIssuedAt < TokenReuse)
        {
            return _token;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(ApiBase, "oauth/token"))
        {
            Content = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("grant_type", "client_credentials"),
                new KeyValuePair<string, string>("client_id", _credential.ClientId),
                new KeyValuePair<string, string>("client_secret", _credential.ClientSecret),
            ]),
        };
        using var response = await _http.SendAsync(request);
        await EnsureSuccessAsync(response, "obtain an access token");
        _token = await ReadAsync(response, root => Text(root, "access_token"));
        _tokenIssuedAt = DateTimeOffset.UtcNow;
        return _token;
    }

    /// <summary>A refusal is shown with its status and a short redacted excerpt of the body, where Tailscale says why.</summary>
    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var status = (int)response.StatusCode;
        var text = await response.Content.ReadAsStringAsync();
        var excerpt = SecretRedactor.Redact(text.Length > MaxLoggedBodyLength ? text[..MaxLoggedBodyLength] : text);
        response.Dispose();
        throw new ConnectProvisioningException($"The tailnet API refused to {operation} (HTTP {status}): {excerpt}");
    }

    /// <summary>The body may hold a token or an auth key, so its bytes are zeroed once read.</summary>
    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, Func<JsonElement, T> read)
    {
        var bytes = await response.Content.ReadAsByteArrayAsync();
        try
        {
            using var document = JsonDocument.Parse(bytes);
            return read(document.RootElement);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException or FormatException)
        {
            throw new ConnectProvisioningException("The tailnet API returned a response this harness cannot read.");
        }
        finally
        {
            Array.Clear(bytes);
        }
    }

    private static string Text(JsonElement element, string name) =>
        element.GetProperty(name).GetString() is { Length: > 0 } text
            ? text
            : throw new InvalidOperationException($"'{name}' is empty.");

    private static string? OptionalText(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool? Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static IReadOnlyList<string> Strings(JsonElement element, string name) =>
        element.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToList()
            : [];
}
