using ServerManager.Contracts;

namespace ServerManager.Core.Tests;

public sealed class InstalledVersionStatePolicyTests
{
    [Fact]
    public void ComponentMismatch_IsReportedAsIncomplete()
    {
        var state = InstalledVersionStatePolicy.Determine(
            "1.3.2",
            "1.3.1",
            "1.3.2");

        Assert.Equal("Update incomplete", state);
    }

    [Fact]
    public void StagedAgent_IsReportedAsPendingRestart()
    {
        var state = InstalledVersionStatePolicy.Determine(
            "1.3.2",
            "1.3.1",
            "1.3.2",
            "1.3.2");

        Assert.Equal("Update pending Agent restart", state);
    }

    [Fact]
    public void MatchingComponents_AreConsistent()
    {
        var state = InstalledVersionStatePolicy.Determine(
            "1.3.2",
            "1.3.2",
            "1.3.2");

        Assert.Equal("Consistent", state);
    }
}
