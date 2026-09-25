using System.Security.Cryptography;
using ServerManager.Connect.Core.Crypto;
using ServerManager.Connect.Core.Tickets;
using ServerManager.Infrastructure.Connect;

namespace ServerManager.Infrastructure.Tests;

/// <summary>
/// The Agent's table of allowed bridged connections: what a close names, and that nothing the
/// host transport will never report can hold a slot for good.
/// </summary>
public sealed class ConnectLiveConnectionsTests
{
    [Fact]
    public void AConnectionRecordsItsFriendAndTheEndpointItWasAllowedTo()
    {
        var table = new ConnectLiveConnections();
        var claims = Claims(ConnectTestServerStore.Minecraft);
        table.TryAdd("c1", claims, 25565);

        ConnectLiveConnections.LiveConnection? seen = null;
        table.RequestClose(connection =>
        {
            seen = connection;
            return false;
        });

        Assert.NotNull(seen);
        Assert.Equal(claims.DeviceId, seen.DeviceId);
        Assert.Equal(claims.MembershipId, seen.MembershipId);
        Assert.Equal(claims.TicketId, seen.TicketId);
        Assert.Equal(ConnectTestServerStore.Minecraft, seen.ServerId);
        Assert.Equal(25565, seen.LocalPort);
    }

    [Fact]
    public void AFullTable_RefusesInsteadOfTrackingLoosely()
    {
        var table = new ConnectLiveConnections(capacity: 1);

        Assert.True(table.TryAdd("c1", Claims(ConnectTestServerStore.Minecraft), 25565));
        Assert.False(table.TryAdd("c2", Claims(ConnectTestServerStore.Minecraft), 25565));
        Assert.False(table.TryAdd("c1", Claims(ConnectTestServerStore.Minecraft), 25565));
        Assert.True(table.Remove("c1"));
        Assert.True(table.TryAdd("c2", Claims(ConnectTestServerStore.Minecraft), 25565));
    }

    [Fact]
    public void ALostSubscription_ClosesEveryConnection_ThenForgetsThemAfterTheirRepeats()
    {
        var table = new ConnectLiveConnections(capacity: 2);
        table.TryAdd("c1", Claims(ConnectTestServerStore.Minecraft), 25565);
        table.TryAdd("c2", Claims(ConnectTestServerStore.SecondMinecraft), 25566);

        table.SubscriptionLost();

        // The next subscription is told about both, however long the transport was away.
        Assert.Equal(new[] { "c1", "c2" }, table.PendingClose().Order());
        Assert.Equal(new[] { "c1", "c2" }, table.PendingClose().Order());
        for (var repeat = 1; repeat <= ConnectLiveConnections.CloseRepeats; repeat++)
        {
            Assert.Equal(new[] { "c1", "c2" }, table.TakeCloseRepeats().Order());
        }

        Assert.Equal(0, table.Count);
        Assert.Empty(table.TakeCloseRepeats());
        Assert.True(table.TryAdd("c3", Claims(ConnectTestServerStore.Minecraft), 25565));
    }

    [Fact]
    public void ACloseTheTransportNeverConfirms_IsForgottenAfterItsLastRepeat()
    {
        // The allow reached the transport after it stopped waiting, so it never learned the id
        // and will never report it closed.
        var table = new ConnectLiveConnections();
        var revoked = Claims(ConnectTestServerStore.Minecraft);
        table.TryAdd("never-learned", revoked, 25565);
        table.TryAdd("kept", Claims(ConnectTestServerStore.Minecraft), 25565);

        Assert.Equal(new[] { "never-learned" }, table.RequestClose(connection => connection.DeviceId == revoked.DeviceId));
        for (var repeat = 1; repeat <= ConnectLiveConnections.CloseRepeats; repeat++)
        {
            Assert.Equal(new[] { "never-learned" }, table.TakeCloseRepeats());
        }

        Assert.Equal(1, table.Count);
        Assert.Empty(table.PendingClose());
    }

    [Fact]
    public void AskingAgain_RenewsTheRepeats()
    {
        var table = new ConnectLiveConnections();
        table.TryAdd("c1", Claims(ConnectTestServerStore.Minecraft), 25565);
        table.RequestClose(_ => true);
        table.TakeCloseRepeats();
        table.TakeCloseRepeats();

        Assert.Equal(new[] { "c1" }, table.RequestClose(_ => true));
        for (var repeat = 1; repeat <= ConnectLiveConnections.CloseRepeats; repeat++)
        {
            Assert.Equal(new[] { "c1" }, table.TakeCloseRepeats());
        }

        Assert.Equal(0, table.Count);
    }

    private static TicketClaims Claims(Guid serverId) => new()
    {
        Issuer = TicketVerifier.ExpectedIssuer,
        Audience = ConnectTestBroker.NewOwnerId(),
        TicketId = Base64Url.Encode(RandomNumberGenerator.GetBytes(16)),
        DeviceId = ConnectTestBroker.NewDeviceId(),
        MembershipId = ConnectTestBroker.MembershipId,
        ServerId = serverId,
        Protocol = TicketVerifier.SupportedProtocol,
        NodeId = ConnectTestBroker.NodeId,
        SessionPublicKey = "unused-by-the-table",
        HostBridge = "100.64.0.7:7780",
        AuthorizationVersion = 1,
        IssuedAt = ConnectTestBroker.StartUnix,
        NotBefore = ConnectTestBroker.StartUnix,
        ExpiresAt = ConnectTestBroker.StartUnix + 600
    };
}
