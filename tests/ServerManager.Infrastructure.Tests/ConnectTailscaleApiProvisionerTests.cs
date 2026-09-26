using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using ServerManager.Infrastructure.Connect;

namespace ServerManager.Infrastructure.Tests;

/// <summary>
/// The Agent's calls into the owner's tailnet (contract §7, §12), against a recording
/// <see cref="HttpMessageHandler"/>: nothing reaches a network. The credential and keys are
/// made-up strings with a recognisable marker so a leak would show.
/// </summary>
public sealed class ConnectTailscaleApiProvisionerTests
{
    private const string ClientId = "kTestClient1CNTRL";
    private const string SecretMarker = "notarealsecret";
    private const string ClientSecret = "tskey-client-kTestClient1CNTRL-" + SecretMarker;
    private const string AccessToken = "test-access-token";
    private const string KeyId = "kTestKey1CNTRL";
    private const string AuthKey = "tskey-auth-kTestKey1CNTRL-" + SecretMarker;
    private const string NodeId = "nTestFriend1CNTRL";

    private static readonly Regex DescriptionRule = new("^[A-Za-z0-9 -]{1,50}$");

    [Fact]
    public async Task AFriendKey_IsMintedWithExactlyTheContractRequest()
    {
        var handler = new RecordingHandler(TailnetApi);
        var provisioner = CreateProvisioner(handler);

        var secret = await provisioner.CreateFriendAuthKeyAsync("mem_0123456789abcdefXYZ", CancellationToken.None);

        Assert.Equal(KeyId, secret.KeyId);
        Assert.Equal(AuthKey, secret.AuthKey);
        Assert.Collection(
            handler.Requests,
            token =>
            {
                Assert.Equal(HttpMethod.Post, token.Method);
                Assert.Equal("https://api.tailscale.com/api/v2/oauth/token", token.Uri.AbsoluteUri);
                Assert.Null(token.Authorization);
                Assert.Equal("application/x-www-form-urlencoded", token.ContentType);
                Assert.Equal(
                    new Dictionary<string, string>
                    {
                        ["grant_type"] = "client_credentials",
                        ["client_id"] = ClientId,
                        ["client_secret"] = ClientSecret
                    },
                    ParseForm(token.Body!));
            },
            create =>
            {
                Assert.Equal(HttpMethod.Post, create.Method);
                Assert.Equal("https://api.tailscale.com/api/v2/tailnet/-/keys", create.Uri.AbsoluteUri);
                Assert.Equal("Bearer " + AccessToken, create.Authorization);
                Assert.Equal("application/json", create.ContentType);
                Assert.Equal(
                    "{\"capabilities\":{\"devices\":{\"create\":{\"reusable\":false,\"ephemeral\":false,\"preauthorized\":true,\"tags\":[\"tag:onesalem-client\"]}}}," +
                    "\"expirySeconds\":86400,\"description\":\"1salem connect mem-0123456789ab\"}",
                    create.Body);
            });
    }

    [Fact]
    public async Task AHostKey_IsOneOffPreauthorizedPersistentAndHasOnlyTheHostTag()
    {
        var handler = new RecordingHandler(TailnetApi);
        var provisioner = CreateProvisioner(handler);

        var secret = await provisioner.CreateHostAuthKeyAsync(CancellationToken.None);

        Assert.Equal(KeyId, secret.KeyId);
        var create = handler.Requests[^1];
        Assert.Equal(
            "{\"capabilities\":{\"devices\":{\"create\":{\"reusable\":false,\"ephemeral\":false,\"preauthorized\":true,\"tags\":[\"tag:onesalem-host\"]}}}," +
            "\"expirySeconds\":86400,\"description\":\"1salem connect host\"}",
            create.Body);
    }

    [Theory]
    [InlineData("mem_test0001")]
    [InlineData("a")]
    [InlineData("../../api/v2/device\"},{\"x\":1")]
    [InlineData("عضوية-صديق")]
    [InlineData("mem_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void TheKeyDescription_IsAtMost50LettersDigitsSpacesAndHyphens(string membershipId)
    {
        var description = TailscaleApiProvisioner.FriendKeyDescription(membershipId);

        Assert.Matches(DescriptionRule, description);
        Assert.StartsWith("1salem connect ", description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnusedKey_IsDeletedByItsId_AndAMissingOneIsReportedAsGone()
    {
        var handler = new RecordingHandler((request, _) =>
            request.Method == HttpMethod.Delete && request.RequestUri!.AbsolutePath.EndsWith("/kGone1CNTRL", StringComparison.Ordinal)
                ? Respond(HttpStatusCode.NotFound, "{\"message\":\"not found\"}")
                : TailnetApi(request, null));
        var provisioner = CreateProvisioner(handler);

        Assert.True(await provisioner.DeleteAuthKeyAsync(KeyId, CancellationToken.None));
        Assert.False(await provisioner.DeleteAuthKeyAsync("kGone1CNTRL", CancellationToken.None));

        var deletions = handler.Requests.Where(request => request.Method == HttpMethod.Delete).ToArray();
        Assert.Equal("https://api.tailscale.com/api/v2/tailnet/-/keys/" + KeyId, deletions[0].Uri.AbsoluteUri);
        Assert.Equal("https://api.tailscale.com/api/v2/tailnet/-/keys/kGone1CNTRL", deletions[1].Uri.AbsoluteUri);
        Assert.All(deletions, request => Assert.Equal("Bearer " + AccessToken, request.Authorization));
    }

    [Fact]
    public async Task AReportedNode_IsReadWithItsTagsAndCreationTime()
    {
        var handler = new RecordingHandler(TailnetApi);
        var provisioner = CreateProvisioner(handler);

        var device = await provisioner.GetDeviceAsync(NodeId, CancellationToken.None);

        Assert.NotNull(device);
        Assert.Equal(NodeId, device.NodeId);
        Assert.True(device.HasTag(TailscaleApiProvisioner.FriendTag));
        Assert.Equal(DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture), device.CreatedAt);
        Assert.Equal("friend-pc", device.Hostname);
        Assert.Equal(["100.100.10.20", "fd7a:115c:a1e0::20"], device.Addresses);
        Assert.False(device.IsEphemeral);
        Assert.Null(device.TailnetLockError);
        var read = handler.Requests[^1];
        Assert.Equal(HttpMethod.Get, read.Method);
        Assert.Equal("https://api.tailscale.com/api/v2/device/" + NodeId, read.Uri.AbsoluteUri);
    }

    [Fact]
    public async Task PolicyRead_RequestsJson_AndForbiddenIsDistinct()
    {
        var forbidden = false;
        var handler = new RecordingHandler((request, body) =>
            forbidden && request.RequestUri!.AbsolutePath.EndsWith("/tailnet/-/acl", StringComparison.Ordinal)
                ? Respond(HttpStatusCode.Forbidden, "{\"message\":\"scope\"}")
                : TailnetApi(request, body));
        var provisioner = CreateProvisioner(handler);

        var policy = await provisioner.GetPolicyAsync(CancellationToken.None);
        Assert.Contains("tagOwners", Encoding.UTF8.GetString(policy), StringComparison.Ordinal);
        Assert.Contains("application/json", handler.Requests[^1].Accept);

        forbidden = true;
        await Assert.ThrowsAsync<ConnectPolicyNotPermittedException>(() => provisioner.GetPolicyAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("tag:onesalem-host", "nTestHost1CNTRL")]
    [InlineData("tag:other", "nTestHost1CNTRL")]
    [InlineData("tag:onesalem-client", NodeId)]
    public async Task DeviceDeletion_RefusesHostUntaggedAndTheKnownHostNode(string tag, string hostNodeId)
    {
        var handler = new RecordingHandler((request, body) =>
            request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith(NodeId, StringComparison.Ordinal)
                ? Respond(HttpStatusCode.OK, $"{{\"nodeId\":\"{NodeId}\",\"tags\":[\"{tag}\"]}}")
                : TailnetApi(request, body));
        var provisioner = CreateProvisioner(handler);

        await Assert.ThrowsAsync<ConnectUnsafeDeviceDeletionException>(() =>
            provisioner.DeleteFriendDeviceAsync(NodeId, hostNodeId, CancellationToken.None));
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task VerifiedFriendDeletion_ReReadsBeforeDeleting()
    {
        var handler = new RecordingHandler(TailnetApi);
        var provisioner = CreateProvisioner(handler);

        Assert.True(await provisioner.DeleteFriendDeviceAsync(NodeId, "nTestHost1CNTRL", CancellationToken.None));

        Assert.Equal([HttpMethod.Get, HttpMethod.Delete], handler.Requests.Skip(1).Select(request => request.Method));
    }

    [Fact]
    public async Task DeviceRead_RefusesAResponseForAnotherNode_AndNeverDeletes()
    {
        var handler = new RecordingHandler((request, body) =>
            request.Method == HttpMethod.Get
                ? Respond(HttpStatusCode.OK, "{\"nodeId\":\"nDifferent1CNTRL\",\"tags\":[\"tag:onesalem-client\"]}")
                : TailnetApi(request, body));
        var provisioner = CreateProvisioner(handler);

        await Assert.ThrowsAsync<ConnectProvisioningException>(() =>
            provisioner.DeleteFriendDeviceAsync(NodeId, "nTestHost1CNTRL", CancellationToken.None));
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task AnUnknownNode_ReadsAsNull_AndDeletesAsAlreadyGone()
    {
        var handler = new RecordingHandler((request, _) =>
            request.RequestUri!.AbsolutePath.StartsWith("/api/v2/device/", StringComparison.Ordinal)
                ? Respond(HttpStatusCode.NotFound, "{\"message\":\"not found\"}")
                : TailnetApi(request, null));
        var provisioner = CreateProvisioner(handler);

        Assert.Null(await provisioner.GetDeviceAsync(NodeId, CancellationToken.None));
        Assert.False(await provisioner.DeleteDeviceAsync(NodeId, CancellationToken.None));
    }

    [Fact]
    public async Task ARevokedFriendsNode_IsDeletedFromTheTailnet()
    {
        var handler = new RecordingHandler(TailnetApi);
        var provisioner = CreateProvisioner(handler);

        Assert.True(await provisioner.DeleteDeviceAsync(NodeId, CancellationToken.None));

        var deletion = handler.Requests[^1];
        Assert.Equal(HttpMethod.Delete, deletion.Method);
        Assert.Equal("https://api.tailscale.com/api/v2/device/" + NodeId, deletion.Uri.AbsoluteUri);
        Assert.Equal("Bearer " + AccessToken, deletion.Authorization);
    }

    [Fact]
    public async Task TheAccessToken_IsReused_UntilShortlyBeforeItExpires_AndDroppedWhenRejected()
    {
        var clock = new ConnectManualClock(ConnectTestBroker.StartUnix);
        var rejectNext = false;
        var handler = new RecordingHandler((request, body) =>
        {
            if (rejectNext && request.Method == HttpMethod.Delete)
            {
                rejectNext = false;
                return Respond(HttpStatusCode.Unauthorized, "{\"message\":\"token expired\"}");
            }

            return TailnetApi(request, body);
        });
        var provisioner = CreateProvisioner(handler, clock);

        await provisioner.DeleteDeviceAsync(NodeId, CancellationToken.None);
        await provisioner.DeleteDeviceAsync(NodeId, CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(56));
        await provisioner.DeleteDeviceAsync(NodeId, CancellationToken.None);
        rejectNext = true;
        await Assert.ThrowsAsync<ConnectProvisioningException>(() => provisioner.DeleteDeviceAsync(NodeId, CancellationToken.None));
        await provisioner.DeleteDeviceAsync(NodeId, CancellationToken.None);

        Assert.Equal(3, handler.Requests.Count(request => request.Uri.AbsolutePath == "/api/v2/oauth/token"));
    }

    [Fact]
    public async Task WithoutACredential_EveryCallRefuses_BeforeAnythingIsSent()
    {
        var handler = new RecordingHandler(TailnetApi);
        var provisioner = new TailscaleApiProvisioner(
            new HttpClient(handler),
            credential: null,
            TimeProvider.System,
            NullLogger<TailscaleApiProvisioner>.Instance);

        await Assert.ThrowsAsync<ConnectNotConfiguredException>(() => provisioner.CreateFriendAuthKeyAsync("mem_test0001", CancellationToken.None));
        await Assert.ThrowsAsync<ConnectNotConfiguredException>(() => provisioner.CreateHostAuthKeyAsync(CancellationToken.None));
        await Assert.ThrowsAsync<ConnectNotConfiguredException>(() => provisioner.DeleteAuthKeyAsync(KeyId, CancellationToken.None));
        await Assert.ThrowsAsync<ConnectNotConfiguredException>(() => provisioner.GetDeviceAsync(NodeId, CancellationToken.None));
        await Assert.ThrowsAsync<ConnectNotConfiguredException>(() => provisioner.DeleteDeviceAsync(NodeId, CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("../keys")]
    [InlineData("n1/../../tailnet")]
    [InlineData("n1?fields=all")]
    [InlineData("")]
    public async Task IdsThatAreNotPlainLettersAndDigits_AreRefusedBeforeAnythingIsSent(string id)
    {
        var handler = new RecordingHandler(TailnetApi);
        var provisioner = CreateProvisioner(handler);

        await Assert.ThrowsAsync<ArgumentException>(() => provisioner.DeleteAuthKeyAsync(id, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => provisioner.GetDeviceAsync(id, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => provisioner.DeleteDeviceAsync(id, CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("token-refused")]
    [InlineData("key-refused")]
    [InlineData("wrong-kind-of-key")]
    [InlineData("unreadable-response")]
    [InlineData("network-failure")]
    public async Task Failures_NeverCarryASecret_InTheExceptionOrTheLog(string failure)
    {
        var logger = new ConnectCapturingLogger<TailscaleApiProvisioner>();
        var handler = new RecordingHandler((request, body) =>
        {
            var isToken = request.RequestUri!.AbsolutePath == "/api/v2/oauth/token";
            return failure switch
            {
                "token-refused" when isToken => Respond(
                    HttpStatusCode.Unauthorized,
                    "{\"message\":\"invalid client_secret \\\"" + ClientSecret + "\\\"\"}"),
                "key-refused" when !isToken => Respond(
                    HttpStatusCode.BadRequest,
                    "{\"message\":\"cannot issue " + AuthKey + "\"}"),
                "wrong-kind-of-key" when !isToken => Respond(
                    HttpStatusCode.OK,
                    "{\"id\":\"" + KeyId + "\",\"key\":\"" + ClientSecret + "\"}"),
                "unreadable-response" when !isToken => Respond(HttpStatusCode.OK, "not json " + AuthKey),
                "network-failure" => throw new HttpRequestException("connection reset while sending client_secret=" + ClientSecret),
                _ => TailnetApi(request, body)
            };
        });
        var provisioner = new TailscaleApiProvisioner(
            new HttpClient(handler),
            new TailscaleOAuthCredential(ClientId, ClientSecret),
            TimeProvider.System,
            logger);

        var exception = await Assert.ThrowsAsync<ConnectProvisioningException>(() =>
            provisioner.CreateFriendAuthKeyAsync("mem_test0001", CancellationToken.None));

        Assert.DoesNotContain(SecretMarker, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(AccessToken, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("tskey-", exception.Message, StringComparison.Ordinal);
        Assert.All(logger.Entries, entry =>
        {
            Assert.DoesNotContain(SecretMarker, entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(AccessToken, entry.Message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void TheCredential_NeverPrintsItsSecret_AndOnlyAcceptsAClientSecret()
    {
        var credential = new TailscaleOAuthCredential(ClientId, ClientSecret);

        Assert.DoesNotContain(SecretMarker, credential.ToString(), StringComparison.Ordinal);
        Assert.Contains(ClientId, credential.ToString(), StringComparison.Ordinal);
        foreach (var wrong in new[] { AuthKey, "tskey-client-kTest with space", "plain-secret-value" })
        {
            var exception = Assert.Throws<ArgumentException>(() => new TailscaleOAuthCredential(ClientId, wrong));
            Assert.DoesNotContain(wrong, exception.Message, StringComparison.Ordinal);
        }

        // The bare prefix is not a secret either.
        Assert.Throws<ArgumentException>(() => new TailscaleOAuthCredential(ClientId, TailscaleOAuthCredential.ClientSecretPrefix));
    }

    private static TailscaleApiProvisioner CreateProvisioner(RecordingHandler handler, TimeProvider? clock = null) =>
        new(
            new HttpClient(handler),
            new TailscaleOAuthCredential(ClientId, ClientSecret),
            clock ?? TimeProvider.System,
            NullLogger<TailscaleApiProvisioner>.Instance);

    /// <summary>What the tailnet API answers when everything works.</summary>
    private static HttpResponseMessage TailnetApi(HttpRequestMessage request, string? body)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (request.Method == HttpMethod.Post && path == "/api/v2/oauth/token")
        {
            return Respond(HttpStatusCode.OK, "{\"access_token\":\"" + AccessToken + "\",\"token_type\":\"Bearer\",\"expires_in\":3600}");
        }

        if (request.Method == HttpMethod.Post && path == "/api/v2/tailnet/-/keys")
        {
            return Respond(HttpStatusCode.OK, "{\"id\":\"" + KeyId + "\",\"key\":\"" + AuthKey + "\",\"expirySeconds\":86400}");
        }

        if (request.Method == HttpMethod.Get && path == "/api/v2/device/" + NodeId)
        {
            return Respond(
                HttpStatusCode.OK,
                "{\"nodeId\":\"" + NodeId + "\",\"tags\":[\"tag:onesalem-client\"],\"created\":\"2026-01-01T00:00:00Z\"," +
                "\"hostname\":\"friend-pc\",\"addresses\":[\"100.100.10.20\",\"fd7a:115c:a1e0::20\"],\"isEphemeral\":false,\"tailnetLockError\":null}");
        }

        if (request.Method == HttpMethod.Get && path == "/api/v2/tailnet/-/acl")
        {
            return Respond(HttpStatusCode.OK, "{\"tagOwners\":{\"tag:onesalem-host\":[\"autogroup:admin\"],\"tag:onesalem-client\":[\"tag:onesalem-host\"]}}");
        }

        if (request.Method == HttpMethod.Delete)
        {
            return Respond(HttpStatusCode.OK, string.Empty);
        }

        return Respond(HttpStatusCode.NotFound, "{}");
    }

    private static HttpResponseMessage Respond(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static Dictionary<string, string> ParseForm(string body) =>
        body.Split('&')
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(
                pair => Uri.UnescapeDataString(pair[0].Replace('+', ' ')),
                pair => Uri.UnescapeDataString(pair[1].Replace('+', ' ')));

    private sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? Authorization, string? ContentType, IReadOnlyList<string> Accept, string? Body);

    private sealed class RecordingHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private readonly List<RecordedRequest> _requests = [];

        public IReadOnlyList<RecordedRequest> Requests
        {
            get
            {
                lock (_requests)
                {
                    return [.. _requests];
                }
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (_requests)
            {
                _requests.Add(new RecordedRequest(
                    request.Method,
                    request.RequestUri!,
                    request.Headers.Authorization?.ToString(),
                    request.Content?.Headers.ContentType?.MediaType,
                    request.Headers.Accept.Select(value => value.MediaType ?? string.Empty).ToArray(),
                    body));
            }

            return respond(request, body);
        }
    }
}
