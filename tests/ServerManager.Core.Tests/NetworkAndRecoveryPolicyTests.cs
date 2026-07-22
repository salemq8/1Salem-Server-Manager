using ServerManager.Contracts;
using ServerManager.Core;

namespace ServerManager.Core.Tests;

public sealed class NetworkAndRecoveryPolicyTests
{
    [Fact]
    public void AdapterSelection_UsesExplicitPreferredAdapter()
    {
        var adapters = CreateAdapters();

        var selected = NetworkAdapterSelection.Select(adapters, "wifi");

        Assert.NotNull(selected);
        Assert.Equal("wifi", selected.Id);
        Assert.Equal("192.168.2.20", selected.Ipv4);
    }

    [Fact]
    public void AdapterSelection_FallsBackToDefaultGateway()
    {
        var selected = NetworkAdapterSelection.Select(CreateAdapters(), "missing");

        Assert.NotNull(selected);
        Assert.Equal("ethernet", selected.Id);
    }

    [Fact]
    public void AdapterSelection_ReflectsIpChangeWithoutCachedAddress()
    {
        var first = NetworkAdapterSelection.Select(CreateAdapters(), "wifi");
        var changed = CreateAdapters()
            .Select(adapter => adapter.Id == "wifi"
                ? adapter with { Ipv4 = "192.168.2.99" }
                : adapter)
            .ToArray();
        var second = NetworkAdapterSelection.Select(changed, "wifi");

        Assert.Equal("192.168.2.20", first?.Ipv4);
        Assert.Equal("192.168.2.99", second?.Ipv4);
    }

    [Fact]
    public void RecoveryPolicy_DoesNotStartUnmarkedServer() =>
        Assert.False(ServerRecoveryPolicy.ShouldAutoStart(false, true));

    [Fact]
    public void RecoveryPolicy_DoesNotStartMissingInstallation() =>
        Assert.False(ServerRecoveryPolicy.ShouldAutoStart(true, false));

    [Theory]
    [InlineData(ServerState.Running, ServerState.Stopped)]
    [InlineData(ServerState.Starting, ServerState.Stopped)]
    [InlineData(ServerState.Error, ServerState.Stopped)]
    [InlineData(ServerState.NotInstalled, ServerState.NotInstalled)]
    public void RecoveryPolicy_ReconcilesStaleState(
        ServerState persisted,
        ServerState expected) =>
        Assert.Equal(
            expected,
            ServerRecoveryPolicy.ReconcileAfterAgentRestart(persisted, false));

    private static NetworkAdapterSnapshot[] CreateAdapters() =>
    [
        new("ethernet", "Ethernet", "Physical Ethernet", "192.168.1.20", true, false),
        new("wifi", "Wi-Fi", "Physical Wi-Fi", "192.168.2.20", false, true)
    ];
}
