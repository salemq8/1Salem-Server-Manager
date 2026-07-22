using ServerManager.Core;

namespace ServerManager.Core.Tests;

public sealed class ProductBuildPolicyTests
{
    [Fact]
    public void TwoPartVisibleVersion_RemainsExactlyOnePointFive()
    {
        var version = SemanticVersion.Parse("1.5");

        Assert.Equal("1.5", version.ToString());
        Assert.Equal(0, version.Patch);
    }

    [Fact]
    public void EqualProductVersion_HigherBuildIsAvailable()
    {
        var decision = ProductBuildPolicy.Evaluate(
            new ProductBuildIdentity("1.5", 4),
            new ProductBuildIdentity("1.5", 5));

        Assert.Equal(ProductBuildDisposition.UpdateAvailable, decision.Disposition);
        Assert.True(decision.CanInstall);
    }

    [Fact]
    public void EqualProductVersion_LowerBuildIsRejected()
    {
        var decision = ProductBuildPolicy.Evaluate(
            new ProductBuildIdentity("1.5", 5),
            new ProductBuildIdentity("1.5", 4));

        Assert.Equal(ProductBuildDisposition.DowngradeRejected, decision.Disposition);
        Assert.False(decision.CanInstall);
    }

    [Fact]
    public void EqualProductVersionAndBuild_IsAlreadyCurrent()
    {
        var hash = new string('A', 64);
        var decision = ProductBuildPolicy.Evaluate(
            new ProductBuildIdentity("1.5", 5, hash),
            new ProductBuildIdentity("1.5", 5, hash));

        Assert.Equal(ProductBuildDisposition.AlreadyCurrent, decision.Disposition);
    }

    [Fact]
    public void IdenticalBuild_RequiresExplicitRepairMode()
    {
        var decision = ProductBuildPolicy.Evaluate(
            new ProductBuildIdentity("1.5", 5, new string('A', 64)),
            new ProductBuildIdentity("1.5", 5, new string('B', 64)),
            repairMode: true);

        Assert.Equal(ProductBuildDisposition.RepairAllowed, decision.Disposition);
        Assert.True(decision.CanInstall);
    }

    [Fact]
    public void SameBuildWithDifferentHash_IsNotInventedAsPatchVersion()
    {
        var decision = ProductBuildPolicy.Evaluate(
            new ProductBuildIdentity("1.5", 5, new string('A', 64)),
            new ProductBuildIdentity("1.5", 5, new string('B', 64)));

        Assert.Equal(ProductBuildDisposition.SameBuildHashMismatch, decision.Disposition);
        Assert.DoesNotContain("1.5.1", decision.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OneTimeProductTransition_AcceptsVersionOnePointFiveBuildOne()
    {
        var decision = ProductBuildPolicy.Evaluate(
            new ProductBuildIdentity("1.3.2", 0),
            new ProductBuildIdentity("1.5", 1));

        Assert.Equal(ProductBuildDisposition.UpdateAvailable, decision.Disposition);
    }

    [Fact]
    public void ReleaseGuard_UsesBuildRevisionInsteadOfNextPatch()
    {
        var decision = ReleaseVersionPolicy.EvaluateBuild(
            "1.5",
            9,
            [new ProductBuildIdentity("1.5", 8)]);

        Assert.True(decision.CanInstall);
        Assert.Contains("Build 9", decision.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("1.5.1", decision.Message, StringComparison.Ordinal);
    }
}
