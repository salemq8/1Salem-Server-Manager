using System.Net;
using System.Security.Cryptography;
using System.Text;
using ServerManager.Connect.Core.Broker;
using ServerManager.Connect.Core.Identity;
using ServerManager.Infrastructure.Connect;

namespace ServerManager.Infrastructure.Tests;

public sealed class ConnectOwnerBrokerClientTests
{
    [Theory]
    [InlineData("http://broker.example/", false)]
    [InlineData("http://127.0.0.1/", false)]
    [InlineData("http://broker.example/", true)]
    [InlineData("https://broker.example/path", false)]
    public void UnsafeOrigins_AreRefused(string origin, bool development)
    {
        using var identity = ConnectIdentity.Generate(ConnectIdentityKind.Owner);
        Assert.Throws<ConnectOwnerBrokerEndpointRefusedException>(() =>
            new ConnectOwnerBrokerClient(new Uri(origin), development, identity, TimeProvider.System));
    }

    [Fact]
    public async Task OwnerCalls_AreSigned_AndInviteSecretsNeverPrint()
    {
        using var identity = ConnectIdentity.Generate(ConnectIdentityKind.Owner);
        var handler = new BrokerHandler((request, _) => request.RequestUri!.AbsolutePath switch
        {
            "/v1/owners" => Json(HttpStatusCode.Created, $"{{\"ownerId\":\"{identity.KeyId}\"}}"),
            "/v1/invites" => Json(HttpStatusCode.Created, "{\"inviteId\":\"inv_test\",\"secret\":\"invite-secret-marker\",\"expiresAt\":1900000000}"),
            _ => Json(HttpStatusCode.OK, "{}")
        });
        using var client = new ConnectOwnerBrokerClient(new Uri("https://broker.example/"), false, identity, TimeProvider.System, handler);

        Assert.Equal(identity.KeyId, await client.RegisterOwnerAsync(CancellationToken.None));
        var invite = await client.CreateInviteAsync(Guid.NewGuid(), 3600, CancellationToken.None);

        Assert.Equal("invite-secret-marker", invite.Secret);
        Assert.DoesNotContain("invite-secret-marker", invite.ToString(), StringComparison.Ordinal);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal(identity.KeyId, request.Key);
            Assert.NotNull(request.Signature);
            Assert.NotNull(request.Nonce);
            Assert.NotNull(request.Time);
            Assert.Equal("application/json", request.Accept);
        });
    }

    [Fact]
    public async Task RedirectsAndTypedBrokerErrors_AreNotFollowedOrEchoed()
    {
        using var identity = ConnectIdentity.Generate(ConnectIdentityKind.Owner);
        var redirect = true;
        var handler = new BrokerHandler((_, _) => redirect
            ? new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://evil.example/") }, Content = new StringContent("secret response") }
            : Json(HttpStatusCode.Conflict, "{\"error\":\"cursor_ahead\",\"detail\":\"sensitive-marker\"}"));
        using var client = new ConnectOwnerBrokerClient(new Uri("https://broker.example/"), false, identity, TimeProvider.System, handler);

        var rejected = await Assert.ThrowsAsync<ConnectOwnerBrokerException>(() => client.RegisterOwnerAsync(CancellationToken.None));
        Assert.Equal(ConnectOwnerBrokerFailure.Rejected, rejected.Failure);
        Assert.Single(handler.Requests);

        redirect = false;
        var conflict = await Assert.ThrowsAsync<ConnectOwnerBrokerException>(() => client.GetRevocationsAsync(9, CancellationToken.None));
        Assert.Equal(ConnectOwnerBrokerFailure.Conflict, conflict.Failure);
        Assert.Equal("cursor_ahead", conflict.ErrorCode);
        Assert.DoesNotContain("sensitive-marker", conflict.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResponsesOver64KiB_AreRefused()
    {
        using var identity = ConnectIdentity.Generate(ConnectIdentityKind.Owner);
        var handler = new BrokerHandler((_, _) => Json(HttpStatusCode.OK, new string('x', 70_000)));
        using var client = new ConnectOwnerBrokerClient(new Uri("https://broker.example/"), false, identity, TimeProvider.System, handler);

        var exception = await Assert.ThrowsAsync<ConnectOwnerBrokerException>(() => client.RegisterOwnerAsync(CancellationToken.None));
        Assert.Equal(ConnectOwnerBrokerFailure.InvalidResponse, exception.Failure);
    }

    [Fact]
    public async Task EveryOwnerRoute_HasTheExpectedMethodAndPath()
    {
        using var identity = ConnectIdentity.Generate(ConnectIdentityKind.Owner);
        var handler = new BrokerHandler((request, _) => request.RequestUri!.AbsolutePath switch
        {
            "/v1/owners/me/memberships" => Json(HttpStatusCode.OK, "{\"memberships\":[]}"),
            "/v1/owners/me/revocations" => Json(HttpStatusCode.OK, "{\"revocations\":[],\"cursor\":0,\"more\":false}"),
            "/v1/memberships/mem_test/revoke" => Json(HttpStatusCode.OK, "{\"membershipId\":\"mem_test\",\"state\":\"revoked\",\"av\":2}"),
            "/v1/keys" => Json(HttpStatusCode.OK, "{\"keys\":[{}]}"),
            _ => Json(HttpStatusCode.OK, "{}")
        });
        using var client = new ConnectOwnerBrokerClient(new Uri("https://broker.example/"), false, identity, TimeProvider.System, handler);
        var serverId = Guid.Parse("11111111-2222-3333-4444-555555555555");

        await client.PutServerAsync(serverId, "Home", "100.100.1.2:7780", CancellationToken.None);
        await client.RevokeInviteAsync("inv_test", CancellationToken.None);
        Assert.Empty(await client.GetMembershipsAsync(CancellationToken.None));
        await client.ApproveMembershipAsync("mem_test", CancellationToken.None);
        await client.RejectMembershipAsync("mem_test", CancellationToken.None);
        await client.PutEnrollmentAsync("mem_test", "ciphertext", CancellationToken.None);
        await client.ConfirmNodeAsync("mem_test", "nNode1", CancellationToken.None);
        await client.RejectNodeAsync("mem_test", "nNode1", CancellationToken.None);
        await client.RevokeMembershipAsync("mem_test", CancellationToken.None);
        await client.RevokeDeviceAsync("dev_test", CancellationToken.None);
        await client.RevokeSessionAsync("ticket_test", CancellationToken.None);
        await client.GetRevocationsAsync(0, CancellationToken.None);
        CryptographicOperations.ZeroMemory(await client.GetTicketKeysAsync(CancellationToken.None));

        Assert.Equal(
            new[]
            {
                $"PUT /v1/servers/{serverId:D}",
                "POST /v1/invites/inv_test/revoke",
                "GET /v1/owners/me/memberships",
                "POST /v1/memberships/mem_test/approve",
                "POST /v1/memberships/mem_test/reject",
                "POST /v1/memberships/mem_test/enrollment",
                "POST /v1/memberships/mem_test/node/confirm",
                "POST /v1/memberships/mem_test/node/reject",
                "POST /v1/memberships/mem_test/revoke",
                "POST /v1/devices/dev_test/revoke",
                "POST /v1/sessions/ticket_test/revoke",
                "GET /v1/owners/me/revocations",
                "GET /v1/keys"
            },
            handler.Requests.Select(request => request.Method + " " + request.Path));
        Assert.Null(handler.Requests[^1].Key);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed record BrokerRequest(string Method, string Path, string? Key, string? Signature, string? Nonce, string? Time, string? Accept, string? Body);

    private sealed class BrokerHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> response) : HttpMessageHandler
    {
        public List<BrokerRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new BrokerRequest(
                request.Method.Method,
                request.RequestUri!.AbsolutePath,
                Header(request, SignedRequestSigner.KeyHeader),
                Header(request, SignedRequestSigner.SignatureHeader),
                Header(request, SignedRequestSigner.NonceHeader),
                Header(request, SignedRequestSigner.TimeHeader),
                request.Headers.Accept.SingleOrDefault()?.MediaType,
                body));
            return response(request, body);
        }

        private static string? Header(HttpRequestMessage request, string name) =>
            request.Headers.TryGetValues(name, out var values) ? values.SingleOrDefault() : null;
    }
}
