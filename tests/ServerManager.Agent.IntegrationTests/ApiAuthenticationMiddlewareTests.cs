using System.Net;
using Microsoft.AspNetCore.Http;
using ServerManager.Core;

namespace ServerManager.Agent.IntegrationTests;

public sealed class ApiAuthenticationMiddlewareTests
{
    private const string LocalKey = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcd";

    [Fact]
    public async Task InvokeAsync_UnauthenticatedLoopbackRequest_IsRejected()
    {
        var probe = new NextProbe();
        var middleware = CreateMiddleware(probe, lanEnabled: false);
        var context = CreateContext(IPAddress.Loopback, "/api/v1/servers");

        await middleware.InvokeAsync(context, new FakePairingService(), new FakeLocalCredential(LocalKey));

        Assert.False(probe.Called);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_InvalidLocalToken_IsRejected()
    {
        var probe = new NextProbe();
        var middleware = CreateMiddleware(probe, lanEnabled: false);
        var context = CreateContext(IPAddress.Loopback, "/api/v1/servers/00000000-0000-0000-0000-000000000000/stop");
        context.Request.Headers.Authorization = "Bearer wrong-token";

        await middleware.InvokeAsync(context, new FakePairingService(), new FakeLocalCredential(LocalKey));

        Assert.False(probe.Called);
        // A credential was presented but it doesn't match -- Forbidden, not "please
        // authenticate" (401 is reserved for a request that presented no credential at all).
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("/api/v1/servers/00000000-0000-0000-0000-000000000000/stop")]
    [InlineData("/api/v1/servers/00000000-0000-0000-0000-000000000000/console")]
    [InlineData("/api/v1/servers/00000000-0000-0000-0000-000000000000/files/delete")]
    [InlineData("/api/v1/servers/00000000-0000-0000-0000-000000000000/backups/00000000-0000-0000-0000-000000000000")]
    [InlineData("/api/v1/backups/00000000-0000-0000-0000-000000000000/restore")]
    [InlineData("/api/v1/playit/stop")]
    public async Task InvokeAsync_ValidLocalCredential_IsAcceptedForPrivilegedEndpoints(string path)
    {
        var probe = new NextProbe();
        var middleware = CreateMiddleware(probe, lanEnabled: false);
        var context = CreateContext(IPAddress.Loopback, path);
        context.Request.Headers.Authorization = $"Bearer {LocalKey}";

        await middleware.InvokeAsync(context, new FakePairingService(), new FakeLocalCredential(LocalKey));

        Assert.True(probe.Called);
        Assert.Equal(true, context.Items["LocalClient"]);
    }

    [Fact]
    public async Task InvokeAsync_PrivilegedEndpointsCannotBeReachedAnonymously_EvenWhenLanEnabled()
    {
        var probe = new NextProbe();
        var middleware = CreateMiddleware(probe, lanEnabled: true);
        var context = CreateContext(IPAddress.Loopback, "/api/v1/servers/00000000-0000-0000-0000-000000000000/start");

        await middleware.InvokeAsync(context, new FakePairingService(), new FakeLocalCredential(LocalKey));

        Assert.False(probe.Called);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_LanRequestWithoutCredential_IsRejected()
    {
        var probe = new NextProbe();
        var middleware = CreateMiddleware(probe, lanEnabled: true);
        var context = CreateContext(IPAddress.Parse("192.168.1.50"), "/api/v1/servers");

        await middleware.InvokeAsync(context, new FakePairingService(), new FakeLocalCredential(LocalKey));

        Assert.False(probe.Called);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_LanRequestWithWrongCredential_IsForbiddenNotUnauthorized()
    {
        // A non-empty but unrecognized LAN credential must be treated the same as every other
        // present-but-wrong credential in this middleware: 403, not 401 (401 is reserved for
        // "no credential presented at all").
        var probe = new NextProbe();
        var middleware = CreateMiddleware(probe, lanEnabled: true);
        var context = CreateContext(IPAddress.Parse("192.168.1.50"), "/api/v1/servers");
        context.Request.Headers.Authorization = "Bearer wrong-lan-token";
        var pairing = new FakePairingService { CredentialToAccept = "correct-lan-token" };

        await middleware.InvokeAsync(context, pairing, new FakeLocalCredential(LocalKey));

        Assert.False(probe.Called);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_LanRequestWithPairedCredential_IsAccepted()
    {
        var probe = new NextProbe();
        var middleware = CreateMiddleware(probe, lanEnabled: true);
        var context = CreateContext(IPAddress.Parse("192.168.1.50"), "/api/v1/servers");
        context.Request.Headers.Authorization = "Bearer lan-token";
        var pairing = new FakePairingService { CredentialToAccept = "lan-token" };

        await middleware.InvokeAsync(context, pairing, new FakeLocalCredential(LocalKey));

        Assert.True(probe.Called);
        Assert.NotNull(context.Items["PairedClient"]);
    }

    [Fact]
    public async Task InvokeAsync_LoopbackCannotSubstituteForLanCredential()
    {
        // A loopback caller with no local key still cannot fall through to LAN-only trust --
        // "127.0.0.1 is not an authentication boundary" applies uniformly.
        var probe = new NextProbe();
        var middleware = CreateMiddleware(probe, lanEnabled: true);
        var context = CreateContext(IPAddress.Loopback, "/api/v1/servers");

        await middleware.InvokeAsync(context, new FakePairingService(), new FakeLocalCredential(LocalKey));

        Assert.False(probe.Called);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_MintingAPairingCode_RequiresTheLocalCredential()
    {
        var probe = new NextProbe();
        var middleware = CreateMiddleware(probe, lanEnabled: true);
        var context = CreateContext(IPAddress.Loopback, "/api/v1/pairing/challenge");

        await middleware.InvokeAsync(context, new FakePairingService(), new FakeLocalCredential(LocalKey));

        Assert.False(probe.Called);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_HealthEndpoint_NeverRequiresAuthentication()
    {
        var probe = new NextProbe();
        var middleware = CreateMiddleware(probe, lanEnabled: false);
        var context = CreateContext(IPAddress.Parse("203.0.113.9"), "/health");

        await middleware.InvokeAsync(context, new FakePairingService(), new FakeLocalCredential(LocalKey));

        Assert.True(probe.Called);
    }

    [Fact]
    public async Task InvokeAsync_PairingComplete_RemainsReachableWithoutAuthentication()
    {
        var probe = new NextProbe();
        var middleware = CreateMiddleware(probe, lanEnabled: true);
        var context = CreateContext(IPAddress.Parse("203.0.113.9"), "/api/v1/pairing/complete");

        await middleware.InvokeAsync(context, new FakePairingService(), new FakeLocalCredential(LocalKey));

        Assert.True(probe.Called);
    }

    private static ApiAuthenticationMiddleware CreateMiddleware(NextProbe probe, bool lanEnabled) =>
        new(
            context =>
            {
                probe.Called = true;
                return Task.CompletedTask;
            },
            new AgentOptions(
                Path.GetTempPath(),
                "test-pipe",
                "http://127.0.0.1:5251",
                lanEnabled));

    private static DefaultHttpContext CreateContext(IPAddress remoteAddress, string path)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = remoteAddress;
        context.Request.Path = path;
        return context;
    }

    private sealed class NextProbe
    {
        public bool Called { get; set; }
    }

    private sealed class FakeLocalCredential(string expected) : Infrastructure.Security.ILocalAgentCredential
    {
        public bool Validate(string? candidate) =>
            !string.IsNullOrEmpty(candidate) && candidate == expected;
    }

    private sealed class FakePairingService : IPairingService
    {
        public string? CredentialToAccept { get; set; }

        public Task<PairingChallenge> CreateChallengeAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PairingResult> CompleteAsync(
            string code,
            string clientName,
            string clientAddress,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task RevokeAsync(Guid clientId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task RenameAsync(Guid clientId, string name, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<PairedClientRecord>> ListClientsAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PairedClientRecord?> ValidateCredentialAsync(
            string credential,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                CredentialToAccept is not null && credential == CredentialToAccept
                    ? new PairedClientRecord(
                        Guid.NewGuid(),
                        "Test Client",
                        "fingerprint",
                        "hash",
                        DateTimeOffset.UtcNow,
                        null,
                        null)
                    : null);
    }
}
