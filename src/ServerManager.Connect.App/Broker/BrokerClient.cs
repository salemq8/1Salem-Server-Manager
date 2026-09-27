using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using ServerManager.Connect.App.Configuration;
using ServerManager.Connect.App.Identity;
using ServerManager.Connect.Core.Crypto;

namespace ServerManager.Connect.App.Broker;

/// <summary>
/// HTTPS client for the broker API. The address is checked against
/// <see cref="BrokerEndpointPolicy"/> once, here, so no code path can send a signed request or an
/// invite secret anywhere else. Redirects are never followed: the broker does not issue them, and
/// following one could carry a signed request (and its body) to another origin or down to plain
/// HTTP. Responses are read up to 64 KiB, within one deadline per request, and parsed
/// defensively; anything unexpected is <see cref="BrokerFailure.InvalidResponse"/>, never a guess.
/// </summary>
public sealed partial class BrokerClient : IBrokerClient, IDisposable
{
    private const int MaxResponseBytes = 64 * 1024;

    // DateTimeOffset's range in Unix seconds: FromUnixTimeSeconds throws outside it, and a time
    // the broker sends outside it is an unexpected response like any other.
    private const long MinUnixSeconds = -62_135_596_800;
    private const long MaxUnixSeconds = 253_402_300_799;

    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(20);

    private readonly Uri _origin;
    private readonly IDeviceIdentity _identity;
    private readonly HttpClient _http;
    private readonly TimeSpan _requestTimeout;

    public BrokerClient(Uri origin, bool developmentMode, IDeviceIdentity identity, HttpMessageHandler? handler = null)
        : this(origin, developmentMode, identity, handler, DefaultRequestTimeout)
    {
    }

    internal BrokerClient(Uri origin, bool developmentMode, IDeviceIdentity identity, HttpMessageHandler? handler, TimeSpan requestTimeout)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(identity);
        if (!BrokerEndpointPolicy.IsAllowed(origin, developmentMode, out var reason))
        {
            throw new BrokerEndpointRefusedException(reason);
        }

        _origin = origin;
        _identity = identity;
        _requestTimeout = requestTimeout;

        // The deadline is per request, in SendRawAsync, and covers reading the body too: with
        // ResponseHeadersRead, HttpClient.Timeout would stop at the headers.
        _http = new HttpClient(handler ?? CreateHandler()) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<string> RegisterDeviceAsync(CancellationToken cancellationToken)
    {
        const string operation = "register device";
        var spki = _identity.PublicKeySpkiBase64Url;
        var json = await SendAsync(operation, HttpMethod.Post, "/v1/devices", JsonBody(writer => writer.WriteString("spki", spki)), cancellationToken)
            .ConfigureAwait(false);

        // The broker derives the id from our key. Any other answer means it is not talking about
        // this device, and nothing it says afterwards could be trusted to be about us.
        var deviceId = RequiredString(json, "deviceId", operation);
        return string.Equals(deviceId, _identity.DeviceId, StringComparison.Ordinal)
            ? deviceId
            : throw Invalid(operation);
    }

    public async Task<InviteRedemption> RedeemInviteAsync(string secret, CancellationToken cancellationToken)
    {
        const string operation = "redeem invite";
        ArgumentException.ThrowIfNullOrEmpty(secret);
        var json = await SendAsync(operation, HttpMethod.Post, "/v1/invites/redeem", JsonBody(writer => writer.WriteString("secret", secret)), cancellationToken)
            .ConfigureAwait(false);
        var membershipId = RequiredString(json, "membershipId", operation);
        return BrokerFormats.IsMembershipId(membershipId)
            ? new InviteRedemption(membershipId, RequiredString(json, "serverLabel", operation))
            : throw Invalid(operation);
    }

    public async Task<IReadOnlyList<Membership>> GetMembershipsAsync(CancellationToken cancellationToken)
    {
        const string operation = "list memberships";
        var json = await SendAsync(operation, HttpMethod.Get, "/v1/devices/me/memberships", null, cancellationToken).ConfigureAwait(false);
        if (!json.TryGetProperty("memberships", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            throw Invalid(operation);
        }

        var memberships = new List<Membership>();
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw Invalid(operation);
            }

            var membershipId = RequiredString(item, "membershipId", operation);
            var ownerId = RequiredString(item, "ownerId", operation);
            var nodeId = OptionalString(item, "nodeId", operation);
            var nodeState = ParseNodeState(OptionalString(item, "nodeState", operation));
            if (!BrokerFormats.IsMembershipId(membershipId) ||
                !ConnectKeyIds.IsOwnerId(ownerId) ||
                (nodeId is not null && !BrokerFormats.IsNodeId(nodeId)))
            {
                throw Invalid(operation);
            }

            memberships.Add(new Membership(
                membershipId,
                ownerId,
                RequiredString(item, "serverId", operation),
                RequiredString(item, "serverLabel", operation),
                ParseState(RequiredString(item, "state", operation)),
                nodeId,
                nodeState));
        }

        return memberships;
    }

    public async Task<EnrollmentPackage?> TakeEnrollmentAsync(string membershipId, CancellationToken cancellationToken)
    {
        const string operation = "take enrollment";
        RequireMembershipId(membershipId);
        JsonElement json;
        try
        {
            json = await SendAsync(operation, HttpMethod.Get, $"/v1/memberships/{membershipId}/enrollment", null, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (BrokerException exception) when (exception.Failure == BrokerFailure.NotFound)
        {
            // The Agent has not posted it yet, or it expired unread. Either way there is nothing to open.
            return null;
        }

        var echoed = RequiredString(json, "membershipId", operation);
        var ownerId = RequiredString(json, "ownerId", operation);
        return echoed == membershipId && ConnectKeyIds.IsOwnerId(ownerId)
            ? new EnrollmentPackage(membershipId, ownerId, RequiredString(json, "ciphertext", operation))
            : throw Invalid(operation);
    }

    public async Task BindNodeAsync(string membershipId, string nodeId, CancellationToken cancellationToken)
    {
        RequireMembershipId(membershipId);
        if (!BrokerFormats.IsNodeId(nodeId))
        {
            throw new ArgumentException("Unexpected node id format.", nameof(nodeId));
        }

        await SendAsync("bind node", HttpMethod.Post, $"/v1/memberships/{membershipId}/node", JsonBody(writer => writer.WriteString("nodeId", nodeId)), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<SessionTicket> CreateSessionAsync(string membershipId, string sessionSpki, CancellationToken cancellationToken)
    {
        const string operation = "create session";
        RequireMembershipId(membershipId);
        ArgumentException.ThrowIfNullOrEmpty(sessionSpki);
        var json = await SendAsync(
                operation,
                HttpMethod.Post,
                "/v1/sessions",
                JsonBody(writer =>
                {
                    writer.WriteString("membershipId", membershipId);
                    writer.WriteString("sessionSpki", sessionSpki);
                }),
                cancellationToken)
            .ConfigureAwait(false);
        if (!json.TryGetProperty("expiresAt", out var expiresAt) ||
            !expiresAt.TryGetInt64(out var expiresAtSeconds) ||
            expiresAtSeconds is < MinUnixSeconds or > MaxUnixSeconds)
        {
            throw Invalid(operation);
        }

        return new SessionTicket(
            RequiredString(json, "ticket", operation),
            DateTimeOffset.FromUnixTimeSeconds(expiresAtSeconds),
            RequiredString(json, "hostBridge", operation));
    }

    public async Task<byte[]> GetTicketKeysAsync(CancellationToken cancellationToken)
    {
        const string operation = "get ticket keys";
        var content = await SendRawAsync(operation, HttpMethod.Get, "/v1/keys", null, signed: false, cancellationToken).ConfigureAwait(false);
        var json = ParseObject(content, operation);
        if (!json.TryGetProperty("keys", out var keys) || keys.ValueKind != JsonValueKind.Array || keys.GetArrayLength() == 0)
        {
            throw Invalid(operation);
        }

        // Verbatim: the transport parses the /v1/keys document itself and pins every key to ES256.
        return content;
    }

    public void Dispose() => _http.Dispose();

    private static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    };

    private async Task<JsonElement> SendAsync(string operation, HttpMethod method, string path, byte[]? body, CancellationToken cancellationToken) =>
        ParseObject(await SendRawAsync(operation, method, path, body, signed: true, cancellationToken).ConfigureAwait(false), operation);

    private async Task<byte[]> SendRawAsync(
        string operation,
        HttpMethod method,
        string path,
        byte[]? body,
        bool signed,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(_origin, path));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        }

        // One deadline for the whole exchange, body included: most callers pass no token of their
        // own, and a broker that stalls after its headers must not hold them (and whatever gate
        // they hold) forever.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_requestTimeout);
        try
        {
            if (signed)
            {
                await _identity.SignAsync(request, deadline.Token).ConfigureAwait(false);
            }

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                .ConfigureAwait(false);
            var content = await ReadBoundedAsync(response.Content, operation, deadline.Token).ConfigureAwait(false);
            return response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created
                ? content
                : throw Failure(operation, response, content);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            throw new BrokerException(BrokerFailure.Unreachable, $"{operation}: the broker could not be reached.", exception);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new BrokerException(BrokerFailure.Unreachable, $"{operation}: the broker did not answer in time.", exception);
        }
        finally
        {
            // A body can hold an invite secret. The managed strings cannot be wiped, but this copy can.
            if (body is not null)
            {
                CryptographicOperations.ZeroMemory(body);
            }
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, string operation, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaxResponseBytes)
        {
            throw Invalid(operation);
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxResponseBytes)
            {
                throw Invalid(operation);
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static BrokerException Failure(string operation, HttpResponseMessage response, byte[] content)
    {
        var status = (int)response.StatusCode;
        var code = ErrorCode(content);
        var message = code is null ? $"{operation}: HTTP {status}." : $"{operation}: HTTP {status} {code}.";
        return status switch
        {
            404 => new BrokerException(BrokerFailure.NotFound, message) { ErrorCode = code },
            401 => new BrokerException(BrokerFailure.Unauthorized, message) { ErrorCode = code },
            409 => new BrokerException(BrokerFailure.Conflict, message) { ErrorCode = code },
            429 => new BrokerException(BrokerFailure.RateLimited, message) { ErrorCode = code, RetryAfter = RetryAfter(response) },
            >= 500 => new BrokerException(BrokerFailure.Unavailable, message) { ErrorCode = code },
            _ => new BrokerException(BrokerFailure.Rejected, message) { ErrorCode = code }
        };
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta)
        {
            return delta;
        }

        return header?.Date is { } date && date > DateTimeOffset.UtcNow ? date - DateTimeOffset.UtcNow : null;
    }

    /// <summary>The broker's error code, only if it has the shape of one, so a body is never echoed into a message.</summary>
    private static string? ErrorCode(byte[] content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("error", out var error) &&
                   error.ValueKind == JsonValueKind.String &&
                   error.GetString() is { } code &&
                   ErrorCodePattern().IsMatch(code)
                ? code
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement ParseObject(byte[] content, string operation)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? document.RootElement.Clone()
                : throw Invalid(operation);
        }
        catch (JsonException exception)
        {
            throw Invalid(operation, exception);
        }
    }

    private static byte[] JsonBody(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }

        var body = buffer.WrittenSpan.ToArray();
        buffer.Clear();
        return body;
    }

    private static string RequiredString(JsonElement json, string name, string operation) =>
        json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text
            : throw Invalid(operation);

    private static string? OptionalString(JsonElement json, string name, string operation)
    {
        if (!json.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : throw Invalid(operation);
    }

    private static MembershipState ParseState(string state) => state switch
    {
        "pending" => MembershipState.Pending,
        "approved" => MembershipState.Approved,
        "rejected" => MembershipState.Rejected,
        "revoked" => MembershipState.Revoked,
        _ => MembershipState.Unknown
    };

    private static MembershipNodeState ParseNodeState(string? state) => state switch
    {
        null => MembershipNodeState.None,
        "candidate" => MembershipNodeState.Candidate,
        "confirmed" => MembershipNodeState.Confirmed,
        "rejected" => MembershipNodeState.Rejected,
        _ => MembershipNodeState.Unknown
    };

    private static void RequireMembershipId(string membershipId)
    {
        if (!BrokerFormats.IsMembershipId(membershipId))
        {
            throw new ArgumentException("Unexpected membership id format.", nameof(membershipId));
        }
    }

    private static BrokerException Invalid(string operation, Exception? innerException = null) =>
        new(BrokerFailure.InvalidResponse, $"{operation}: the broker sent an unexpected response.", innerException);

    [GeneratedRegex("^[a-z_]{1,40}$", RegexOptions.CultureInvariant)]
    private static partial Regex ErrorCodePattern();
}
