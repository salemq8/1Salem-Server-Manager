using System.Net;
using System.Text;
using System.Text.Json;
using ServerManager.Connect.App.Broker;
using ServerManager.Connect.App.Configuration;
using ServerManager.Connect.App.Tests.Fakes;

namespace ServerManager.Connect.App.Tests;

public sealed class BrokerClientTests
{
    /// <summary>Bounds a test whose subject should have given up long before.</summary>
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);

    [Theory]
    [InlineData("http://broker.example/", true)]
    [InlineData("http://broker.example/", false)]
    [InlineData("http://192.168.1.10:8787/", true)]
    [InlineData("http://127.0.0.2:8787/", true)]
    [InlineData("http://localhost.evil.test/", true)]
    [InlineData("http://[::1]:8787/", true)]
    [InlineData("http://127.0.0.1:8787/", false)]
    [InlineData("http://localhost:8787/", false)]
    [InlineData("https://broker.example/v1/", false)]
    [InlineData("https://user:pass@broker.example/", false)]
    [InlineData("https://broker.example/?x=1", false)]
    public void Broker_client_refuses_addresses_outside_the_policy(string address, bool developmentMode)
    {
        using var identity = TestIds.NewDevice();

        Assert.Throws<BrokerEndpointRefusedException>(() => new BrokerClient(new Uri(address), developmentMode, identity));
    }

    [Theory]
    [InlineData("https://broker.example/", false)]
    [InlineData("https://broker.example:8443/", false)]
    [InlineData("https://onesalem-connect-broker-production.onesalemconnect.workers.dev/", false)]
    [InlineData("http://127.0.0.1:8787/", true)]
    [InlineData("http://localhost:8787/", true)]
    public void Broker_client_accepts_https_and_local_http_in_development(string address, bool developmentMode)
    {
        using var identity = TestIds.NewDevice();

        using var client = new BrokerClient(new Uri(address), developmentMode, identity);

        Assert.True(BrokerEndpointPolicy.IsAllowed(new Uri(address), developmentMode, out _));
    }

    [Fact]
    public async Task Redeem_sends_the_secret_in_a_signed_body_never_in_the_url()
    {
        using var identity = TestIds.NewDevice();
        var handler = new RecordingHandler(HttpStatusCode.Created, """{"membershipId":"mem_aaaaaaaaaaaaaaaaaaaaaaaaaa","state":"pending","serverLabel":"Salem's world"}""");
        using var client = new BrokerClient(new Uri("https://broker.test/"), false, identity, handler);
        var secret = TestIds.NewInviteSecret();

        var redemption = await client.RedeemInviteAsync(secret, CancellationToken.None);

        Assert.Equal(new InviteRedemption("mem_aaaaaaaaaaaaaaaaaaaaaaaaaa", "Salem's world"), redemption);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal("https://broker.test/v1/invites/redeem", request.Uri);
        Assert.DoesNotContain(secret, request.Uri, StringComparison.Ordinal);
        Assert.Equal(secret, JsonDocument.Parse(request.Body).RootElement.GetProperty("secret").GetString());
        Assert.Equal(identity.DeviceId, request.Headers["X-1S-Key"]);
        Assert.True(request.Headers.ContainsKey("X-1S-Time"));
        Assert.True(request.Headers.ContainsKey("X-1S-Nonce"));
        Assert.True(request.Headers.ContainsKey("X-1S-Sig"));
    }

    [Fact]
    public async Task Not_found_and_rate_limits_map_to_generic_failures()
    {
        using var identity = TestIds.NewDevice();
        var notFound = new RecordingHandler(HttpStatusCode.NotFound, """{"error":"not_found"}""");
        using var first = new BrokerClient(new Uri("https://broker.test/"), false, identity, notFound);
        var limited = new RecordingHandler((HttpStatusCode)429, """{"error":"rate_limited"}""", retryAfterSeconds: 30);
        using var second = new BrokerClient(new Uri("https://broker.test/"), false, identity, limited);

        var missing = await Assert.ThrowsAsync<BrokerException>(() => first.RedeemInviteAsync(TestIds.NewInviteSecret(), CancellationToken.None));
        var throttled = await Assert.ThrowsAsync<BrokerException>(() => second.GetMembershipsAsync(CancellationToken.None));

        Assert.Equal(BrokerFailure.NotFound, missing.Failure);
        Assert.Equal("not_found", missing.ErrorCode);
        Assert.Equal(BrokerFailure.RateLimited, throttled.Failure);
        Assert.Equal("rate_limited", throttled.ErrorCode);
        Assert.Equal(TimeSpan.FromSeconds(30), throttled.RetryAfter);
    }

    [Fact]
    public async Task Missing_enrollment_is_null_and_a_malformed_membership_list_is_refused()
    {
        using var identity = TestIds.NewDevice();
        var notFound = new RecordingHandler(HttpStatusCode.NotFound, """{"error":"not_found"}""");
        using var first = new BrokerClient(new Uri("https://broker.test/"), false, identity, notFound);
        var malformed = new RecordingHandler(HttpStatusCode.OK, """{"memberships":[{"membershipId":"../../v1/owners","ownerId":"x","serverId":"s","serverLabel":"l","state":"approved"}]}""");
        using var second = new BrokerClient(new Uri("https://broker.test/"), false, identity, malformed);

        Assert.Null(await first.TakeEnrollmentAsync(TestIds.MembershipId(), CancellationToken.None));
        var invalid = await Assert.ThrowsAsync<BrokerException>(() => second.GetMembershipsAsync(CancellationToken.None));
        Assert.Equal(BrokerFailure.InvalidResponse, invalid.Failure);
    }

    [Theory]
    [InlineData("candidate", MembershipNodeState.Candidate, false)]
    [InlineData("confirmed", MembershipNodeState.Confirmed, true)]
    [InlineData("rejected", MembershipNodeState.Rejected, false)]
    [InlineData("future_state", MembershipNodeState.Unknown, false)]
    public async Task Membership_node_state_is_parsed_and_only_confirmed_can_connect(
        string wireState,
        MembershipNodeState expected,
        bool canConnect)
    {
        using var identity = TestIds.NewDevice();
        var ownerId = TestIds.NewOwnerId();
        var handler = new RecordingHandler(HttpStatusCode.OK, $$"""
            {"memberships":[{"membershipId":"mem_aaaaaaaaaaaaaaaaaaaaaaaaaa","ownerId":"{{ownerId}}",
             "serverId":"5b0f7f2e-3c1a-4d57-9a7e-2f1d8c0b6a41","serverLabel":"Home","state":"approved",
             "nodeId":"nFAKE1CNTRL","nodeState":"{{wireState}}"}]}
            """);
        using var client = new BrokerClient(new Uri("https://broker.test/"), false, identity, handler);

        var membership = Assert.Single(await client.GetMembershipsAsync(CancellationToken.None));

        Assert.Equal(expected, membership.NodeState);
        Assert.Equal(canConnect, membership.CanConnect);
    }

    [Fact]
    public async Task Registration_must_echo_the_id_derived_from_our_key()
    {
        using var identity = TestIds.NewDevice();
        using var other = TestIds.NewDevice();
        var handler = new RecordingHandler(HttpStatusCode.Created, $$"""{"deviceId":"{{other.DeviceId}}"}""");
        using var client = new BrokerClient(new Uri("https://broker.test/"), false, identity, handler);

        var failure = await Assert.ThrowsAsync<BrokerException>(() => client.RegisterDeviceAsync(CancellationToken.None));

        Assert.Equal(BrokerFailure.InvalidResponse, failure.Failure);
        var body = JsonDocument.Parse(Assert.Single(handler.Requests).Body).RootElement;
        Assert.Equal(identity.PublicKeySpkiBase64Url, body.GetProperty("spki").GetString());
    }

    [Theory]
    [InlineData("253402300800")] // one second after 9999-12-31T23:59:59Z
    [InlineData("-62135596801")] // one second before 0001-01-01T00:00:00Z
    [InlineData("9223372036854775807")]
    public async Task A_session_expiry_outside_what_a_date_can_hold_is_an_invalid_response(string expiresAt)
    {
        using var identity = TestIds.NewDevice();
        var handler = new RecordingHandler(HttpStatusCode.Created, $$"""{"ticket":"eyJ.t.sig","expiresAt":{{expiresAt}},"hostBridge":"127.0.0.1:7780"}""");
        using var client = new BrokerClient(new Uri("https://broker.test/"), false, identity, handler);

        var failure = await Assert.ThrowsAsync<BrokerException>(() => client.CreateSessionAsync(TestIds.MembershipId(), "spki", CancellationToken.None));

        Assert.Equal(BrokerFailure.InvalidResponse, failure.Failure);
    }

    [Fact]
    public async Task A_session_expiry_at_the_last_second_a_date_can_hold_is_accepted()
    {
        using var identity = TestIds.NewDevice();
        var handler = new RecordingHandler(HttpStatusCode.Created, """{"ticket":"eyJ.t.sig","expiresAt":253402300799,"hostBridge":"127.0.0.1:7780"}""");
        using var client = new BrokerClient(new Uri("https://broker.test/"), false, identity, handler);

        var ticket = await client.CreateSessionAsync(TestIds.MembershipId(), "spki", CancellationToken.None);

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(253_402_300_799), ticket.ExpiresAt);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_broker_that_stalls_fails_as_unreachable_within_the_request_time(bool afterHeaders)
    {
        // Callers pass no token of their own; the transport start even holds a gate meanwhile.
        using var identity = TestIds.NewDevice();
        using var client = new BrokerClient(new Uri("https://broker.test/"), false, identity, new StallingHandler(afterHeaders), TimeSpan.FromMilliseconds(300));

        var keys = await Assert.ThrowsAsync<BrokerException>(() => client.GetTicketKeysAsync(CancellationToken.None)).WaitAsync(TestTimeout);
        var memberships = await Assert.ThrowsAsync<BrokerException>(() => client.GetMembershipsAsync(CancellationToken.None)).WaitAsync(TestTimeout);

        Assert.Equal(BrokerFailure.Unreachable, keys.Failure);
        Assert.Equal(BrokerFailure.Unreachable, memberships.Failure);
        Assert.Equal(Localization.Text.ErrorBrokerUnreachable, ViewModels.UserMessages.For(keys));
    }

    [Fact]
    public async Task A_caller_that_gives_up_first_gets_a_cancellation_not_a_broker_failure()
    {
        using var identity = TestIds.NewDevice();
        using var client = new BrokerClient(new Uri("https://broker.test/"), false, identity, new StallingHandler(afterHeaders: true), TimeSpan.FromMinutes(5));
        using var giveUp = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetTicketKeysAsync(giveUp.Token)).WaitAsync(TestTimeout);
    }

    private sealed record RecordedRequest(string Method, string Uri, string Body, Dictionary<string, string> Headers);

    /// <summary>A broker that stops sending: before its headers, or after them in the middle of the body.</summary>
    private sealed class StallingHandler(bool afterHeaders) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!afterHeaders)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StalledBody()) };
        }
    }

    /// <summary>A body whose bytes never arrive: a read ends only when it is cancelled.</summary>
    private sealed class StalledBody : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class RecordingHandler(HttpStatusCode status, string body, int? retryAfterSeconds = null) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Copied now: the client wipes its body buffer once the call returns.
            var content = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(
                request.Method.Method,
                request.RequestUri!.AbsoluteUri,
                content,
                request.Headers.ToDictionary(header => header.Key, header => string.Join(",", header.Value))));
            var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (retryAfterSeconds is { } seconds)
            {
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
            }

            return response;
        }
    }
}
