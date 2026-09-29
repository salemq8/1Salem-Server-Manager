using Phase2Acceptance;

namespace ServerManager.Agent.IntegrationTests;

public sealed class ConnectAcceptanceCleanupTests
{
    [Fact]
    public void NeverVisible404_PreservesRecoveryStateAndCannotConfirmRemoval()
    {
        var decision = CleanupNodeGate.Evaluate(404, matchesRun: false,
            driverSawNode: false, agentSawNode: false);

        Assert.True(decision.PreserveRecoveryState);
        Assert.False(decision.SeenByApi);
        Assert.False(decision.DeleteDevice);
        Assert.False(decision.ConfirmsRemoval(404));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void PreviouslyVisible404_CanConfirmOnlyFinal404(bool driverSaw, bool agentSaw)
    {
        var decision = CleanupNodeGate.Evaluate(404, matchesRun: false, driverSaw, agentSaw);

        Assert.False(decision.PreserveRecoveryState);
        Assert.True(decision.SeenByApi);
        Assert.False(decision.DeleteDevice);
        Assert.True(decision.ConfirmsRemoval(404));
        Assert.False(decision.ConfirmsRemoval(200));
        Assert.False(decision.ConfirmsRemoval(403));
    }

    [Fact]
    public void Matching200_RecordsVisibilityAndAllowsOwnedDeletion()
    {
        var decision = CleanupNodeGate.Evaluate(200, matchesRun: true,
            driverSawNode: false, agentSawNode: false);

        Assert.False(decision.PreserveRecoveryState);
        Assert.True(decision.SeenByApi);
        Assert.True(decision.DeleteDevice);
        Assert.True(decision.ConfirmsRemoval(404));
        Assert.False(decision.ConfirmsRemoval(200));
    }

    [Theory]
    [InlineData(200, false)]
    [InlineData(403, true)]
    [InlineData(500, true)]
    public void MismatchedOrFailedRead_NeverAllowsDeletionOrRemoval(int status, bool matchesRun)
    {
        var decision = CleanupNodeGate.Evaluate(status, matchesRun,
            driverSawNode: true, agentSawNode: true);

        Assert.True(decision.PreserveRecoveryState);
        Assert.False(decision.DeleteDevice);
        Assert.False(decision.ConfirmsRemoval(404));
    }

    [Fact]
    public void BaselineDevice_IsRefusedBeforeAnyApiReadOrDelete()
    {
        Assert.Throws<InvalidOperationException>(() => CleanupNodeGate.RequireNotBaseline(true));
        CleanupNodeGate.RequireNotBaseline(false);
    }
}
