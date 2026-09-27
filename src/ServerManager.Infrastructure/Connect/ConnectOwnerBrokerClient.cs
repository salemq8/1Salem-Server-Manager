using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using ServerManager.Connect.Core.Broker;
using ServerManager.Connect.Core.Crypto;
using ServerManager.Connect.Core.Identity;

namespace ServerManager.Infrastructure.Connect;

/// <summary>Hardened owner-side client for every broker owner route.</summary>
public interface IConnectOwnerBrokerClient : IDisposable
{
    Task<string> RegisterOwnerAsync(CancellationToken cancellationToken);
    Task PutServerAsync(Guid serverId, string label, string hostBridge, CancellationToken cancellationToken);
    Task<ConnectBrokerInvite> CreateInviteAsync(Guid serverId, int ttlSeconds, CancellationToken cancellationToken);
    Task RevokeInviteAsync(string inviteId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ConnectBrokerMembership>> GetMembershipsAsync(CancellationToken cancellationToken);
    Task ApproveMembershipAsync(string membershipId, CancellationToken cancellationToken);
    Task RejectMembershipAsync(string membershipId, CancellationToken cancellationToken);
    Task PutEnrollmentAsync(string membershipId, string ciphertext, CancellationToken cancellationToken);
    Task ConfirmNodeAsync(string membershipId, string nodeId, CancellationToken cancellationToken);
    Task RejectNodeAsync(string membershipId, string nodeId, CancellationToken cancellationToken);
    Task<ConnectBrokerRevocationResult> RevokeMembershipAsync(string membershipId, CancellationToken cancellationToken);
    Task RevokeDeviceAsync(string deviceId, CancellationToken cancellationToken);
    Task RevokeSessionAsync(string ticketId, CancellationToken cancellationToken);
    Task<ConnectBrokerRevocationPage> GetRevocationsAsync(long after, CancellationToken cancellationToken);
    Task<byte[]> GetTicketKeysAsync(CancellationToken cancellationToken);
}

public sealed partial class ConnectOwnerBrokerClient : IConnectOwnerBrokerClient
{
    private const int MaxResponseBytes = 64 * 1024;
    private readonly Uri _origin;
    private readonly ConnectIdentity _identity;
    private readonly SignedRequestSigner _signer;
    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;

    public ConnectOwnerBrokerClient(
        Uri origin,
        bool developmentMode,
        ConnectIdentity identity,
        TimeProvider clock,
        HttpMessageHandler? handler = null)
        : this(origin, developmentMode, identity, clock, handler, TimeSpan.FromSeconds(20))
    {
    }

    internal ConnectOwnerBrokerClient(
        Uri origin,
        bool developmentMode,
        ConnectIdentity identity,
        TimeProvider clock,
        HttpMessageHandler? handler,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(identity);
        if (identity.Kind != ConnectIdentityKind.Owner)
        {
            throw new ArgumentException("The broker owner client requires an owner identity.", nameof(identity));
        }

        ValidateOrigin(origin, developmentMode);
        _origin = origin;
        _identity = identity;
        _signer = new SignedRequestSigner(identity, clock ?? throw new ArgumentNullException(nameof(clock)));
        _timeout = timeout;
        _http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<string> RegisterOwnerAsync(CancellationToken cancellationToken)
    {
        var json = await SendJsonAsync("register owner", HttpMethod.Post, "/v1/owners", Body(w => w.WriteString("spki", _identity.PublicKeySpkiBase64Url)), true, cancellationToken).ConfigureAwait(false);
        var ownerId = RequiredString(json, "ownerId", "register owner");
        return ownerId == _identity.KeyId ? ownerId : throw Invalid("register owner");
    }

    public Task PutServerAsync(Guid serverId, string label, string hostBridge, CancellationToken cancellationToken) =>
        SendNoResultAsync("register server", HttpMethod.Put, $"/v1/servers/{serverId:D}", Body(w =>
        {
            w.WriteString("label", label);
            w.WriteString("protocol", "tcp");
            w.WriteString("hostBridge", hostBridge);
        }), cancellationToken);

    public async Task<ConnectBrokerInvite> CreateInviteAsync(Guid serverId, int ttlSeconds, CancellationToken cancellationToken)
    {
        var json = await SendJsonAsync("create invite", HttpMethod.Post, "/v1/invites", Body(w =>
        {
            w.WriteString("serverId", serverId.ToString("D"));
            w.WriteNumber("ttlSeconds", ttlSeconds);
        }), true, cancellationToken).ConfigureAwait(false);
        return new ConnectBrokerInvite(
            RequiredString(json, "inviteId", "create invite"),
            RequiredString(json, "secret", "create invite"),
            UnixTime(json, "expiresAt", "create invite"));
    }

    public Task RevokeInviteAsync(string inviteId, CancellationToken cancellationToken) =>
        SendNoResultAsync("revoke invite", HttpMethod.Post, $"/v1/invites/{Segment(inviteId)}/revoke", null, cancellationToken);

    public async Task<IReadOnlyList<ConnectBrokerMembership>> GetMembershipsAsync(CancellationToken cancellationToken)
    {
        var json = await SendJsonAsync("list memberships", HttpMethod.Get, "/v1/owners/me/memberships", null, true, cancellationToken).ConfigureAwait(false);
        if (!json.TryGetProperty("memberships", out var values) || values.ValueKind != JsonValueKind.Array)
        {
            throw Invalid("list memberships");
        }

        var memberships = new List<ConnectBrokerMembership>();
        foreach (var value in values.EnumerateArray())
        {
            memberships.Add(new ConnectBrokerMembership(
                RequiredString(value, "membershipId", "list memberships"),
                RequiredGuid(value, "serverId", "list memberships"),
                RequiredString(value, "serverLabel", "list memberships"),
                RequiredString(value, "deviceId", "list memberships"),
                RequiredString(value, "deviceSpki", "list memberships"),
                RequiredString(value, "state", "list memberships"),
                RequiredInt64(value, "av", "list memberships"),
                OptionalString(value, "nodeId", "list memberships"),
                OptionalString(value, "nodeState", "list memberships"),
                OptionalUnixTime(value, "nodeBoundAt", "list memberships"),
                OptionalUnixTime(value, "nodeConfirmedAt", "list memberships"),
                UnixTime(value, "createdAt", "list memberships"),
                OptionalUnixTime(value, "approvedAt", "list memberships")));
        }

        return memberships;
    }

    public Task ApproveMembershipAsync(string membershipId, CancellationToken cancellationToken) =>
        MembershipActionAsync(membershipId, "approve", cancellationToken);

    public Task RejectMembershipAsync(string membershipId, CancellationToken cancellationToken) =>
        MembershipActionAsync(membershipId, "reject", cancellationToken);

    public Task PutEnrollmentAsync(string membershipId, string ciphertext, CancellationToken cancellationToken) =>
        SendNoResultAsync("post enrollment", HttpMethod.Post, $"/v1/memberships/{Segment(membershipId)}/enrollment", Body(w => w.WriteString("ciphertext", ciphertext)), cancellationToken);

    public Task ConfirmNodeAsync(string membershipId, string nodeId, CancellationToken cancellationToken) =>
        DecideNodeAsync(membershipId, nodeId, "confirm", cancellationToken);

    public Task RejectNodeAsync(string membershipId, string nodeId, CancellationToken cancellationToken) =>
        DecideNodeAsync(membershipId, nodeId, "reject", cancellationToken);

    public async Task<ConnectBrokerRevocationResult> RevokeMembershipAsync(string membershipId, CancellationToken cancellationToken)
    {
        var json = await SendJsonAsync("revoke membership", HttpMethod.Post, $"/v1/memberships/{Segment(membershipId)}/revoke", null, true, cancellationToken).ConfigureAwait(false);
        return new ConnectBrokerRevocationResult(
            RequiredString(json, "membershipId", "revoke membership"),
            RequiredString(json, "state", "revoke membership"),
            RequiredInt64(json, "av", "revoke membership"));
    }

    public Task RevokeDeviceAsync(string deviceId, CancellationToken cancellationToken) =>
        SendNoResultAsync("revoke device", HttpMethod.Post, $"/v1/devices/{Segment(deviceId)}/revoke", null, cancellationToken);

    public Task RevokeSessionAsync(string ticketId, CancellationToken cancellationToken) =>
        SendNoResultAsync("revoke session", HttpMethod.Post, $"/v1/sessions/{Segment(ticketId)}/revoke", null, cancellationToken);

    public async Task<ConnectBrokerRevocationPage> GetRevocationsAsync(long after, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(after);
        var path = "/v1/owners/me/revocations?after=" + after.ToString(CultureInfo.InvariantCulture);
        var json = await SendJsonAsync("read revocations", HttpMethod.Get, path, null, true, cancellationToken).ConfigureAwait(false);
        if (!json.TryGetProperty("revocations", out var values) || values.ValueKind != JsonValueKind.Array ||
            !json.TryGetProperty("more", out var more) || more.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw Invalid("read revocations");
        }

        var events = new List<ConnectBrokerRevocation>();
        foreach (var value in values.EnumerateArray())
        {
            events.Add(new ConnectBrokerRevocation(
                RequiredInt64(value, "seq", "read revocations"),
                RequiredString(value, "kind", "read revocations"),
                OptionalString(value, "jti", "read revocations"),
                OptionalString(value, "membershipId", "read revocations"),
                OptionalString(value, "deviceId", "read revocations"),
                OptionalString(value, "serverId", "read revocations"),
                OptionalInt64(value, "av", "read revocations"),
                UnixTime(value, "at", "read revocations")));
        }

        return new ConnectBrokerRevocationPage(events, RequiredInt64(json, "cursor", "read revocations"), more.GetBoolean());
    }

    public Task<byte[]> GetTicketKeysAsync(CancellationToken cancellationToken) =>
        SendRawAsync("get ticket keys", HttpMethod.Get, "/v1/keys", null, signed: false, cancellationToken);

    public void Dispose() => _http.Dispose();

    private Task MembershipActionAsync(string membershipId, string action, CancellationToken cancellationToken) =>
        SendNoResultAsync(action + " membership", HttpMethod.Post, $"/v1/memberships/{Segment(membershipId)}/{action}", null, cancellationToken);

    private Task DecideNodeAsync(string membershipId, string nodeId, string action, CancellationToken cancellationToken) =>
        SendNoResultAsync(action + " node", HttpMethod.Post, $"/v1/memberships/{Segment(membershipId)}/node/{action}", Body(w => w.WriteString("nodeId", nodeId)), cancellationToken);

    private async Task SendNoResultAsync(string operation, HttpMethod method, string path, byte[]? body, CancellationToken cancellationToken) =>
        _ = await SendJsonAsync(operation, method, path, body, true, cancellationToken).ConfigureAwait(false);

    private async Task<JsonElement> SendJsonAsync(string operation, HttpMethod method, string path, byte[]? body, bool signed, CancellationToken cancellationToken)
    {
        var response = await SendRawAsync(operation, method, path, body, signed, cancellationToken).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(response);
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : throw Invalid(operation);
        }
        catch (JsonException exception)
        {
            throw Invalid(operation, exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(response);
        }
    }

    private async Task<byte[]> SendRawAsync(string operation, HttpMethod method, string path, byte[]? body, bool signed, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(_origin, path));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);
        try
        {
            if (signed)
            {
                await _signer.SignAsync(request, deadline.Token).ConfigureAwait(false);
            }

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            var content = await ReadBoundedAsync(response.Content, deadline.Token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
            {
                return content;
            }

            try
            {
                throw Failure(operation, response, content);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(content);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            throw new ConnectOwnerBrokerException(ConnectOwnerBrokerFailure.Unreachable, null, $"{operation}: the broker could not be reached.", exception);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ConnectOwnerBrokerException(ConnectOwnerBrokerFailure.Unreachable, null, $"{operation}: the broker did not answer in time.", exception);
        }
        finally
        {
            if (body is not null)
            {
                CryptographicOperations.ZeroMemory(body);
            }
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaxResponseBytes)
        {
            throw Invalid("read response");
        }

        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        try
        {
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (output.Length + read > MaxResponseBytes)
                {
                    throw Invalid("read response");
                }

                output.Write(buffer, 0, read);
            }

            return output.ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private static ConnectOwnerBrokerException Failure(string operation, HttpResponseMessage response, byte[] content)
    {
        var status = (int)response.StatusCode;
        var code = ErrorCode(content);
        var message = code is null ? $"{operation}: HTTP {status}." : $"{operation}: HTTP {status} {code}.";
        return new ConnectOwnerBrokerException(status switch
        {
            401 => ConnectOwnerBrokerFailure.Unauthorized,
            404 => ConnectOwnerBrokerFailure.NotFound,
            409 => ConnectOwnerBrokerFailure.Conflict,
            429 => ConnectOwnerBrokerFailure.RateLimited,
            >= 500 => ConnectOwnerBrokerFailure.Unavailable,
            _ => ConnectOwnerBrokerFailure.Rejected
        }, code, message) { RetryAfter = RetryAfter(response) };
    }

    private static string? ErrorCode(byte[] content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            return document.RootElement.TryGetProperty("error", out var value) &&
                value.ValueKind == JsonValueKind.String && value.GetString() is { } code &&
                ErrorCodePattern().IsMatch(code) ? code : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response) =>
        response.Headers.RetryAfter?.Delta ??
        (response.Headers.RetryAfter?.Date is { } date && date > DateTimeOffset.UtcNow ? date - DateTimeOffset.UtcNow : null);

    private static byte[] Body(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }

        var result = buffer.WrittenSpan.ToArray();
        buffer.Clear();
        return result;
    }

    private static string RequiredString(JsonElement value, string name, string operation) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String && property.GetString() is { Length: > 0 } text
            ? text : throw Invalid(operation);

    private static string? OptionalString(JsonElement value, string name, string operation) =>
        !value.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null
            ? null
            : property.ValueKind == JsonValueKind.String ? property.GetString() : throw Invalid(operation);

    private static long RequiredInt64(JsonElement value, string name, string operation) =>
        value.TryGetProperty(name, out var property) && property.TryGetInt64(out var number) ? number : throw Invalid(operation);

    private static long? OptionalInt64(JsonElement value, string name, string operation) =>
        !value.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null
            ? null
            : property.TryGetInt64(out var number) ? number : throw Invalid(operation);

    private static DateTimeOffset UnixTime(JsonElement value, string name, string operation) =>
        FromUnixTime(RequiredInt64(value, name, operation), operation);

    private static DateTimeOffset? OptionalUnixTime(JsonElement value, string name, string operation) =>
        OptionalInt64(value, name, operation) is { } seconds ? FromUnixTime(seconds, operation) : null;

    private static Guid RequiredGuid(JsonElement value, string name, string operation) =>
        Guid.TryParseExact(RequiredString(value, name, operation), "D", out var parsed) && parsed != Guid.Empty
            ? parsed : throw Invalid(operation);

    private static DateTimeOffset FromUnixTime(long seconds, string operation)
    {
        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw Invalid(operation, exception);
        }
    }

    private static string Segment(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 160 || !SegmentPattern().IsMatch(value))
        {
            throw new ArgumentException("Unexpected broker id format.", nameof(value));
        }

        return value;
    }

    private static void ValidateOrigin(Uri origin, bool developmentMode)
    {
        if (!origin.IsAbsoluteUri)
        {
            throw new ConnectOwnerBrokerEndpointRefusedException();
        }

        var allowedHttp = developmentMode && origin.Scheme == Uri.UriSchemeHttp &&
            (origin.Host == "127.0.0.1" || string.Equals(origin.Host, "localhost", StringComparison.OrdinalIgnoreCase));
        if (origin.UserInfo.Length != 0 || origin.Query.Length != 0 ||
            origin.Fragment.Length != 0 || origin.AbsolutePath != "/" ||
            (origin.Scheme != Uri.UriSchemeHttps && !allowedHttp))
        {
            throw new ConnectOwnerBrokerEndpointRefusedException();
        }
    }

    private static ConnectOwnerBrokerException Invalid(string operation, Exception? inner = null) =>
        new(ConnectOwnerBrokerFailure.InvalidResponse, null, $"{operation}: the broker sent an unexpected response.", inner);

    [GeneratedRegex("^[A-Za-z0-9._:-]{1,160}$", RegexOptions.CultureInvariant)]
    private static partial Regex SegmentPattern();

    [GeneratedRegex("^[a-z_]{1,40}$", RegexOptions.CultureInvariant)]
    private static partial Regex ErrorCodePattern();
}

public sealed record ConnectBrokerInvite(string InviteId, string Secret, DateTimeOffset ExpiresAt)
{
    public override string ToString() => $"ConnectBrokerInvite {{ InviteId = {InviteId}, Secret = [REDACTED], ExpiresAt = {ExpiresAt} }}";
}

public sealed record ConnectBrokerMembership(
    string MembershipId,
    Guid ServerId,
    string ServerLabel,
    string DeviceId,
    string DeviceSpki,
    string State,
    long AuthorizationVersion,
    string? NodeId,
    string? NodeState,
    DateTimeOffset? NodeBoundAt,
    DateTimeOffset? NodeConfirmedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ApprovedAt);

public sealed record ConnectBrokerRevocationResult(string MembershipId, string State, long AuthorizationVersion);

public sealed record ConnectBrokerRevocation(
    long Sequence,
    string Kind,
    string? TicketId,
    string? MembershipId,
    string? DeviceId,
    string? ServerId,
    long? AuthorizationVersion,
    DateTimeOffset At);

public sealed record ConnectBrokerRevocationPage(IReadOnlyList<ConnectBrokerRevocation> Revocations, long Cursor, bool More);

public enum ConnectOwnerBrokerFailure { NotFound, Unauthorized, Conflict, RateLimited, Unavailable, Unreachable, Rejected, InvalidResponse }

public sealed class ConnectOwnerBrokerException : Exception
{
    public ConnectOwnerBrokerException(ConnectOwnerBrokerFailure failure, string? errorCode, string message, Exception? inner = null)
        : base(message, inner)
    {
        Failure = failure;
        ErrorCode = errorCode;
    }

    public ConnectOwnerBrokerFailure Failure { get; }
    public string? ErrorCode { get; }
    public TimeSpan? RetryAfter { get; init; }
}

public sealed class ConnectOwnerBrokerEndpointRefusedException : Exception
{
    public ConnectOwnerBrokerEndpointRefusedException()
        : base("The Connect broker must be an HTTPS origin; development HTTP is allowed only on this PC.")
    {
    }
}
