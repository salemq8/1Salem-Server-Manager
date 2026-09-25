using System.Net;
using System.Text.Json;
using ServerManager.Connect.Core.Crypto;
using ServerManager.Connect.Core.Tickets;
using ServerManager.Connect.Tests.Support;

namespace ServerManager.Connect.Tests;

/// <summary>
/// Fixtures/ticket-vector.json is shared with the Go transport and the TypeScript broker tests.
/// It holds public material only: the private keys that signed it were thrown away, so the
/// vector can be checked but never re-signed or extended.
/// </summary>
public sealed class TicketVectorTests
{
    private static readonly string VectorPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ticket-vector.json");

    [Fact]
    public void Vector_VerifiesWithItsFixedClock()
    {
        using var vector = JsonDocument.Parse(File.ReadAllText(VectorPath));
        var root = vector.RootElement;
        var claims = root.GetProperty("claims");
        var clock = new FixedTimeProvider(root.GetProperty("fixedNowUnix").GetInt64());
        var serverId = Guid.Parse(claims.GetProperty("sid").GetString()!);
        var keys = TicketKeySet.Parse(root.GetProperty("keyset").GetRawText());
        var verifier = new TicketVerifier(
            keys,
            claims.GetProperty("aud").GetString()!,
            new InMemoryCatalog(new ConnectServerEntry(serverId, ConnectGameKind.Minecraft, ConnectProtocol.Tcp, 25565, connectEnabled: true)),
            new RevocationSet(clock),
            clock);
        var nodeId = claims.GetProperty("nid").GetString()!;

        var ticket = verifier.Verify(root.GetProperty("ticket").GetString(), nodeId);
        Assert.True(ticket.IsValid, ticket.Reason.ToString());
        Assert.Equal(claims.GetProperty("jti").GetString(), ticket.Claims!.TicketId);
        Assert.Equal(claims.GetProperty("sub").GetString(), ticket.Claims.DeviceId);
        Assert.Equal(claims.GetProperty("mid").GetString(), ticket.Claims.MembershipId);
        Assert.Equal(claims.GetProperty("hb").GetString(), ticket.Claims.HostBridge);
        Assert.Equal(claims.GetProperty("av").GetInt64(), ticket.Claims.AuthorizationVersion);
        Assert.Equal(claims.GetProperty("exp").GetInt64(), ticket.Claims.ExpiresAt);
        Assert.Equal(root.GetProperty("sessionSpki").GetString(), ticket.Claims.SessionPublicKey);

        var preamble = ConnectionPreamble.FromJson(root.GetProperty("preamble"));
        Assert.Equal(root.GetProperty("ticket").GetString(), preamble.Ticket);
        Assert.True(ConnectionProof.Verify(
            Base64Url.Decode(root.GetProperty("sessionSpki").GetString()),
            ticket.Claims.TicketId,
            preamble.Nonce,
            preamble.Timestamp,
            ticket.Claims.ServerIdText,
            preamble.Proof));

        var decision = new HostAuthorizer(verifier, new ReplayCache(clock)).Authorize(new HostAuthorizationRequest(preamble, nodeId));
        Assert.True(decision.IsAllowed, decision.Reason.ToString());
        Assert.Equal(new IPEndPoint(IPAddress.Loopback, 25565), decision.Endpoint);
    }

    [Fact]
    public void Vector_FailsOutsideItsWindowAndForOtherPeers()
    {
        using var vector = JsonDocument.Parse(File.ReadAllText(VectorPath));
        var root = vector.RootElement;
        var claims = root.GetProperty("claims");
        var serverId = Guid.Parse(claims.GetProperty("sid").GetString()!);
        var catalog = new InMemoryCatalog(new ConnectServerEntry(serverId, ConnectGameKind.Minecraft, ConnectProtocol.Tcp, 25565, connectEnabled: true));
        var keys = TicketKeySet.Parse(root.GetProperty("keyset").GetRawText());
        var ticket = root.GetProperty("ticket").GetString();
        var nodeId = claims.GetProperty("nid").GetString()!;

        var late = new FixedTimeProvider(claims.GetProperty("exp").GetInt64() + 31);
        Assert.Equal(
            AccessDenialReason.Expired,
            new TicketVerifier(keys, claims.GetProperty("aud").GetString()!, catalog, new RevocationSet(late), late).Verify(ticket, nodeId).Reason);

        var now = new FixedTimeProvider(root.GetProperty("fixedNowUnix").GetInt64());
        Assert.Equal(
            AccessDenialReason.WrongPeerNode,
            new TicketVerifier(keys, claims.GetProperty("aud").GetString()!, catalog, new RevocationSet(now), now).Verify(ticket, "nOtherNodeCNTRL").Reason);
    }

    [Fact]
    public void Vector_ContainsNoPrivateKeyMaterial()
    {
        var text = File.ReadAllText(VectorPath);

        Assert.DoesNotContain("PRIVATE", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"d\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("pkcs8", text, StringComparison.OrdinalIgnoreCase);
        using var vector = JsonDocument.Parse(text);
        Assert.Equal(
            new[] { "keyset", "ticket", "sessionSpki", "preamble", "claims", "fixedNowUnix" },
            vector.RootElement.EnumerateObject().Select(member => member.Name).ToArray());
    }
}
