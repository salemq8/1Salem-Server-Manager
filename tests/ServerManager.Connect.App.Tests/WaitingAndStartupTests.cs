using ServerManager.Connect.App.Broker;
using ServerManager.Connect.App.Tests.Fakes;
using ServerManager.Connect.App.ViewModels;

namespace ServerManager.Connect.App.Tests;

public sealed class WaitingAndStartupTests
{
    [Fact]
    public async Task Waiting_page_moves_to_servers_once_the_owner_approves()
    {
        using var app = new AppHarness();
        var membership = app.AddMembership(MembershipState.Pending);
        app.Main.ShowWaiting(new InviteRedemption(membership.MembershipId, AppHarness.ServerLabel));
        var waiting = Assert.IsType<WaitingViewModel>(app.Main.CurrentPage);

        Assert.False(await waiting.PollOnceAsync(CancellationToken.None));
        Assert.Same(waiting, app.Main.CurrentPage);
        Assert.Equal("Waiting for the owner to approve…", waiting.StatusText);

        app.Broker.SetState(membership.MembershipId, MembershipState.Approved);
        Assert.True(await waiting.PollOnceAsync(CancellationToken.None));

        var servers = Assert.IsType<ServersViewModel>(app.Main.CurrentPage);
        await servers.RefreshAsync(CancellationToken.None);
        Assert.Equal("Enrollment pending", Assert.Single(servers.Servers).StatusText);
    }

    [Fact]
    public async Task Waiting_page_reports_a_revocation_and_stops()
    {
        using var app = new AppHarness();
        var membership = app.AddMembership(MembershipState.Revoked);
        app.Main.ShowWaiting(new InviteRedemption(membership.MembershipId, AppHarness.ServerLabel));
        var waiting = Assert.IsType<WaitingViewModel>(app.Main.CurrentPage);

        Assert.True(await waiting.PollOnceAsync(CancellationToken.None));

        Assert.Equal("Access revoked", waiting.StatusText);
        Assert.False(waiting.IsWaiting);
    }

    [Fact]
    public async Task Unconfigured_app_says_so_and_offers_nothing_that_would_pretend_to_work()
    {
        using var app = new AppHarness(configured: false);

        await app.Main.InitializeAsync();
        app.Main.ShowInvite();

        var welcome = Assert.IsType<WelcomeViewModel>(app.Main.CurrentPage);
        Assert.Equal("1Salem Connect is not configured yet", welcome.Headline);
        Assert.False(welcome.CanGetStarted);
        Assert.False(welcome.GetStartedCommand.CanExecute(null));
        Assert.False(app.Main.CanUseServers);
        Assert.Equal(0, app.Broker.RegisterCalls);
    }

    [Fact]
    public async Task Returning_friend_starts_on_the_server_list()
    {
        using var app = new AppHarness();
        app.AddMembership(MembershipState.Approved, "nFAKE1CNTRL");

        await app.Main.InitializeAsync();

        var servers = Assert.IsType<ServersViewModel>(app.Main.CurrentPage);
        await servers.RefreshAsync(CancellationToken.None);
        Assert.Equal("Ready", Assert.Single(servers.Servers).StatusText);
    }

    [Fact]
    public async Task Broker_outage_at_startup_offers_a_retry()
    {
        using var app = new AppHarness();
        var outage = new FailingBroker();
        var context = new Services.ConnectAppContext
        {
            Settings = app.Context.Settings,
            Services = new Services.ConnectServices(outage, app.Identity, app.Process),
            Transport = app.Transport,
            Clock = app.Clock,
            Log = app.Log,
            Clipboard = app.Clipboard
        };
        var main = new MainViewModel(context);

        await main.InitializeAsync();

        var welcome = Assert.IsType<WelcomeViewModel>(main.CurrentPage);
        Assert.True(welcome.CanRetry);
        Assert.Equal("Can't reach 1Salem Connect. Check your internet connection and try again.", welcome.ErrorText);
    }

    private sealed class FailingBroker : IBrokerClient
    {
        private static BrokerException Outage => new(BrokerFailure.Unreachable, "the broker could not be reached.");

        public Task<string> RegisterDeviceAsync(CancellationToken cancellationToken) => Task.FromException<string>(Outage);

        public Task<InviteRedemption> RedeemInviteAsync(string secret, CancellationToken cancellationToken) => Task.FromException<InviteRedemption>(Outage);

        public Task<IReadOnlyList<Membership>> GetMembershipsAsync(CancellationToken cancellationToken) => Task.FromException<IReadOnlyList<Membership>>(Outage);

        public Task<EnrollmentPackage?> TakeEnrollmentAsync(string membershipId, CancellationToken cancellationToken) => Task.FromException<EnrollmentPackage?>(Outage);

        public Task BindNodeAsync(string membershipId, string nodeId, CancellationToken cancellationToken) => Task.FromException(Outage);

        public Task<SessionTicket> CreateSessionAsync(string membershipId, string sessionSpki, CancellationToken cancellationToken) => Task.FromException<SessionTicket>(Outage);

        public Task<byte[]> GetTicketKeysAsync(CancellationToken cancellationToken) => Task.FromException<byte[]>(Outage);
    }
}
