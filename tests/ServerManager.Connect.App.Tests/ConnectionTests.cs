using System.Security.Cryptography;
using ServerManager.Connect.App.Broker;
using ServerManager.Connect.App.Localization;
using ServerManager.Connect.App.Tests.Fakes;
using ServerManager.Connect.App.Transport;
using ServerManager.Connect.App.ViewModels;
using ServerManager.Connect.Core.Crypto;

namespace ServerManager.Connect.App.Tests;

public sealed class ConnectionTests
{
    [Fact]
    public async Task Connect_shows_the_local_address_and_hands_the_transport_the_ticket_key()
    {
        using var app = new AppHarness();
        var connection = Open(app, out var membership);

        await connection.ConnectAsync();

        Assert.Equal(ConnectionState.Connected, connection.State);
        Assert.Equal("Connected", connection.StateText);
        Assert.Equal("127.0.0.1:18211", connection.LocalAddress);
        var open = Assert.Single(app.Transport.Opens);
        Assert.Equal(membership.OwnerId, open.Node);
        Assert.Equal(0, open.PreferredPort);
        Assert.Equal(Assert.Single(app.Broker.SessionSpkis), SpkiOf(open.SessionKey));

        connection.CopyAddressCommand.Execute(null);
        Assert.Equal("127.0.0.1:18211", app.Clipboard.Text);
    }

    [Fact]
    public async Task Every_session_gets_a_fresh_key()
    {
        using var app = new AppHarness();
        var connection = Open(app, out _);

        await connection.ConnectAsync();
        await connection.DisconnectAsync();
        await connection.ConnectAsync();

        Assert.Equal(2, app.Transport.Opens.Count);
        Assert.NotEqual(app.Transport.Opens[0].SessionKey, app.Transport.Opens[1].SessionKey);
        Assert.NotEqual(app.Broker.SessionSpkis[0], app.Broker.SessionSpkis[1]);
        Assert.Equal(["ses_test1"], app.Transport.Closed);
    }

    [Fact]
    public async Task Disconnect_closes_the_session_and_clears_the_address()
    {
        using var app = new AppHarness();
        var connection = Open(app, out _);
        await connection.ConnectAsync();

        await connection.DisconnectAsync();

        Assert.Equal("Disconnected", connection.StateText);
        Assert.Null(connection.LocalAddress);
        Assert.Equal("ses_test1", Assert.Single(app.Transport.Closed));
    }

    [Fact]
    public async Task Ticket_is_refreshed_before_expiry_with_a_new_session_key()
    {
        using var app = new AppHarness();
        var connection = Open(app, out _);
        var firstExpiry = app.Broker.SessionExpiresAt;
        await connection.ConnectAsync();

        app.Clock.UtcNow = firstExpiry - TimeSpan.FromMinutes(1);
        app.Broker.SessionExpiresAt = firstExpiry.AddMinutes(10);
        await connection.CheckSessionAsync(CancellationToken.None);

        var refresh = Assert.Single(app.Transport.Refreshes);
        Assert.Equal("ses_test1", refresh.SessionId);
        Assert.NotEqual(app.Transport.Opens[0].SessionKey, refresh.SessionKey);
        Assert.Equal(app.Broker.SessionSpkis[1], SpkiOf(refresh.SessionKey));
        Assert.Equal("Connected", connection.StateText);
    }

    [Fact]
    public async Task Revoked_membership_shows_access_revoked_when_connecting()
    {
        using var app = new AppHarness();
        var connection = Open(app, out var membership);
        app.Broker.SetState(membership.MembershipId, MembershipState.Revoked);
        app.Broker.SessionFailure = new BrokerException(BrokerFailure.NotFound, "create session: HTTP 404 not_found.");

        await connection.ConnectAsync();

        Assert.Equal("Access revoked", connection.StateText);
        Assert.False(connection.CanConnect);
        Assert.Empty(app.Transport.Opens);
    }

    [Fact]
    public async Task Revocation_during_a_session_ends_it_as_access_revoked()
    {
        using var app = new AppHarness();
        var connection = Open(app, out var membership);
        var expiry = app.Broker.SessionExpiresAt;
        await connection.ConnectAsync();

        app.Broker.SetState(membership.MembershipId, MembershipState.Revoked);
        app.Broker.SessionFailure = new BrokerException(BrokerFailure.NotFound, "create session: HTTP 404 not_found.");
        app.Clock.UtcNow = expiry - TimeSpan.FromMinutes(1);
        await connection.CheckSessionAsync(CancellationToken.None);

        Assert.Equal("Access revoked", connection.StateText);
        Assert.Null(connection.LocalAddress);
        Assert.Equal("ses_test1", Assert.Single(app.Transport.Closed));
        await connection.MonitorTask!;
    }

    [Fact]
    public async Task Ticket_that_cannot_be_renewed_ends_as_access_expired()
    {
        using var app = new AppHarness();
        var connection = Open(app, out _);
        var expiry = app.Broker.SessionExpiresAt;
        await connection.ConnectAsync();
        app.Broker.SessionFailure = new BrokerException(BrokerFailure.Unreachable, "create session: the broker could not be reached.");

        // Inside the refresh window but not yet expired: keep the session and keep trying.
        app.Clock.UtcNow = expiry - TimeSpan.FromMinutes(1);
        await connection.CheckSessionAsync(CancellationToken.None);
        Assert.Equal("Connected", connection.StateText);

        app.Clock.UtcNow = expiry + TimeSpan.FromSeconds(1);
        await connection.CheckSessionAsync(CancellationToken.None);

        Assert.Equal("Access expired", connection.StateText);
        Assert.Equal("ses_test1", Assert.Single(app.Transport.Closed));
        Assert.True(connection.CanConnect);
    }

    [Fact]
    public async Task Unreachable_host_shows_server_offline_until_a_connection_gets_through()
    {
        using var app = new AppHarness();
        var connection = Open(app, out _);
        await connection.ConnectAsync();

        app.Transport.Log.Add("2026-09-24T10:00:00Z friend: session ses_test1 could not reach the host bridge: dial tcp 127.0.0.1:7780: connect: connection refused");
        await connection.CheckSessionAsync(CancellationToken.None);
        Assert.Equal("Server offline", connection.StateText);
        Assert.True(connection.CanDisconnect);

        app.Transport.Log.Add("2026-09-24T10:01:00Z friend: session ses_test1 connection ended (512 bytes up, 2048 bytes down)");
        await connection.CheckSessionAsync(CancellationToken.None);
        Assert.Equal("Connected", connection.StateText);
    }

    [Fact]
    public async Task Refused_connection_after_revocation_shows_access_revoked()
    {
        using var app = new AppHarness();
        var connection = Open(app, out var membership);
        await connection.ConnectAsync();

        app.Broker.SetState(membership.MembershipId, MembershipState.Revoked);
        app.Transport.Log.Add("2026-09-24T10:00:00Z friend: session ses_test1 connection not accepted: host refused the connection");
        await connection.CheckSessionAsync(CancellationToken.None);

        Assert.Equal("Access revoked", connection.StateText);
    }

    [Fact]
    public async Task A_live_connection_ended_by_a_revocation_shows_access_revoked()
    {
        using var app = new AppHarness();
        var connection = Open(app, out var membership);
        await connection.ConnectAsync();

        // Revoking a friend closes their live connections from the owner's side; locally that is
        // just a connection that ended.
        app.Broker.SetState(membership.MembershipId, MembershipState.Revoked);
        app.Transport.Log.Add("2026-09-24T10:00:00Z friend: session ses_test1 connection ended (512 bytes up, 2048 bytes down)");
        await connection.CheckSessionAsync(CancellationToken.None);

        Assert.Equal("Access revoked", connection.StateText);
        Assert.Null(connection.LocalAddress);
        Assert.Equal("ses_test1", Assert.Single(app.Transport.Closed));
    }

    [Fact]
    public async Task A_transport_that_cannot_produce_its_log_keeps_the_session()
    {
        using var app = new AppHarness();
        var connection = Open(app, out _);
        await connection.ConnectAsync();
        app.Transport.DiagnosticsFailure = new TransportException("internal");

        await connection.CheckSessionAsync(CancellationToken.None);
        await connection.CheckSessionAsync(CancellationToken.None);

        Assert.Equal("Connected", connection.StateText);
        Assert.Equal("127.0.0.1:18211", connection.LocalAddress);
        Assert.Empty(app.Transport.Closed);
        Assert.Single(app.Log.Entries);
    }

    [Fact]
    public async Task Session_lost_by_the_transport_is_reported_as_disconnected()
    {
        using var app = new AppHarness();
        var connection = Open(app, out _);
        await connection.ConnectAsync();

        app.Transport.Sessions.Clear();
        await connection.CheckSessionAsync(CancellationToken.None);

        Assert.Equal("Disconnected", connection.StateText);
        Assert.Equal("The connection stopped. Connect again to continue.", connection.Message);
    }

    [Fact]
    public async Task A_connection_accepted_after_a_failure_shows_connected_while_the_game_runs()
    {
        using var app = new AppHarness();
        var connection = Open(app, out _);
        await connection.ConnectAsync();

        app.Transport.Log.Add("2026-09-24T10:00:00Z friend: session ses_test1 could not reach the host bridge: dial tcp 127.0.0.1:7780: connect: connection refused");
        await connection.CheckSessionAsync(CancellationToken.None);
        Assert.Equal("Server offline", connection.StateText);

        // The next attempt gets through and the game keeps playing, so no "ended" line follows.
        app.Transport.Log.Add("2026-09-24T10:01:00Z friend: session ses_test1 connection accepted");
        await connection.CheckSessionAsync(CancellationToken.None);
        await connection.CheckSessionAsync(CancellationToken.None);

        Assert.Equal("Connected", connection.StateText);
    }

    [Fact]
    public async Task A_failure_nobody_expected_while_connecting_does_not_leave_the_page_connecting()
    {
        using var app = new AppHarness();
        var connection = Open(app, out _);

        // A failure no catch was written for: before the broker's expiry was range-checked, an
        // answer from it could raise this one.
        app.Broker.SessionFailure = new ArgumentOutOfRangeException("seconds");
        await connection.ConnectAsync();

        Assert.Equal(ConnectionState.Disconnected, connection.State);
        Assert.Equal(Text.ErrorUnexpected, connection.Message);
        Assert.True(connection.CanConnect);
        Assert.True(connection.ConnectCommand.CanExecute(null));
        Assert.Null(connection.LocalAddress);
        Assert.Empty(app.Transport.Opens);
        Assert.Single(app.Log.Entries);
    }

    [Fact]
    public async Task A_failure_nobody_expected_while_telling_a_revocation_apart_does_not_leave_the_page_connecting()
    {
        using var app = new AppHarness();
        var connection = Open(app, out _);

        // The 404 is generic, so the membership is looked up; that lookup fails in a way no catch
        // was written for. It runs inside a catch block, out of reach of Connect's catch-all.
        app.Broker.SessionFailure = new BrokerException(BrokerFailure.NotFound, "create session: HTTP 404 not_found.");
        app.Broker.MembershipsFailure = new InvalidOperationException("membership lookup");
        await connection.ConnectAsync();

        Assert.Equal(ConnectionState.Disconnected, connection.State);
        Assert.Equal(Text.ErrorUnexpected, connection.Message);
        Assert.True(connection.CanConnect);
        Assert.True(connection.ConnectCommand.CanExecute(null));
        Assert.Empty(app.Transport.Opens);
    }

    [Fact]
    public async Task Exit_closes_every_session_this_run_opened_before_it_stops_the_transport()
    {
        using var app = new AppHarness();
        var first = Open(app, out _);
        await first.ConnectAsync();
        app.Main.ShowConnection(app.AddMembership(MembershipState.Approved, "nFAKE1CNTRL", idLetter: 'b'));
        var second = Assert.IsType<ConnectionViewModel>(app.Main.CurrentPage);
        await second.ConnectAsync();
        await first.DisconnectAsync();
        List<string>? closedByStop = null;
        app.Process.Stopping = () => closedByStop = [.. app.Transport.Closed];

        app.Main.Shutdown();

        // The harness's transport stands for one another copy of the app started: stopping it
        // ends nothing, so the session still open is closed first. The one already closed is not
        // closed again.
        Assert.Equal(["ses_test1", "ses_test2"], closedByStop);
        Assert.Empty(app.Transport.Sessions);
        Assert.Equal(1, app.Process.StopCalls);
    }

    [Fact]
    public async Task Exit_does_not_wait_for_a_transport_that_does_not_answer()
    {
        using var app = new AppHarness();
        var connection = Open(app, out _);
        await connection.ConnectAsync();
        app.Transport.CloseNeverAnswers = true;

        await Task.Run(app.Main.Shutdown).WaitAsync(MainViewModel.SessionCloseAtExitTimeout + TimeSpan.FromSeconds(10));

        Assert.Equal("ses_test1", Assert.Single(app.Transport.Closed));
        Assert.Equal(1, app.Process.StopCalls);
        Assert.Contains(app.Log.Entries, entry => entry.Contains("1 of this run's sessions could not be closed", StringComparison.Ordinal));

        // The harness exits the app again when the test ends; that one need not wait too.
        app.Transport.CloseNeverAnswers = false;
    }

    [Theory]
    [InlineData(TransportErrorCodes.NoAnswer)]
    [InlineData(TransportErrorCodes.Unavailable)]
    [InlineData(TransportErrorCodes.Untrusted)]
    public async Task A_disconnect_that_cannot_be_confirmed_keeps_the_address_until_it_can(string code)
    {
        using var app = new AppHarness();
        var connection = Open(app, out _);
        await connection.ConnectAsync();

        // The close may not have happened, and the transport cannot say either.
        app.Transport.CloseFailures.Enqueue(new TransportException(code));
        app.Transport.StatusFailure = new TransportException(code);
        await connection.DisconnectAsync();

        Assert.Equal("Connected", connection.StateText);
        Assert.Equal("127.0.0.1:18211", connection.LocalAddress);
        Assert.Equal(Text.ErrorCloseUnconfirmed, connection.Message);
        Assert.True(connection.CanDisconnect);
        Assert.False(connection.CanConnect);

        // The monitor's next step tries again, and nothing else, until the close goes through.
        app.Transport.StatusFailure = null;
        await connection.CheckSessionAsync(CancellationToken.None);

        Assert.Equal("Disconnected", connection.StateText);
        Assert.Null(connection.LocalAddress);
        Assert.Null(connection.Message);
        Assert.Equal(["ses_test1", "ses_test1"], app.Transport.Closed);
        Assert.Empty(app.Transport.Sessions);
    }

    [Fact]
    public async Task A_close_whose_answer_was_lost_is_settled_by_the_transports_status()
    {
        using var app = new AppHarness();
        var connection = Open(app, out _);
        await connection.ConnectAsync();

        // The transport closed the session, but its answer never arrived.
        app.Transport.Sessions.Clear();
        app.Transport.CloseFailures.Enqueue(new TransportException(TransportErrorCodes.NoAnswer));
        await connection.DisconnectAsync();

        Assert.Equal("Disconnected", connection.StateText);
        Assert.Null(connection.LocalAddress);
        Assert.Single(app.Transport.Closed);
    }

    [Theory]
    [InlineData(TransportErrorCodes.Unavailable)]
    [InlineData(TransportErrorCodes.Untrusted)] // say, another copy's transport serves the pipe now
    public async Task Every_address_is_closed_once_the_transports_that_served_this_app_have_exited(string code)
    {
        using var app = new AppHarness();
        var connection = Open(app, out _);
        await connection.ConnectAsync();

        app.Transport.CloseFailures.Enqueue(new TransportException(code));
        app.Transport.StatusFailure = new TransportException(code);
        app.Process.UsedTransportsExited = true;
        await connection.DisconnectAsync();

        Assert.Equal("Disconnected", connection.StateText);
        Assert.Null(connection.LocalAddress);
    }

    [Fact]
    public async Task A_revocation_that_cannot_close_yet_still_ends_as_access_revoked()
    {
        using var app = new AppHarness();
        var connection = Open(app, out var membership);
        await connection.ConnectAsync();
        app.Broker.SetState(membership.MembershipId, MembershipState.Revoked);

        // Two closes fail without an answer; status still lists the session each time.
        app.Transport.CloseFailures.Enqueue(new TransportException(TransportErrorCodes.NoAnswer));
        app.Transport.CloseFailures.Enqueue(new TransportException(TransportErrorCodes.NoAnswer));

        app.Transport.Log.Add("2026-09-24T10:00:00Z friend: session ses_test1 connection ended (512 bytes up, 2048 bytes down)");
        await connection.CheckSessionAsync(CancellationToken.None);
        Assert.Equal("127.0.0.1:18211", connection.LocalAddress);
        Assert.Equal(Text.ErrorCloseUnconfirmed, connection.Message);

        // Pressing Disconnect meanwhile does not turn the revocation into a plain disconnect.
        await connection.DisconnectAsync();
        Assert.Equal("127.0.0.1:18211", connection.LocalAddress);

        await connection.CheckSessionAsync(CancellationToken.None);

        Assert.Equal("Access revoked", connection.StateText);
        Assert.Null(connection.LocalAddress);
        Assert.False(connection.CanConnect);
        Assert.Equal(3, app.Transport.Closed.Count);
    }

    [Fact]
    public async Task A_busy_transport_does_not_end_a_healthy_session()
    {
        using var app = new AppHarness();
        var connection = Open(app, out _);
        await connection.ConnectAsync();

        app.Transport.StatusFailure = new TransportException(TransportErrorCodes.NoAnswer);
        await connection.CheckSessionAsync(CancellationToken.None);

        Assert.Equal("Connected", connection.StateText);
        Assert.Equal("127.0.0.1:18211", connection.LocalAddress);
        Assert.Empty(app.Transport.Closed);
    }

    [Fact]
    public async Task A_refresh_the_transport_was_too_busy_to_confirm_is_sent_again_on_the_next_step()
    {
        using var app = new AppHarness();
        var connection = Open(app, out _);
        var expiry = app.Broker.SessionExpiresAt;
        await connection.ConnectAsync();

        app.Clock.UtcNow = expiry - TimeSpan.FromMinutes(1);
        app.Transport.NextRefreshFailure = new TransportException(TransportErrorCodes.NoAnswer);
        await connection.CheckSessionAsync(CancellationToken.None);
        Assert.Equal("Connected", connection.StateText);

        app.Broker.SessionExpiresAt = expiry.AddMinutes(10);
        await connection.CheckSessionAsync(CancellationToken.None);

        Assert.Equal(2, app.Transport.Refreshes.Count);
        Assert.Equal("Connected", connection.StateText);
        Assert.Empty(app.Transport.Closed);
    }

    [Fact]
    public void A_foreign_pipe_is_explained_differently_from_a_broken_component()
    {
        Assert.Equal(Text.ErrorTransportUntrusted, UserMessages.For(new TransportException(TransportErrorCodes.Untrusted)));
        Assert.Equal(Text.ErrorTransportUnavailable, UserMessages.For(new TransportException(TransportErrorCodes.Unavailable)));
        Assert.Equal(Text.ErrorTransportUnavailable, UserMessages.For(new TransportException(TransportErrorCodes.NoAnswer)));
    }

    private static ConnectionViewModel Open(AppHarness app, out Membership membership)
    {
        membership = app.AddMembership(MembershipState.Approved, "nFAKE1CNTRL");
        app.Main.ShowConnection(membership);
        return Assert.IsType<ConnectionViewModel>(app.Main.CurrentPage);
    }

    private static string SpkiOf(string pkcs8Base64Url)
    {
        using var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(Base64Url.Decode(pkcs8Base64Url), out _);
        return Base64Url.Encode(key.ExportSubjectPublicKeyInfo());
    }
}
