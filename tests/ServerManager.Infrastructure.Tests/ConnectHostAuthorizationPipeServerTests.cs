using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using ServerManager.Connect.Core.Crypto;
using ServerManager.Connect.Core.Pipes;
using ServerManager.Connect.Core.Tickets;
using ServerManager.Infrastructure.Connect;

namespace ServerManager.Infrastructure.Tests;

/// <summary>
/// The Agent's host authorization pipe, end to end over a real named pipe with a throwaway
/// name. Each client behaves like the Go host transport: owner check first, hello, one
/// connection for authorize/closed and another for subscribe.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ConnectHostAuthorizationPipeServerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(400);

    // The Go bridge refuses an allow whose connId does not match this.
    private static readonly Regex ConnectionIdPattern = new("^[A-Za-z0-9._:-]{1,128}$");

    [Fact]
    public async Task Hello_AnswersVersionOne_OnAPipeOwnedByTheAgentAccount()
    {
        await using var host = new Harness();
        await using var client = await host.ConnectAsync(hello: false);

        var response = await client.CallAsync("hello", new JsonObject { ["v"] = 1 });

        Assert.Equal("{\"id\":1,\"ok\":true,\"v\":1}", response.GetRawText());
    }

    [Fact]
    public async Task NothingIsInterpreted_BeforeASuccessfulHello()
    {
        await using var host = new Harness();
        await using var transport = await host.SubscribeAsync();
        await using var client = await host.ConnectAsync(hello: false);
        var body = ConnectTestBroker.AuthorizeBody(host.Broker.Preamble(host.Broker.Issue(ConnectTestServerStore.Minecraft)));

        var early = await client.CallAsync("authorize", body);
        var wrongVersion = await client.CallAsync("hello", new JsonObject { ["v"] = 2 });
        var stillEarly = await client.CallAsync("subscribe");
        var hello = await client.CallAsync("hello", new JsonObject { ["v"] = 1 });
        var afterHello = await client.CallAsync("authorize", body);

        Assert.Equal("{\"id\":1,\"ok\":false,\"error\":\"hello_required\"}", early.GetRawText());
        Assert.Equal("{\"id\":2,\"ok\":false,\"error\":\"unsupported_version\"}", wrongVersion.GetRawText());
        Assert.Equal("hello_required", stillEarly.GetProperty("error").GetString());
        Assert.True(hello.GetProperty("ok").GetBoolean());
        Assert.Equal("allow", afterHello.GetProperty("decision").GetString());
    }

    [Fact]
    public async Task UnknownOperation_IsAnErrorResponse()
    {
        await using var host = new Harness();
        await using var client = await host.ConnectAsync();

        var response = await client.CallAsync("status");

        Assert.Equal("{\"id\":2,\"ok\":false,\"error\":\"unknown_op\"}", response.GetRawText());
    }

    [Fact]
    public async Task Authorize_AllowsTheRegisteredPortOnLoopback_WithAUsableConnectionId()
    {
        await using var host = new Harness();
        await using var transport = await host.SubscribeAsync();
        await using var requests = await host.ConnectAsync();
        var ticket = host.Broker.Issue(ConnectTestServerStore.Minecraft);

        var response = await requests.AuthorizeAsync(ConnectTestBroker.AuthorizeBody(host.Broker.Preamble(ticket)));

        Assert.True(response.GetProperty("ok").GetBoolean());
        Assert.Equal("allow", response.GetProperty("decision").GetString());
        Assert.Equal($"127.0.0.1:{ConnectTestServerStore.MinecraftPort}", response.GetProperty("endpoint").GetString());
        Assert.Matches(ConnectionIdPattern, response.GetProperty("connId").GetString());
    }

    [Fact]
    public async Task Authorize_IsRefused_WhileNoTransportIsSubscribedToCloseEvents()
    {
        await using var host = new Harness();
        await using var requests = await host.ConnectAsync();
        var ticket = host.Broker.Issue(ConnectTestServerStore.Minecraft);

        var unsubscribed = await requests.AuthorizeAsync(ConnectTestBroker.AuthorizeBody(host.Broker.Preamble(ticket)));
        await using var transport = await host.SubscribeAsync();
        var subscribed = await requests.AuthorizeAsync(ConnectTestBroker.AuthorizeBody(host.Broker.Preamble(ticket)));

        AssertBareDeny(unsubscribed);
        Assert.Equal("allow", subscribed.GetProperty("decision").GetString());
    }

    [Theory]
    [InlineData("wrong-peer-node")]
    [InlineData("unknown-server")]
    [InlineData("palworld-server")]
    [InlineData("connect-disabled")]
    [InlineData("other-owner")]
    [InlineData("expired")]
    [InlineData("forged-ticket")]
    [InlineData("forged-proof")]
    [InlineData("malformed-preamble")]
    public async Task EveryDenial_IsABareDeny_WithoutAReason(string refusal)
    {
        await using var host = new Harness();
        await using var transport = await host.SubscribeAsync();
        await using var requests = await host.ConnectAsync();
        var broker = host.Broker;
        var body = refusal switch
        {
            "wrong-peer-node" => ConnectTestBroker.AuthorizeBody(
                broker.Preamble(broker.Issue(ConnectTestServerStore.Minecraft)), "nSomeoneElseCNTRL"),
            "unknown-server" => Authorize(broker, broker.Issue(Guid.NewGuid())),
            "palworld-server" => Authorize(broker, broker.Issue(ConnectTestServerStore.Palworld)),
            "connect-disabled" => Authorize(broker, broker.Issue(ConnectTestServerStore.SecondMinecraft)),
            "other-owner" => Authorize(broker, broker.Issue(
                ConnectTestServerStore.Minecraft, claims => claims["aud"] = ConnectTestBroker.NewOwnerId())),
            "expired" => Authorize(broker, broker.Issue(ConnectTestServerStore.Minecraft, claims =>
            {
                claims["iat"] = broker.Clock.UnixSeconds - 700;
                claims["nbf"] = broker.Clock.UnixSeconds - 700;
                claims["exp"] = broker.Clock.UnixSeconds - 100;
            })),
            "forged-ticket" => Authorize(broker, ForgeSignature(broker.Issue(ConnectTestServerStore.Minecraft))),
            "forged-proof" => WithForgedProof(broker, broker.Issue(ConnectTestServerStore.Minecraft)),
            "malformed-preamble" => ConnectTestBroker.AuthorizeBody(new JsonObject { ["t"] = "not a ticket" }),
            _ => throw new ArgumentOutOfRangeException(nameof(refusal))
        };

        AssertBareDeny(await requests.AuthorizeAsync(body));
    }

    [Fact]
    public async Task AReplayedPreamble_IsDenied_ButAFreshProofForTheSameTicketIsNot()
    {
        await using var host = new Harness();
        await using var transport = await host.SubscribeAsync();
        await using var requests = await host.ConnectAsync();
        var ticket = host.Broker.Issue(ConnectTestServerStore.Minecraft);
        var captured = ConnectTestBroker.AuthorizeBody(host.Broker.Preamble(ticket));

        var original = await requests.AuthorizeAsync(captured);
        var replay = await requests.AuthorizeAsync(captured);
        var fresh = await requests.AuthorizeAsync(ConnectTestBroker.AuthorizeBody(host.Broker.Preamble(ticket)));

        Assert.Equal("allow", original.GetProperty("decision").GetString());
        AssertBareDeny(replay);
        Assert.Equal("allow", fresh.GetProperty("decision").GetString());
    }

    [Fact]
    public async Task APreambleFromTheWrongPeerNode_IsDenied()
    {
        await using var host = new Harness();
        await using var transport = await host.SubscribeAsync();
        await using var requests = await host.ConnectAsync();
        var ticket = host.Broker.Issue(ConnectTestServerStore.Minecraft);

        // The ticket is bound to the friend's node; the same preamble from another node is refused.
        var stolen = await requests.AuthorizeAsync(
            ConnectTestBroker.AuthorizeBody(host.Broker.Preamble(ticket), "nAttacker01CNTRL"));
        var genuine = await requests.AuthorizeAsync(
            ConnectTestBroker.AuthorizeBody(host.Broker.Preamble(ticket), ConnectTestBroker.NodeId));

        AssertBareDeny(stolen);
        Assert.Equal("allow", genuine.GetProperty("decision").GetString());
    }

    [Fact]
    public async Task ConcurrentClients_AreServedSideBySide_AndTheNameStaysHeld()
    {
        await using var host = new Harness();

        // Three connections open at once: the subscription, and two request connections.
        await using var transport = await host.SubscribeAsync();
        await using var first = await host.ConnectAsync();
        await using var second = await host.ConnectAsync();

        var responses = await Task.WhenAll(
            first.AuthorizeAsync(host.FreshAuthorizeBody()),
            second.AuthorizeAsync(host.FreshAuthorizeBody()));

        Assert.All(responses, response => Assert.Equal("allow", response.GetProperty("decision").GetString()));
        Assert.NotEqual(responses[0].GetProperty("connId").GetString(), responses[1].GetProperty("connId").GetString());
        Assert.Throws<ConnectPipeNameInUseException>(() => ConnectPipeSecurity.CreateFirstInstance(host.PipeName));
    }

    [Fact]
    public async Task PipelinedRequestsOnOneConnection_AreAnsweredInOrderByTheirIds()
    {
        await using var host = new Harness();
        await using var transport = await host.SubscribeAsync();
        await using var requests = await host.ConnectAsync();

        var firstId = await requests.SendAsync("authorize", host.FreshAuthorizeBody());
        var secondId = await requests.SendAsync("authorize", host.FreshAuthorizeBody());
        var firstResponse = await requests.ReadAsync();
        var secondResponse = await requests.ReadAsync();

        Assert.Equal(firstId, firstResponse.GetProperty("id").GetInt64());
        Assert.Equal(secondId, secondResponse.GetProperty("id").GetInt64());
        Assert.Equal("allow", firstResponse.GetProperty("decision").GetString());
        Assert.Equal("allow", secondResponse.GetProperty("decision").GetString());
    }

    [Fact]
    public async Task RevokingADevice_ClosesItsLiveConnections_AndRefusesItsNextOnes()
    {
        await using var host = new Harness();
        await using var transport = await host.SubscribeAsync();
        await using var requests = await host.ConnectAsync();
        var friend = await AllowAsync(requests, host.FreshAuthorizeBody());
        var otherFriend = await AllowAsync(requests, host.OtherFriendAuthorizeBody());

        var closing = await host.Server.RevokeDeviceAsync(host.Broker.DeviceId, host.Cancellation);

        Assert.Equal(new[] { friend }, closing);
        AssertCloseEvent(await transport.ReadAsync(), friend);
        AssertBareDeny(await requests.AuthorizeAsync(host.FreshAuthorizeBody()));
        Assert.Equal("allow", (await requests.AuthorizeAsync(host.OtherFriendAuthorizeBody())).GetProperty("decision").GetString());
        Assert.NotEqual(friend, otherFriend);
    }

    [Fact]
    public async Task RevokingAMembership_ClosesItsLiveConnections_AndRefusesItsNextOnes()
    {
        await using var host = new Harness();
        await using var transport = await host.SubscribeAsync();
        await using var requests = await host.ConnectAsync();
        var friend = await AllowAsync(requests, host.FreshAuthorizeBody());
        await AllowAsync(requests, host.OtherFriendAuthorizeBody());

        var closing = await host.Server.RevokeMembershipAsync(ConnectTestBroker.MembershipId, host.Cancellation);

        Assert.Equal(new[] { friend }, closing);
        AssertCloseEvent(await transport.ReadAsync(), friend);
        AssertBareDeny(await requests.AuthorizeAsync(host.FreshAuthorizeBody()));
    }

    [Fact]
    public async Task RevokingATicket_ClosesWhatItOpened_AndLeavesTheFriendsOtherTicketsAlone()
    {
        await using var host = new Harness();
        await using var transport = await host.SubscribeAsync();
        await using var requests = await host.ConnectAsync();
        var revokedTicket = host.Broker.Issue(ConnectTestServerStore.Minecraft);
        var opened = await AllowAsync(requests, ConnectTestBroker.AuthorizeBody(host.Broker.Preamble(revokedTicket)));

        var closing = await host.Server.RevokeTicketAsync(ConnectTestBroker.TicketIdOf(revokedTicket), host.Cancellation);

        Assert.Equal(new[] { opened }, closing);
        AssertCloseEvent(await transport.ReadAsync(), opened);
        AssertBareDeny(await requests.AuthorizeAsync(ConnectTestBroker.AuthorizeBody(host.Broker.Preamble(revokedTicket))));
        Assert.Equal("allow", (await requests.AuthorizeAsync(host.FreshAuthorizeBody())).GetProperty("decision").GetString());
    }

    [Fact]
    public async Task ClosedReports_AreAcknowledged_AndForgetTheConnection()
    {
        await using var host = new Harness();
        await using var transport = await host.SubscribeAsync();
        await using var requests = await host.ConnectAsync();
        var connection = await AllowAsync(requests, host.FreshAuthorizeBody());

        var closed = await requests.CallAsync("closed", new JsonObject
        {
            ["connId"] = connection,
            ["bytesIn"] = 1_200,
            ["bytesOut"] = 48_000
        });
        var unknown = await requests.CallAsync("closed", new JsonObject { ["connId"] = "never-allowed", ["bytesIn"] = 0, ["bytesOut"] = 0 });
        var malformed = await requests.CallAsync("closed", new JsonObject { ["bytesIn"] = 0 });
        var closing = await host.Server.RevokeDeviceAsync(host.Broker.DeviceId, host.Cancellation);

        Assert.Equal(2, closed.EnumerateObject().Count());
        Assert.True(closed.GetProperty("ok").GetBoolean());
        Assert.True(unknown.GetProperty("ok").GetBoolean());
        Assert.Equal("bad_request", malformed.GetProperty("error").GetString());
        Assert.Empty(closing);
        Assert.Null(await transport.TryReadAsync(Quiet));
    }

    [Fact]
    public async Task ARevocationWhileTheTransportIsReconnecting_ReachesItsNextSubscription()
    {
        await using var host = new Harness();
        var lostSubscription = await host.SubscribeAsync();
        await using var requests = await host.ConnectAsync();
        var connection = await AllowAsync(requests, host.FreshAuthorizeBody());
        await lostSubscription.DisposeAsync();

        var closing = await host.Server.RevokeDeviceAsync(host.Broker.DeviceId, host.Cancellation);
        await using var renewed = await host.ConnectAsync();
        var acknowledgement = await renewed.CallAsync("subscribe");

        Assert.Equal(new[] { connection }, closing);
        Assert.True(acknowledgement.GetProperty("ok").GetBoolean());
        AssertCloseEvent(await renewed.ReadAsync(), connection);
    }

    [Fact]
    public async Task AnUnconfirmedClose_IsRepeated_UntilTheTransportReportsItClosed()
    {
        await using var host = new Harness(closeRepeatInterval: TimeSpan.FromMilliseconds(100));
        await using var transport = await host.SubscribeAsync();
        await using var requests = await host.ConnectAsync();
        var connection = await AllowAsync(requests, host.FreshAuthorizeBody());

        await host.Server.RevokeDeviceAsync(host.Broker.DeviceId, host.Cancellation);
        AssertCloseEvent(await transport.ReadAsync(), connection);
        AssertCloseEvent(await transport.ReadAsync(), connection);
        await requests.CallAsync("closed", new JsonObject { ["connId"] = connection, ["bytesIn"] = 0, ["bytesOut"] = 0 });

        // At most one repeat was already on its way when the report arrived.
        if (await transport.TryReadAsync(Quiet) is { } inFlight)
        {
            AssertCloseEvent(inFlight, connection);
            Assert.Null(await transport.TryReadAsync(Quiet));
        }
    }

    [Fact]
    public async Task CloseRepeats_AreBounded_ForAConnectionTheTransportNeverConfirms()
    {
        await using var host = new Harness(closeRepeatInterval: TimeSpan.FromMilliseconds(50));
        await using var transport = await host.SubscribeAsync();
        await using var requests = await host.ConnectAsync();
        var connection = await AllowAsync(requests, host.FreshAuthorizeBody());

        await host.Server.RevokeDeviceAsync(host.Broker.DeviceId, host.Cancellation);
        for (var sent = 0; sent < 1 + ConnectLiveConnections.CloseRepeats; sent++)
        {
            AssertCloseEvent(await transport.ReadAsync(), connection);
        }

        Assert.Null(await transport.TryReadAsync(Quiet));
    }

    [Fact]
    public async Task ConnectionsOfALostSubscription_AreClosedOnTheNextOne_AndThenFreeTheirSlots()
    {
        await using var host = new Harness(closeRepeatInterval: TimeSpan.FromMilliseconds(50), liveConnectionCapacity: 1);
        var crashed = await host.SubscribeAsync();
        await using var requests = await host.ConnectAsync();
        var connection = await AllowAsync(requests, host.FreshAuthorizeBody());

        // The transport died with the connection open, so it will never report it closed. Its
        // successor may subscribe before the Agent has even seen the old subscription break.
        await crashed.DisposeAsync();
        await using var restarted = await host.SubscribeAsync();

        var closes = 0;
        while (await restarted.TryReadAsync(Quiet) is { } line)
        {
            AssertCloseEvent(line, connection);
            closes++;
        }

        Assert.InRange(closes, ConnectLiveConnections.CloseRepeats, 1 + ConnectLiveConnections.CloseRepeats);
        Assert.Equal("allow", (await requests.AuthorizeAsync(host.FreshAuthorizeBody())).GetProperty("decision").GetString());
    }

    [Fact]
    public async Task ADecisionReadyTooLate_IsDenied_WithoutBeingTrackedOrUsingUpItsNonce()
    {
        await using var host = new Harness();
        await using var transport = await host.SubscribeAsync();
        await using var requests = await host.ConnectAsync();
        var slowRead = host.FreshAuthorizeBody();

        host.Store.Listing = SlowStore(host, ConnectHostAuthorizationPipeServer.DecisionBudget + TimeSpan.FromSeconds(1));
        var late = await requests.AuthorizeAsync(slowRead);
        host.Store.Listing = SlowStore(host, ConnectHostAuthorizationPipeServer.DecisionBudget - TimeSpan.FromSeconds(1));
        var inTime = await AllowAsync(requests, host.FreshAuthorizeBody());
        host.Store.Listing = null;
        var retried = await AllowAsync(requests, slowRead);

        AssertBareDeny(late);
        var tracked = await host.Server.RevokeDeviceAsync(host.Broker.DeviceId, host.Cancellation);
        Assert.Equal(new[] { inTime, retried }.Order(), tracked.Order());
    }

    [Theory]
    [InlineData("device")]
    [InlineData("membership")]
    [InlineData("ticket")]
    public async Task ARevocation_ReleasesTheReplayCacheRoomTheRevokedFriendHeld(string revoked)
    {
        await using var host = new Harness(replayCacheCapacity: 2);
        await using var transport = await host.SubscribeAsync();
        await using var requests = await host.ConnectAsync();
        var ticket = host.Broker.Issue(ConnectTestServerStore.Minecraft);
        await AllowAsync(requests, ConnectTestBroker.AuthorizeBody(host.Broker.Preamble(ticket)));
        await AllowAsync(requests, ConnectTestBroker.AuthorizeBody(host.Broker.Preamble(ticket)));

        // One friend's connections fill the cache, so every other friend is refused.
        AssertBareDeny(await requests.AuthorizeAsync(host.OtherFriendAuthorizeBody()));
        await (revoked switch
        {
            "device" => host.Server.RevokeDeviceAsync(host.Broker.DeviceId, host.Cancellation),
            "membership" => host.Server.RevokeMembershipAsync(ConnectTestBroker.MembershipId, host.Cancellation),
            "ticket" => host.Server.RevokeTicketAsync(ConnectTestBroker.TicketIdOf(ticket), host.Cancellation),
            _ => throw new ArgumentOutOfRangeException(nameof(revoked))
        });

        Assert.Equal("allow", (await requests.AuthorizeAsync(host.OtherFriendAuthorizeBody())).GetProperty("decision").GetString());
        AssertBareDeny(await requests.AuthorizeAsync(ConnectTestBroker.AuthorizeBody(host.Broker.Preamble(ticket))));
    }

    [Fact]
    public async Task SwitchingConnectOffForAServer_ClosesOnlyItsConnections_AndRefusesItsNextOnes()
    {
        await using var host = new Harness();
        host.Enabled.Enable(ConnectTestServerStore.SecondMinecraft);
        await using var transport = await host.SubscribeAsync();
        await using var requests = await host.ConnectAsync();
        await AllowAsync(requests, host.FreshAuthorizeBody());
        var onSecond = await AllowAsync(requests, host.AuthorizeBodyFor(ConnectTestServerStore.SecondMinecraft));

        var closing = await host.Server.DisableConnectAsync(ConnectTestServerStore.SecondMinecraft, host.Cancellation);

        Assert.Equal(new[] { onSecond }, closing);
        AssertCloseEvent(await transport.ReadAsync(), onSecond);
        Assert.False(host.Enabled.IsEnabled(ConnectTestServerStore.SecondMinecraft));
        AssertBareDeny(await requests.AuthorizeAsync(host.AuthorizeBodyFor(ConnectTestServerStore.SecondMinecraft)));
        Assert.Equal("allow", (await requests.AuthorizeAsync(host.FreshAuthorizeBody())).GetProperty("decision").GetString());
        Assert.Null(await transport.TryReadAsync(Quiet));
    }

    [Fact]
    public async Task ClosingAServersConnections_LeavesOtherServersAndNewConnectionsAlone()
    {
        await using var host = new Harness();
        host.Enabled.Enable(ConnectTestServerStore.SecondMinecraft);
        await using var transport = await host.SubscribeAsync();
        await using var requests = await host.ConnectAsync();
        await AllowAsync(requests, host.FreshAuthorizeBody());
        var onSecond = await AllowAsync(requests, host.AuthorizeBodyFor(ConnectTestServerStore.SecondMinecraft));

        var closing = await host.Server.CloseServerConnectionsAsync(ConnectTestServerStore.SecondMinecraft, host.Cancellation);

        Assert.Equal(new[] { onSecond }, closing);
        AssertCloseEvent(await transport.ReadAsync(), onSecond);
        Assert.Equal("allow", (await requests.AuthorizeAsync(host.AuthorizeBodyFor(ConnectTestServerStore.SecondMinecraft))).GetProperty("decision").GetString());
        Assert.Null(await transport.TryReadAsync(Quiet));
    }

    [Fact]
    public async Task AServerMovedToAnotherPort_LosesItsConnections_AtTheNextAuthorize()
    {
        await using var host = new Harness();
        host.Enabled.Enable(ConnectTestServerStore.SecondMinecraft);
        await using var transport = await host.SubscribeAsync();
        await using var requests = await host.ConnectAsync();
        var onOldPort = await AllowAsync(requests, host.FreshAuthorizeBody());
        await AllowAsync(requests, host.AuthorizeBodyFor(ConnectTestServerStore.SecondMinecraft));

        host.Store.Put(ConnectTestServerStore.Minecraft, ServerManager.Contracts.GameType.Minecraft, 25570);
        var onNewPort = await requests.AuthorizeAsync(host.FreshAuthorizeBody());

        AssertCloseEvent(await transport.ReadAsync(), onOldPort);
        Assert.Equal("127.0.0.1:25570", onNewPort.GetProperty("endpoint").GetString());

        // Later refreshes neither repeat that close nor touch the unchanged server.
        await AllowAsync(requests, host.FreshAuthorizeBody());
        Assert.Null(await transport.TryReadAsync(Quiet));
    }

    [Theory]
    [InlineData("deleted")]
    [InlineData("switched-off")]
    [InlineData("moved")]
    [InlineData("now-refused")]
    public async Task WithoutAnyNewConnection_ThePeriodicCheckEndsConnectionsToAServerThatChanged(string change)
    {
        await using var host = new Harness(serverCheckInterval: TimeSpan.FromMilliseconds(50));
        await using var transport = await host.SubscribeAsync();
        await using var requests = await host.ConnectAsync();
        var connection = await AllowAsync(requests, host.FreshAuthorizeBody());

        // Checks of an unchanged server leave its connections alone.
        Assert.Null(await transport.TryReadAsync(Quiet));
        switch (change)
        {
            case "deleted":
                host.Store.Remove(ConnectTestServerStore.Minecraft);
                break;
            case "switched-off":
                host.Enabled.Disable(ConnectTestServerStore.Minecraft);
                break;
            case "moved":
                host.Store.Put(ConnectTestServerStore.Minecraft, ServerManager.Contracts.GameType.Minecraft, 25570);
                break;
            case "now-refused":
                // Another server was registered on its port.
                host.Store.Put(Guid.NewGuid(), ServerManager.Contracts.GameType.Palworld, ConnectTestServerStore.MinecraftPort);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(change));
        }

        AssertCloseEvent(await transport.ReadAsync(), connection);
    }

    [Fact]
    public async Task APeriodicCheckThatCannotReadTheServers_KeepsTheConnections()
    {
        await using var host = new Harness(serverCheckInterval: TimeSpan.FromMilliseconds(50));
        await using var transport = await host.SubscribeAsync();
        await using var requests = await host.ConnectAsync();
        await AllowAsync(requests, host.FreshAuthorizeBody());

        host.Store.Fail = true;

        Assert.Null(await transport.TryReadAsync(Quiet));
    }

    [Fact]
    public async Task EveryAuthorize_ReadsTheCurrentRegistrationAndConnectSwitch()
    {
        await using var host = new Harness();
        await using var transport = await host.SubscribeAsync();
        await using var requests = await host.ConnectAsync();

        host.Store.Put(ConnectTestServerStore.Minecraft, ServerManager.Contracts.GameType.Minecraft, 25570);
        var moved = await requests.AuthorizeAsync(host.FreshAuthorizeBody());
        host.Store.Fail = true;
        var storeDown = await requests.AuthorizeAsync(host.FreshAuthorizeBody());
        host.Store.Fail = false;
        host.Enabled.Disable(ConnectTestServerStore.Minecraft);
        var switchedOff = await requests.AuthorizeAsync(host.FreshAuthorizeBody());

        Assert.Equal("127.0.0.1:25570", moved.GetProperty("endpoint").GetString());
        AssertBareDeny(storeDown);
        AssertBareDeny(switchedOff);
    }

    [Fact]
    public async Task ASecondServer_CannotTakeANameThatIsAlreadyHeld()
    {
        await using var host = new Harness();
        await using var squatter = new ConnectHostAuthorizationPipeServer(
            new ConnectHostAuthorizationOptions(host.Broker.OwnerId, host.Broker.KeySet, host.PipeName),
            new ConnectServerCatalog(host.Store, host.Enabled),
            host.Broker.Clock,
            NullLogger<ConnectHostAuthorizationPipeServer>.Instance);

        Assert.Throws<ConnectPipeNameInUseException>(squatter.Start);
    }

    private static JsonObject Authorize(ConnectTestBroker broker, string ticket) =>
        ConnectTestBroker.AuthorizeBody(broker.Preamble(ticket));

    private static string ForgeSignature(string ticket)
    {
        using var impostor = Es256.CreateKey();
        var signingInput = ticket[..ticket.LastIndexOf('.')];
        return signingInput + "." + Base64Url.Encode(Es256.Sign(impostor, Encoding.ASCII.GetBytes(signingInput)));
    }

    private static JsonObject WithForgedProof(ConnectTestBroker broker, string ticket)
    {
        using var impostor = Es256.CreateKey();
        var preamble = ConnectionPreamble.Create(ticket, impostor, broker.Clock);
        return ConnectTestBroker.AuthorizeBody(new JsonObject
        {
            ["t"] = preamble.Ticket,
            ["n"] = preamble.Nonce,
            ["ts"] = preamble.Timestamp,
            ["p"] = preamble.Proof
        });
    }

    /// <summary>A server list that takes <paramref name="delay"/> to read, on the Agent's clock.</summary>
    private static Func<Task> SlowStore(Harness host, TimeSpan delay) => () =>
    {
        host.Broker.Clock.Advance(delay);
        return Task.CompletedTask;
    };

    private static async Task<string> AllowAsync(ConnectPipeTestClient requests, JsonObject body)
    {
        var response = await requests.AuthorizeAsync(body);
        Assert.Equal("allow", response.GetProperty("decision").GetString());
        return response.GetProperty("connId").GetString()!;
    }

    /// <summary>Exactly <c>{"id":n,"ok":true,"decision":"deny"}</c>: nothing says why.</summary>
    private static void AssertBareDeny(JsonElement response)
    {
        Assert.Equal(new[] { "id", "ok", "decision" }, response.EnumerateObject().Select(member => member.Name));
        Assert.True(response.GetProperty("ok").GetBoolean());
        Assert.Equal("deny", response.GetProperty("decision").GetString());
    }

    private static void AssertCloseEvent(JsonElement line, params string[] connectionIds)
    {
        Assert.False(line.TryGetProperty("id", out _), "An event carries no request id.");
        Assert.Equal("close", line.GetProperty("event").GetString());
        Assert.Equal(connectionIds, line.GetProperty("connIds").EnumerateArray().Select(id => id.GetString()!));
    }

    /// <summary>
    /// One running pipe server over the real catalog, with the Minecraft and Palworld servers
    /// switched on (Palworld to show the game is what refuses it) and the second Minecraft
    /// server left off.
    /// </summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _timeout = new(Timeout);

        public Harness(
            TimeSpan? closeRepeatInterval = null,
            TimeSpan? serverCheckInterval = null,
            int liveConnectionCapacity = ConnectLiveConnections.DefaultCapacity,
            int replayCacheCapacity = ReplayCache.DefaultCapacity)
        {
            Enabled.Enable(ConnectTestServerStore.Minecraft);
            Enabled.Enable(ConnectTestServerStore.Palworld);
            var options = new ConnectHostAuthorizationOptions(Broker.OwnerId, Broker.KeySet, PipeName)
            {
                // Long by default so a repeat or a server check never interleaves with what a
                // test reads.
                CloseRepeatInterval = closeRepeatInterval ?? TimeSpan.FromHours(1),
                ServerCheckInterval = serverCheckInterval ?? TimeSpan.FromHours(1)
            };
            Server = new ConnectHostAuthorizationPipeServer(
                options,
                new ConnectServerCatalog(Store, Enabled),
                Broker.Clock,
                NullLogger<ConnectHostAuthorizationPipeServer>.Instance,
                liveConnectionCapacity,
                replayCacheCapacity);
            Server.Start();
        }

        public ConnectTestBroker Broker { get; } = new();

        public ConnectTestServerStore Store { get; } = new();

        public ConnectEnabledServers Enabled { get; } = new();

        public string PipeName { get; } = $"1Salem.Connect.Test.{Guid.NewGuid():N}";

        public ConnectHostAuthorizationPipeServer Server { get; }

        public CancellationToken Cancellation => _timeout.Token;

        public Task<ConnectPipeTestClient> ConnectAsync(bool hello = true) =>
            ConnectPipeTestClient.ConnectAsync(PipeName, Cancellation, hello);

        /// <summary>A client that has subscribed, as the host transport does before it bridges anything.</summary>
        public async Task<ConnectPipeTestClient> SubscribeAsync()
        {
            var client = await ConnectAsync();
            var acknowledgement = await client.CallAsync("subscribe");
            Assert.Equal("{\"id\":2,\"ok\":true}", acknowledgement.GetRawText());
            return client;
        }

        /// <summary>A new ticket and preamble for the test friend on the Minecraft server.</summary>
        public JsonObject FreshAuthorizeBody() => AuthorizeBodyFor(ConnectTestServerStore.Minecraft);

        /// <summary>A new ticket and preamble for the test friend on <paramref name="serverId"/>.</summary>
        public JsonObject AuthorizeBodyFor(Guid serverId) =>
            ConnectTestBroker.AuthorizeBody(Broker.Preamble(Broker.Issue(serverId)));

        /// <summary>The same, for a different friend (device and membership) on the same node.</summary>
        public JsonObject OtherFriendAuthorizeBody() =>
            ConnectTestBroker.AuthorizeBody(Broker.Preamble(Broker.Issue(ConnectTestServerStore.Minecraft, claims =>
            {
                claims["sub"] = OtherDeviceId;
                claims["mid"] = "mem_other0001";
            })));

        private string OtherDeviceId { get; } = ConnectTestBroker.NewDeviceId();

        public async ValueTask DisposeAsync()
        {
            await Server.DisposeAsync();
            Broker.Dispose();
            _timeout.Dispose();
        }
    }
}
