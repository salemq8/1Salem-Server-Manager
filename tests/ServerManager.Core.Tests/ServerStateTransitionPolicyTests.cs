using ServerManager.Contracts;
using ServerManager.Core;

namespace ServerManager.Core.Tests;

public sealed class ServerStateTransitionPolicyTests
{
    [Theory]
    [InlineData(ServerState.Stopped, ServerState.Starting)]
    [InlineData(ServerState.Starting, ServerState.Running)]
    [InlineData(ServerState.Running, ServerState.Stopping)]
    [InlineData(ServerState.Crashed, ServerState.Starting)]
    public void CanTransition_AllowsExpectedLifecycleChanges(
        ServerState from,
        ServerState to)
    {
        Assert.True(ServerStateTransitionPolicy.CanTransition(from, to));
    }

    [Fact]
    public void CanTransition_RejectsRunningToUpdating()
    {
        Assert.False(
            ServerStateTransitionPolicy.CanTransition(
                ServerState.Running,
                ServerState.Updating));
    }

    [Fact]
    public void EnsureAllowed_ThrowsForUnsafeTransition()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => ServerStateTransitionPolicy.EnsureAllowed(
                ServerState.NotInstalled,
                ServerState.Running));

        Assert.Contains("NotInstalled", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Running", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OperationResult_FailureRetainsDiagnosticFields()
    {
        var result = OperationResult.Fail("PortInUse", "Port 25565 is already occupied.");

        Assert.False(result.Success);
        Assert.Equal("PortInUse", result.ErrorCode);
        Assert.Equal("Port 25565 is already occupied.", result.Message);
    }
}

