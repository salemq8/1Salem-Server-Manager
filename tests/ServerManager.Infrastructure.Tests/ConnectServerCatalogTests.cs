using System.Net;
using ServerManager.Connect.Core.Tickets;
using ServerManager.Contracts;
using ServerManager.Infrastructure.Connect;

namespace ServerManager.Infrastructure.Tests;

/// <summary>
/// The Agent's authoritative ServerId → endpoint map for 1Salem Connect (contract §3, §9):
/// Minecraft only, the owner's switch, the registered port, loopback always, and never a port
/// that could be some other service.
/// </summary>
public sealed class ConnectServerCatalogTests
{
    [Fact]
    public async Task OnlyMinecraftServers_AppearAsTcpOnTheirOwnPort()
    {
        var (catalog, enabled, _) = CreateCatalog();
        enabled.Enable(ConnectTestServerStore.Minecraft);
        enabled.Enable(ConnectTestServerStore.Palworld);

        await catalog.RefreshAsync(CancellationToken.None);
        var minecraft = catalog.Find(ConnectTestServerStore.Minecraft);

        Assert.NotNull(minecraft);
        Assert.Equal(ConnectGameKind.Minecraft, minecraft.Game);
        Assert.Equal(ConnectProtocol.Tcp, minecraft.Protocol);
        Assert.Equal(ConnectTestServerStore.MinecraftPort, minecraft.LocalPort);
        Assert.True(minecraft.ConnectEnabled);

        // Palworld is UDP and not bridged in Phase 1, so its id is as unknown as a made-up one,
        // even with Connect switched on for it.
        Assert.Null(catalog.Find(ConnectTestServerStore.Palworld));
        Assert.Null(catalog.Find(Guid.NewGuid()));
    }

    [Fact]
    public async Task NothingIsKnown_BeforeTheFirstRefresh()
    {
        var (catalog, enabled, _) = CreateCatalog();
        enabled.Enable(ConnectTestServerStore.Minecraft);

        Assert.Null(catalog.Find(ConnectTestServerStore.Minecraft));
        await catalog.RefreshAsync(CancellationToken.None);
        Assert.NotNull(catalog.Find(ConnectTestServerStore.Minecraft));
    }

    [Fact]
    public async Task EveryServer_StartsWithConnectOff_AndTheSwitchTakesEffectWithoutARefresh()
    {
        var (catalog, enabled, _) = CreateCatalog();
        await catalog.RefreshAsync(CancellationToken.None);

        Assert.False(catalog.Find(ConnectTestServerStore.Minecraft)!.ConnectEnabled);
        enabled.Enable(ConnectTestServerStore.Minecraft);
        Assert.True(catalog.Find(ConnectTestServerStore.Minecraft)!.ConnectEnabled);
        enabled.Disable(ConnectTestServerStore.Minecraft);
        Assert.False(catalog.Find(ConnectTestServerStore.Minecraft)!.ConnectEnabled);
        Assert.False(catalog.Find(ConnectTestServerStore.SecondMinecraft)!.ConnectEnabled);
    }

    [Fact]
    public async Task Refresh_PicksUpPortChangesAndRemovedServers()
    {
        var (catalog, _, store) = CreateCatalog();
        await catalog.RefreshAsync(CancellationToken.None);

        store.Put(ConnectTestServerStore.Minecraft, GameType.Minecraft, 25570);
        store.Remove(ConnectTestServerStore.SecondMinecraft);
        await catalog.RefreshAsync(CancellationToken.None);

        Assert.Equal(25570, catalog.Find(ConnectTestServerStore.Minecraft)!.LocalPort);
        Assert.Null(catalog.Find(ConnectTestServerStore.SecondMinecraft));
    }

    [Fact]
    public async Task AFailedRefresh_Propagates_AndKeepsThePreviousSnapshot()
    {
        var (catalog, _, store) = CreateCatalog();
        await catalog.RefreshAsync(CancellationToken.None);
        store.Put(ConnectTestServerStore.Minecraft, GameType.Minecraft, 25570);
        store.Fail = true;

        await Assert.ThrowsAsync<IOException>(() => catalog.RefreshAsync(CancellationToken.None));
        Assert.Equal(ConnectTestServerStore.MinecraftPort, catalog.Find(ConnectTestServerStore.Minecraft)!.LocalPort);
    }

    [Fact]
    public async Task ARegistrationWithAnImpossiblePort_IsLeftOut()
    {
        var (catalog, enabled, store) = CreateCatalog();
        store.Put(ConnectTestServerStore.Minecraft, GameType.Minecraft, 0);
        enabled.Enable(ConnectTestServerStore.Minecraft);

        await catalog.RefreshAsync(CancellationToken.None);

        Assert.Null(catalog.Find(ConnectTestServerStore.Minecraft));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(22)]
    [InlineData(135)]
    [InlineData(445)]
    [InlineData(1023)]
    public async Task ASystemServicePort_IsNeverBridged(int port)
    {
        var (catalog, enabled, store) = CreateCatalog();
        store.Put(ConnectTestServerStore.Minecraft, GameType.Minecraft, port);
        enabled.Enable(ConnectTestServerStore.Minecraft);

        await catalog.RefreshAsync(CancellationToken.None);

        Assert.Null(catalog.Find(ConnectTestServerStore.Minecraft));
    }

    [Theory]
    [InlineData(3389)]
    [InlineData(5357)]
    [InlineData(5985)]
    [InlineData(5986)]
    [InlineData(8212)]
    [InlineData(25575)]
    public async Task ASensitiveLoopbackServicePort_IsNeverBridged(int port)
    {
        var (catalog, enabled, store) = CreateCatalog();
        store.Put(ConnectTestServerStore.Minecraft, GameType.Minecraft, port);
        enabled.Enable(ConnectTestServerStore.Minecraft);

        await catalog.RefreshAsync(CancellationToken.None);

        Assert.Null(catalog.Find(ConnectTestServerStore.Minecraft));
    }

    [Fact]
    public async Task TheAgentsOwnApiPorts_AreNeverBridged()
    {
        var (catalog, _, store) = CreateCatalog();
        var loopbackApiPort = new Uri(AgentTransportDefaults.LoopbackApiUrl).Port;
        store.Put(ConnectTestServerStore.Minecraft, GameType.Minecraft, loopbackApiPort);
        store.Put(ConnectTestServerStore.SecondMinecraft, GameType.Minecraft, ConnectServerCatalog.DefaultAgentLanPort);

        await catalog.RefreshAsync(CancellationToken.None);

        Assert.Equal(new[] { loopbackApiPort, 5252 }, ConnectServerCatalog.DefaultAgentPorts);
        Assert.Null(catalog.Find(ConnectTestServerStore.Minecraft));
        Assert.Null(catalog.Find(ConnectTestServerStore.SecondMinecraft));
    }

    [Fact]
    public async Task AnAgentOnOtherPorts_HasThoseRefusedInstead()
    {
        var store = new ConnectTestServerStore();
        var catalog = new ConnectServerCatalog(store, new ConnectEnabledServers(), [27015, 27443]);
        store.Put(ConnectTestServerStore.Minecraft, GameType.Minecraft, 27015);
        store.Put(ConnectTestServerStore.SecondMinecraft, GameType.Minecraft, 27443);
        var onTheDefaultApiPort = Guid.NewGuid();
        store.Put(onTheDefaultApiPort, GameType.Minecraft, 5251);

        await catalog.RefreshAsync(CancellationToken.None);

        Assert.Null(catalog.Find(ConnectTestServerStore.Minecraft));
        Assert.Null(catalog.Find(ConnectTestServerStore.SecondMinecraft));
        Assert.Equal(5251, catalog.Find(onTheDefaultApiPort)!.LocalPort);
    }

    [Fact]
    public async Task ThePortsJustAboveTheSystemRange_AreBridged()
    {
        var (catalog, _, store) = CreateCatalog();
        store.Put(ConnectTestServerStore.Minecraft, GameType.Minecraft, ConnectServerCatalog.LowestBridgeablePort);
        store.Put(ConnectTestServerStore.SecondMinecraft, GameType.Minecraft, 65535);

        await catalog.RefreshAsync(CancellationToken.None);

        Assert.Equal(1024, catalog.Find(ConnectTestServerStore.Minecraft)!.LocalPort);
        Assert.Equal(65535, catalog.Find(ConnectTestServerStore.SecondMinecraft)!.LocalPort);
    }

    [Fact]
    public async Task APortAnotherRegisteredServerUses_IsNeverBridged_WhateverThatServersGame()
    {
        var (catalog, _, store) = CreateCatalog();

        // A Palworld server's port, and two Minecraft servers sharing one: the friend could reach
        // whichever of them happens to be listening, not only the one they were approved for.
        store.Put(ConnectTestServerStore.Minecraft, GameType.Minecraft, ConnectTestServerStore.PalworldPort);
        store.Put(ConnectTestServerStore.SecondMinecraft, GameType.Minecraft, 30000);
        var third = Guid.NewGuid();
        store.Put(third, GameType.Minecraft, 30000);
        await catalog.RefreshAsync(CancellationToken.None);

        Assert.Null(catalog.Find(ConnectTestServerStore.Minecraft));
        Assert.Null(catalog.Find(ConnectTestServerStore.SecondMinecraft));
        Assert.Null(catalog.Find(third));

        store.Put(third, GameType.Minecraft, 30001);
        await catalog.RefreshAsync(CancellationToken.None);

        Assert.Equal(30000, catalog.Find(ConnectTestServerStore.SecondMinecraft)!.LocalPort);
        Assert.Equal(30001, catalog.Find(third)!.LocalPort);
    }

    [Fact]
    public async Task ASlowerRefreshThatStartedEarlier_NeverReplacesANewerOne()
    {
        var (catalog, _, store) = CreateCatalog();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.Listing = () => release.Task;
        var slow = catalog.RefreshAsync(CancellationToken.None);

        // The server is deleted, and a later refresh sees it gone before the slow one returns.
        store.Listing = null;
        store.Remove(ConnectTestServerStore.Minecraft);
        await catalog.RefreshAsync(CancellationToken.None);
        release.SetResult();
        await slow;

        Assert.Null(catalog.Find(ConnectTestServerStore.Minecraft));
    }

    [Theory]
    [InlineData("unknown", AccessDenialReason.UnknownServer)]
    [InlineData("palworld", AccessDenialReason.UnknownServer)]
    [InlineData("disabled", AccessDenialReason.ServerNotEnabled)]
    public async Task TicketsForServersOutsideTheCatalog_AreRefused(string server, AccessDenialReason expected)
    {
        using var broker = new ConnectTestBroker();
        var (catalog, enabled, _) = CreateCatalog();
        enabled.Enable(ConnectTestServerStore.Minecraft);
        enabled.Enable(ConnectTestServerStore.Palworld);
        await catalog.RefreshAsync(CancellationToken.None);
        var serverId = server switch
        {
            "unknown" => Guid.NewGuid(),
            "palworld" => ConnectTestServerStore.Palworld,
            "disabled" => ConnectTestServerStore.SecondMinecraft,
            _ => throw new ArgumentOutOfRangeException(nameof(server))
        };

        var result = CreateAuthorizer(broker, catalog).Authorize(Request(broker, serverId));

        Assert.False(result.IsAllowed);
        Assert.Equal(expected, result.Reason);
        Assert.Null(result.Endpoint);
    }

    [Fact]
    public async Task ATicketForAServerOnARefusedPort_IsRefusedAsForAnUnknownServer()
    {
        using var broker = new ConnectTestBroker();
        var (catalog, enabled, store) = CreateCatalog();
        store.Put(ConnectTestServerStore.Minecraft, GameType.Minecraft, 3389);
        enabled.Enable(ConnectTestServerStore.Minecraft);
        await catalog.RefreshAsync(CancellationToken.None);

        var result = CreateAuthorizer(broker, catalog).Authorize(Request(broker, ConnectTestServerStore.Minecraft));

        Assert.Equal(AccessDenialReason.UnknownServer, result.Reason);
        Assert.Null(result.Endpoint);
    }

    [Fact]
    public async Task AnAllowedConnection_AlwaysGoesToLoopbackOnTheRegisteredPort()
    {
        using var broker = new ConnectTestBroker();
        var (catalog, enabled, _) = CreateCatalog();
        enabled.Enable(ConnectTestServerStore.Minecraft);
        await catalog.RefreshAsync(CancellationToken.None);

        var result = CreateAuthorizer(broker, catalog).Authorize(Request(broker, ConnectTestServerStore.Minecraft));

        Assert.True(result.IsAllowed);
        Assert.Equal(new IPEndPoint(IPAddress.Loopback, ConnectTestServerStore.MinecraftPort), result.Endpoint);
    }

    private static (ConnectServerCatalog Catalog, ConnectEnabledServers Enabled, ConnectTestServerStore Store) CreateCatalog()
    {
        var store = new ConnectTestServerStore();
        var enabled = new ConnectEnabledServers();
        return (new ConnectServerCatalog(store, enabled), enabled, store);
    }

    private static HostAuthorizer CreateAuthorizer(ConnectTestBroker broker, ConnectServerCatalog catalog) =>
        new(
            new TicketVerifier(broker.KeySet, broker.OwnerId, catalog, new RevocationSet(broker.Clock), broker.Clock),
            new ReplayCache(broker.Clock));

    private static HostAuthorizationRequest Request(ConnectTestBroker broker, Guid serverId) =>
        new(
            ConnectionPreamble.Create(broker.Issue(serverId), broker.SessionKey, broker.Clock),
            ConnectTestBroker.NodeId);
}
