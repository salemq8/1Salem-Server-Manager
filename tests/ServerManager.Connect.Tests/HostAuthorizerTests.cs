using System.Net;
using System.Text.Json;
using ServerManager.Connect.Core.Crypto;
using ServerManager.Connect.Core.Tickets;
using ServerManager.Connect.Tests.Support;

namespace ServerManager.Connect.Tests;

public sealed class HostAuthorizerTests : IDisposable
{
    private readonly TestTicketIssuer _issuer = new();

    public void Dispose() => _issuer.Dispose();

    [Fact]
    public void ValidProof_IsAllowedToTheLoopbackCatalogEndpoint()
    {
        var result = _issuer.CreateAuthorizer().Authorize(Request(Preamble(_issuer.Issue())));

        Assert.True(result.IsAllowed, result.Reason.ToString());
        Assert.Equal(new IPEndPoint(IPAddress.Loopback, TestTicketIssuer.MinecraftPort), result.Endpoint);
        Assert.True(IPAddress.IsLoopback(result.Endpoint!.Address));
        Assert.True(Base64Url.IsEncodingOfLength(result.ConnectionId, 16));
        Assert.Equal(_issuer.DeviceId, result.Claims!.DeviceId);
    }

    [Fact]
    public void ProofByAnotherKey_IsRefused()
    {
        using var thief = Es256.CreateKey();
        var preamble = ConnectionPreamble.Create(_issuer.Issue(), thief, _issuer.Clock);

        AssertDenied(AccessDenialReason.InvalidProof, preamble);
    }

    [Fact]
    public void ProofForAnotherServer_IsRefused()
    {
        var ticket = _issuer.Issue();
        var jti = TicketId(ticket);
        var nonce = Base64Url.Encode(new byte[16]);
        var ts = _issuer.Clock.UnixSeconds;
        var proof = ConnectionProof.Sign(_issuer.SessionKey, jti, nonce, ts, Guid.NewGuid().ToString("D"));

        AssertDenied(AccessDenialReason.InvalidProof, new ConnectionPreamble(ticket, nonce, ts, proof));
    }

    [Theory]
    [InlineData(-61)]
    [InlineData(61)]
    [InlineData(-3600)]
    public void ProofOutsideSixtySeconds_IsRefused(int offsetSeconds)
    {
        var ticket = _issuer.Issue();
        var skewedClock = new FixedTimeProvider(_issuer.Clock.UnixSeconds + offsetSeconds);

        AssertDenied(AccessDenialReason.ProofOutsideClockWindow, ConnectionPreamble.Create(ticket, _issuer.SessionKey, skewedClock));
    }

    [Theory]
    [InlineData(-60)]
    [InlineData(60)]
    public void ProofAtTheEdgeOfTheWindow_IsAllowed(int offsetSeconds)
    {
        var ticket = _issuer.Issue();
        var skewedClock = new FixedTimeProvider(_issuer.Clock.UnixSeconds + offsetSeconds);

        Assert.True(_issuer.CreateAuthorizer().Authorize(Request(ConnectionPreamble.Create(ticket, _issuer.SessionKey, skewedClock))).IsAllowed);
    }

    [Fact]
    public void ReplayedPreamble_IsRefused()
    {
        var authorizer = _issuer.CreateAuthorizer();
        var preamble = Preamble(_issuer.Issue());

        Assert.True(authorizer.Authorize(Request(preamble)).IsAllowed);
        var replay = authorizer.Authorize(Request(preamble));

        Assert.False(replay.IsAllowed);
        Assert.Equal(AccessDenialReason.ReplayedNonce, replay.Reason);
        Assert.Null(replay.Endpoint);
    }

    [Fact]
    public void FreshNonceOnTheSameTicket_IsAllowed()
    {
        // Minecraft opens a new TCP connection for every login and server-list ping.
        var authorizer = _issuer.CreateAuthorizer();
        var ticket = _issuer.Issue();

        Assert.True(authorizer.Authorize(Request(Preamble(ticket))).IsAllowed);
        Assert.True(authorizer.Authorize(Request(Preamble(ticket))).IsAllowed);
    }

    [Fact]
    public void ForgedProof_DoesNotBurnTheVictimsNonce()
    {
        var authorizer = _issuer.CreateAuthorizer();
        var genuine = Preamble(_issuer.Issue());
        using var attacker = Es256.CreateKey();
        var forgedProof = ConnectionProof.Sign(attacker, TicketId(genuine.Ticket), genuine.Nonce, genuine.Timestamp, TestTicketIssuer.MinecraftServer.ToString("D"));
        var forged = new ConnectionPreamble(genuine.Ticket, genuine.Nonce, genuine.Timestamp, forgedProof);

        Assert.Equal(AccessDenialReason.InvalidProof, authorizer.Authorize(Request(forged)).Reason);
        Assert.True(authorizer.Authorize(Request(genuine)).IsAllowed);
    }

    [Fact]
    public void TicketDenial_IsPassedThroughWithoutAnEndpoint()
    {
        var result = _issuer.CreateAuthorizer().Authorize(new HostAuthorizationRequest(Preamble(_issuer.Issue()), "nIntruderCNTRL"));

        Assert.False(result.IsAllowed);
        Assert.Equal(AccessDenialReason.WrongPeerNode, result.Reason);
        Assert.Null(result.Endpoint);
        Assert.Null(result.ConnectionId);
    }

    [Fact]
    public void DestinationLikeFields_AreIgnored()
    {
        var preamble = Preamble(_issuer.Issue());
        var json = $$"""
            {
              "id": 7,
              "op": "authorize",
              "endpoint": "10.0.0.5:3389",
              "destination": "192.168.1.1:445",
              "host": "evil.example",
              "port": 22,
              "preamble": {
                "t": "{{preamble.Ticket}}",
                "n": "{{preamble.Nonce}}",
                "ts": {{preamble.Timestamp}},
                "p": "{{preamble.Proof}}",
                "hb": "8.8.8.8:53",
                "endpoint": "0.0.0.0:5251",
                "addr": "203.0.113.9:25565"
              },
              "peer": { "nodeId": "{{TestTicketIssuer.NodeId}}", "addr": "100.64.0.9:41000", "endpoint": "1.1.1.1:80" }
            }
            """;
        using var document = JsonDocument.Parse(json);

        var request = HostAuthorizationRequest.Parse(document.RootElement);
        var result = _issuer.CreateAuthorizer().Authorize(request);

        Assert.True(result.IsAllowed, result.Reason.ToString());
        Assert.Equal(IPAddress.Loopback, result.Endpoint!.Address);
        Assert.Equal(TestTicketIssuer.MinecraftPort, result.Endpoint.Port);
    }

    [Fact]
    public void EveryAllowedEndpoint_IsLoopback()
    {
        foreach (var port in new[] { 1, 25565, 25566, 65535 })
        {
            using var issuer = new TestTicketIssuer();
            var serverId = Guid.NewGuid();
            var authorizer = new HostAuthorizer(
                new TicketVerifier(
                    issuer.KeySet,
                    issuer.OwnerId,
                    new InMemoryCatalog(new ConnectServerEntry(serverId, ConnectGameKind.Minecraft, ConnectProtocol.Tcp, port, connectEnabled: true)),
                    issuer.Revocations,
                    issuer.Clock),
                new ReplayCache(issuer.Clock));
            var ticket = issuer.Issue(claims => claims["sid"] = serverId.ToString("D"));

            var result = authorizer.Authorize(new HostAuthorizationRequest(
                ConnectionPreamble.Create(ticket, issuer.SessionKey, issuer.Clock),
                TestTicketIssuer.NodeId));

            Assert.True(result.IsAllowed);
            Assert.Equal(IPAddress.Loopback, result.Endpoint!.Address);
            Assert.Equal(port, result.Endpoint.Port);
        }
    }

    [Fact]
    public void ATicketThatUsedUpItsConnections_IsRefused_ButTheFriendsNextTicketIsNot()
    {
        var authorizer = _issuer.CreateAuthorizer();
        var ticket = _issuer.Issue();
        for (var connection = 0; connection < ReplayCache.DefaultPerTicketLimit; connection++)
        {
            Assert.True(authorizer.Authorize(Request(Preamble(ticket))).IsAllowed);
        }

        var overLimit = authorizer.Authorize(Request(Preamble(ticket)));

        Assert.False(overLimit.IsAllowed);
        Assert.Equal(AccessDenialReason.TicketConnectionLimitReached, overLimit.Reason);
        Assert.Null(overLimit.Endpoint);
        Assert.True(authorizer.Authorize(Request(Preamble(_issuer.Issue()))).IsAllowed);
    }

    [Fact]
    public void AFullReplayCache_RefusesInsteadOfEvicting()
    {
        var authorizer = new HostAuthorizer(_issuer.CreateVerifier(), new ReplayCache(_issuer.Clock, capacity: 1));
        var first = Preamble(_issuer.Issue());

        Assert.True(authorizer.Authorize(Request(first)).IsAllowed);
        var full = authorizer.Authorize(Request(Preamble(_issuer.Issue())));

        Assert.Equal(AccessDenialReason.ReplayCacheFull, full.Reason);
        Assert.Equal(AccessDenialReason.ReplayedNonce, authorizer.Authorize(Request(first)).Reason);
    }

    [Fact]
    public void PreambleFrame_RoundTripsAndRejectsOversizeOrForeignData()
    {
        var preamble = Preamble(_issuer.Issue());
        var frame = PreambleCodec.Encode(preamble);

        Assert.Equal("1SC\x01"u8.ToArray(), frame[..4]);
        Assert.Equal(preamble, PreambleCodec.Decode(frame));

        var oversize = (byte[])frame.Clone();
        oversize[4] = 0x10;
        oversize[5] = 0x01;
        Assert.Throws<InvalidDataException>(() => PreambleCodec.Decode(oversize));

        var foreign = (byte[])frame.Clone();
        foreign[0] = (byte)'G';
        Assert.Throws<InvalidDataException>(() => PreambleCodec.Decode(foreign));
        Assert.Throws<InvalidDataException>(() => PreambleCodec.Decode(frame.Concat(new byte[] { 0 }).ToArray()));
    }

    private ConnectionPreamble Preamble(string ticket) =>
        ConnectionPreamble.Create(ticket, _issuer.SessionKey, _issuer.Clock);

    private static HostAuthorizationRequest Request(ConnectionPreamble preamble) =>
        new(preamble, TestTicketIssuer.NodeId);

    private static string TicketId(string ticket)
    {
        using var payload = JsonDocument.Parse(Base64Url.Decode(ticket.Split('.')[1]));
        return payload.RootElement.GetProperty("jti").GetString()!;
    }

    private void AssertDenied(AccessDenialReason expected, ConnectionPreamble preamble)
    {
        var result = _issuer.CreateAuthorizer().Authorize(Request(preamble));
        Assert.False(result.IsAllowed);
        Assert.Equal(expected, result.Reason);
        Assert.Null(result.Endpoint);
    }
}
