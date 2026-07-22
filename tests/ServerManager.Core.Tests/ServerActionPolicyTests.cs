using ServerManager.Contracts;
using ServerManager.Core;

namespace ServerManager.Core.Tests;

public sealed class ServerActionPolicyTests
{
    [Fact]
    public void StoppedServer_EnablesStartAndDisablesConflictingActions()
    {
        var actions = ServerActionPolicy.For(ServerState.Stopped, true);

        Assert.True(actions.CanStart);
        Assert.True(actions.CanBackup);
        Assert.True(actions.CanRestore);
        Assert.False(actions.CanStop);
        Assert.False(actions.CanRestart);
        Assert.False(actions.CanSendCommand);
    }

    [Fact]
    public void RunningServer_EnablesLiveActions()
    {
        var actions = ServerActionPolicy.For(ServerState.Running, true);

        Assert.False(actions.CanStart);
        Assert.True(actions.CanStop);
        Assert.True(actions.CanRestart);
        Assert.True(actions.CanForceStop);
        Assert.True(actions.CanSendCommand);
        Assert.False(actions.CanRestore);
    }

    [Theory]
    [InlineData(ServerState.Starting)]
    [InlineData(ServerState.Stopping)]
    [InlineData(ServerState.Restarting)]
    [InlineData(ServerState.Updating)]
    [InlineData(ServerState.BackingUp)]
    public void TransitionalServer_DisablesEveryConflictingAction(ServerState state)
    {
        var actions = ServerActionPolicy.For(state, true);

        Assert.False(actions.CanStart);
        Assert.False(actions.CanStop);
        Assert.False(actions.CanRestart);
        Assert.False(actions.CanBackup);
        Assert.False(actions.CanUpdate);
        Assert.False(actions.CanSendCommand);
    }

    [Fact]
    public void DashboardCard_PreservesStateAndActionProjection()
    {
        var actions = ServerActionPolicy.For(ServerState.Running);
        var card = new ServerDashboardCard(
            Guid.NewGuid(),
            GameType.Minecraft,
            true,
            "Test",
            ServerState.Running,
            "192.168.1.2:25565",
            "1.21.8",
            "Java 21",
            1,
            20,
            123,
            3,
            1024,
            900,
            1200,
            TimeSpan.FromMinutes(2),
            null,
            "current",
            25565,
            2048,
            4096,
            2048,
            4096,
            "Done",
            null,
            actions);

        Assert.Equal(ServerState.Running, card.State);
        Assert.Equal("192.168.1.2:25565", card.LocalAddress);
        Assert.True(card.Actions.CanRestart);
        Assert.False(card.Actions.CanStart);
    }
}
