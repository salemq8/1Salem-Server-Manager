using ServerManager.Connect.App.Broker;
using ServerManager.Connect.App.Invites;
using ServerManager.Connect.App.Tests.Fakes;
using ServerManager.Connect.App.ViewModels;

namespace ServerManager.Connect.App.Tests;

public sealed class InviteTests
{
    private const string GenericFailure = "This invite isn't valid. Ask the server owner for a new one.";

    [Fact]
    public void Link_yields_the_secret_from_its_fragment()
    {
        var secret = TestIds.NewInviteSecret();

        Assert.True(InviteParser.TryParse($"https://connect.1salem.app/i#{secret}", out var parsed));
        Assert.Equal(secret, parsed);
    }

    [Fact]
    public void Bare_code_is_accepted_with_surrounding_whitespace()
    {
        var secret = TestIds.NewInviteSecret();

        Assert.True(InviteParser.TryParse($"  {secret}\r\n", out var parsed));
        Assert.Equal(secret, parsed);
    }

    [Theory]
    [InlineData("https://connect.1salem.app/i")]
    [InlineData("https://connect.1salem.app/i#")]
    [InlineData("ftp://connect.1salem.app/i#{0}")]
    [InlineData("https://connect.1salem.app/i?secret={0}")]
    [InlineData("https://connect.1salem.app/i/{0}")]
    [InlineData("{0}=")]
    [InlineData("{0}A")]
    [InlineData("not an invite")]
    [InlineData("")]
    public void Anything_else_is_refused(string template)
    {
        var input = string.Format(System.Globalization.CultureInfo.InvariantCulture, template, TestIds.NewInviteSecret());

        Assert.False(InviteParser.TryParse(input, out _));
    }

    [Fact]
    public async Task Joining_with_a_link_redeems_the_fragment_secret_and_waits_for_approval()
    {
        using var app = new AppHarness();
        var secret = TestIds.NewInviteSecret();
        app.Broker.Redemption = new InviteRedemption(TestIds.MembershipId(), AppHarness.ServerLabel);
        app.Main.ShowInvite();
        var invite = Assert.IsType<InviteViewModel>(app.Main.CurrentPage);

        invite.InviteText = $"https://connect.1salem.app/i#{secret}";
        await invite.JoinAsync();

        Assert.Equal(secret, Assert.Single(app.Broker.RedeemedSecrets));
        Assert.Equal(1, app.Broker.RegisterCalls);
        var waiting = Assert.IsType<WaitingViewModel>(app.Main.CurrentPage);
        Assert.Equal(AppHarness.ServerLabel, waiting.ServerLabel);
        Assert.Empty(invite.InviteText);
    }

    [Fact]
    public async Task Unknown_invite_and_malformed_input_show_the_same_generic_failure()
    {
        using var app = new AppHarness();
        app.Broker.RedeemFailure = new BrokerException(BrokerFailure.NotFound, "redeem invite: HTTP 404 not_found.");
        app.Main.ShowInvite();
        var invite = Assert.IsType<InviteViewModel>(app.Main.CurrentPage);

        invite.InviteText = TestIds.NewInviteSecret();
        await invite.JoinAsync();
        var rejectedByBroker = invite.ErrorText;

        invite.InviteText = "https://connect.1salem.app/i#too-short";
        await invite.JoinAsync();
        var rejectedLocally = invite.ErrorText;

        Assert.Equal(GenericFailure, rejectedByBroker);
        Assert.Equal(GenericFailure, rejectedLocally);
        Assert.Single(app.Broker.RedeemedSecrets);
        Assert.Same(invite, app.Main.CurrentPage);
    }

    [Fact]
    public async Task Broker_outage_is_reported_as_such_not_as_a_bad_invite()
    {
        using var app = new AppHarness();
        app.Broker.RedeemFailure = new BrokerException(BrokerFailure.Unreachable, "redeem invite: the broker could not be reached.");
        app.Main.ShowInvite();
        var invite = Assert.IsType<InviteViewModel>(app.Main.CurrentPage);

        invite.InviteText = TestIds.NewInviteSecret();
        await invite.JoinAsync();

        Assert.Equal("Can't reach 1Salem Connect. Check your internet connection and try again.", invite.ErrorText);
    }
}
