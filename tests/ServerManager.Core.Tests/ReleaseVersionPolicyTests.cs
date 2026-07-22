using ServerManager.Core;

namespace ServerManager.Core.Tests;

public sealed class ReleaseVersionPolicyTests
{
    [Fact]
    public void LowerTarget_IsRejected()
    {
        var result = ReleaseVersionPolicy.Evaluate("1.3.0", ["1.3.1"]);

        Assert.False(result.Allowed);
        Assert.Contains("Choose at least 1.3.2", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EqualTarget_IsRejectedWithoutDeveloperOverride()
    {
        var result = ReleaseVersionPolicy.Evaluate("1.3.1", ["1.3.1"]);

        Assert.False(result.Allowed);
        Assert.Contains("Release blocked", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EqualTarget_IsAllowedOnlyWithExplicitRebuildSwitch()
    {
        var denied = ReleaseVersionPolicy.Evaluate("1.3.1", ["1.3.1"]);
        var allowed = ReleaseVersionPolicy.Evaluate("1.3.1", ["1.3.1"], true);

        Assert.False(denied.Allowed);
        Assert.True(allowed.Allowed);
        Assert.Contains("explicitly enabled", allowed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SemanticComparison_TreatsTenAsNewerThanNine()
    {
        var result = ReleaseVersionPolicy.Evaluate("1.3.9", ["1.3.10"]);

        Assert.False(result.Allowed);
        Assert.Equal(SemanticVersion.Parse("1.3.10"), result.HighestKnown);
    }

    [Theory]
    [InlineData("1.3.1", "Patch", "1.3.2")]
    [InlineData("1.3.1", "Minor", "1.4.0")]
    [InlineData("1.3.1", "Major", "2.0.0")]
    public void NextVersion_CalculatesRequestedPart(
        string highest,
        string part,
        string expected) =>
        Assert.Equal(expected, ReleaseVersionPolicy.Next(highest, part).ToString());
}
