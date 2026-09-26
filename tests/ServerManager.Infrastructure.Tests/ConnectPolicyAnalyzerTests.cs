using System.Text;
using ServerManager.Infrastructure.Connect;

namespace ServerManager.Infrastructure.Tests;

public sealed class ConnectPolicyAnalyzerTests
{
    [Fact]
    public void ExactModernGrant_IsSafe()
    {
        var verdict = Analyze("""
            {
              "tagOwners": {
                "tag:onesalem-host": ["autogroup:admin"],
                "tag:onesalem-client": ["tag:onesalem-host"]
              },
              "grants": [{
                "src": ["tag:onesalem-client"],
                "dst": ["tag:onesalem-host"],
                "ip": ["tcp:7780"]
              }]
            }
            """);

        Assert.Equal(ConnectPolicyVerdictKind.Safe, verdict.Kind);
        Assert.Empty(verdict.Reasons);
    }

    [Fact]
    public void ExactLegacyAcl_IsSafe()
    {
        var verdict = Analyze("""
            {
              "tagOwners": {
                "tag:onesalem-host": ["autogroup:admin"],
                "tag:onesalem-client": ["tag:onesalem-host"]
              },
              "acls": [{"action":"accept","src":["tag:onesalem-client"],"dst":["tag:onesalem-host:7780"]}]
            }
            """);

        Assert.True(verdict.IsSafe);
    }

    [Fact]
    public void LegacyUsersAndPortsNames_AreUnderstood()
    {
        var verdict = Analyze("""
            {
              "tagOwners": {
                "tag:onesalem-host": ["autogroup:admin"],
                "tag:onesalem-client": ["tag:onesalem-host"]
              },
              "acls": [{"action":"accept","users":["tag:onesalem-client"],"ports":["tag:onesalem-host:7780"]}]
            }
            """);

        Assert.True(verdict.IsSafe);
    }

    [Theory]
    [InlineData("*")]
    [InlineData("autogroup:tagged")]
    [InlineData("100.64.0.0/10")]
    [InlineData("100.100.2.3")]
    [InlineData("fd7a:115c:a1e0::/48")]
    [InlineData("possible-host-alias")]
    public void PotentialFriendSources_WithBroadAccess_AreUnsafe(string source)
    {
        var verdict = Analyze($$"""
            {
              "tagOwners": {
                "tag:onesalem-host": ["autogroup:admin"],
                "tag:onesalem-client": ["tag:onesalem-host"]
              },
              "grants": [
                {"src":["tag:onesalem-client"],"dst":["tag:onesalem-host"],"ip":["tcp:7780"]},
                {"src":["{{source}}"],"dst":["*"],"ip":["*"]}
              ]
            }
            """);

        Assert.Equal(ConnectPolicyVerdictKind.Unsafe, verdict.Kind);
        Assert.Contains(verdict.Reasons, reason => reason.Contains("beyond", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingOrWrongTagOwnership_IsUnsafe()
    {
        var missing = Analyze("{\"tagOwners\":{}}");
        var wrong = Analyze("""
            {"tagOwners":{"tag:onesalem-host":["autogroup:admin"],"tag:onesalem-client":["autogroup:admin"]}}
            """);

        Assert.Equal(ConnectPolicyVerdictKind.Unsafe, missing.Kind);
        Assert.Equal(ConnectPolicyVerdictKind.Unsafe, wrong.Kind);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("[]")]
    [InlineData("{\"tagOwners\":{\"tag:onesalem-host\":[\"autogroup:admin\"],\"tag:onesalem-client\":[\"tag:onesalem-host\"]},\"grants\":{}}")]
    public void UnreadableShapes_AreUnverifiable(string policy)
    {
        Assert.Equal(ConnectPolicyVerdictKind.Unverifiable, Analyze(policy).Kind);
    }

    [Fact]
    public void MissingRequiredRoute_IsNotSafe()
    {
        var verdict = Analyze("""
            {"tagOwners":{"tag:onesalem-host":["autogroup:admin"],"tag:onesalem-client":["tag:onesalem-host"]}}
            """);

        Assert.Equal(ConnectPolicyVerdictKind.Unsafe, verdict.Kind);
    }

    private static ConnectPolicyVerdict Analyze(string json) =>
        ConnectPolicyAnalyzer.Analyze(Encoding.UTF8.GetBytes(json));
}
