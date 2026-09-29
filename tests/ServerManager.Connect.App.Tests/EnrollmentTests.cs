using System.Text.Json;
using System.Text.RegularExpressions;
using ServerManager.Connect.App.Broker;
using ServerManager.Connect.App.Services;
using ServerManager.Connect.App.Tests.Fakes;
using ServerManager.Connect.App.Transport;
using ServerManager.Connect.App.ViewModels;

namespace ServerManager.Connect.App.Tests;

public sealed class EnrollmentTests
{
    private const string AuthKey = "tskey-auth-kTest1CNTRL-abcdef0123456789";

    [Fact]
    public async Task Server_shows_enrollment_pending_until_the_blob_is_handed_to_the_transport_exactly_once()
    {
        using var app = new AppHarness();
        var membership = app.AddMembership(MembershipState.Approved);
        app.Main.ShowServers();
        var servers = Assert.IsType<ServersViewModel>(app.Main.CurrentPage);

        // Approved, but the owner's Server Manager has not posted the blob yet.
        await servers.RefreshAsync(CancellationToken.None);
        Assert.Equal("Enrollment pending", Assert.Single(servers.Servers).StatusText);
        Assert.Empty(app.Transport.Enrollments);

        app.Broker.Enrollments[membership.MembershipId] = app.EnrollmentFor(membership, AuthKey, "kTest1CNTRL");
        await servers.RefreshAsync(CancellationToken.None);

        var enrolled = Assert.Single(app.Transport.Enrollments);
        Assert.Equal(AuthKey, enrolled.AuthKey);
        Assert.Equal(membership.OwnerId, enrolled.Node);
        Assert.Matches(new Regex("^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$"), enrolled.Hostname);
        Assert.Equal((membership.MembershipId, app.Transport.NextNodeId), Assert.Single(app.Broker.Bound));
        Assert.Equal("Waiting for the owner to finish setting up this PC", Assert.Single(servers.Servers).StatusText);
        Assert.False(Assert.Single(servers.Servers).CanOpen);

        app.Broker.SetNodeState(membership.MembershipId, MembershipNodeState.Confirmed);
        await servers.RefreshAsync(CancellationToken.None);
        Assert.Equal("Ready", Assert.Single(servers.Servers).StatusText);

        await servers.RefreshAsync(CancellationToken.None);
        await servers.RefreshAsync(CancellationToken.None);
        Assert.Single(app.Transport.Enrollments);
    }

    [Fact]
    public async Task A_failed_bind_is_retried_from_the_enrolled_node_without_touching_the_key_again()
    {
        using var app = new AppHarness();
        var membership = app.AddMembership(MembershipState.Approved);
        app.Broker.DeleteEnrollmentOnRead = false;
        app.Broker.Enrollments[membership.MembershipId] = app.EnrollmentFor(membership, AuthKey, "kTest1CNTRL");
        app.Broker.NextBindFailure = new BrokerException(BrokerFailure.Unreachable, "bind node: the broker could not be reached.");
        app.Main.ShowServers();
        var servers = Assert.IsType<ServersViewModel>(app.Main.CurrentPage);

        var stuck = Assert.Single(servers.Servers);
        Assert.Equal("Enrollment pending", stuck.StatusText);
        Assert.True(stuck.HasProblem);

        await servers.RefreshAsync(CancellationToken.None);

        Assert.Single(app.Transport.Enrollments);
        Assert.Equal(1, app.Broker.TakeEnrollmentCalls);
        Assert.Single(app.Broker.Bound);
        Assert.Equal("Waiting for the owner to finish setting up this PC", Assert.Single(servers.Servers).StatusText);

        app.Broker.SetNodeState(membership.MembershipId, MembershipNodeState.Confirmed);
        await servers.RefreshAsync(CancellationToken.None);
        Assert.Equal("Ready", Assert.Single(servers.Servers).StatusText);
    }

    [Fact]
    public async Task A_blob_served_twice_is_never_handed_to_the_transport_twice()
    {
        using var app = new AppHarness();
        var membership = app.AddMembership(MembershipState.Approved);
        app.Broker.DeleteEnrollmentOnRead = false;
        app.Broker.Enrollments[membership.MembershipId] = app.EnrollmentFor(membership, AuthKey, "kTest1CNTRL");
        app.Broker.NextBindFailure = new BrokerException(BrokerFailure.Unreachable, "bind node: the broker could not be reached.");
        app.Main.ShowServers();
        var servers = Assert.IsType<ServersViewModel>(app.Main.CurrentPage);

        // The transport lost the node (say it restarted in fake mode) and the broker serves the same blob again.
        app.Transport.Nodes.Clear();
        await servers.RefreshAsync(CancellationToken.None);

        Assert.Single(app.Transport.Enrollments);
        var failed = Assert.Single(servers.Servers);
        Assert.Equal("Setup failed", failed.StatusText);
        Assert.Equal("Setting up this server failed. Ask the server owner to approve this PC again.", failed.Problem);
    }

    [Theory]
    [InlineData(Transport.TransportErrorCodes.Unavailable)]
    [InlineData(Transport.TransportErrorCodes.NoAnswer)]
    [InlineData(Transport.TransportErrorCodes.Untrusted)]
    public async Task A_transport_lost_during_enrollment_is_reported_as_failed_not_as_pending_forever(string code)
    {
        using var app = new AppHarness();
        var membership = app.AddMembership(MembershipState.Approved);
        app.Broker.Enrollments[membership.MembershipId] = app.EnrollmentFor(membership, AuthKey, "kTest1CNTRL");
        var failure = new Transport.TransportException(code);
        app.Transport.NextEnrollFailure = failure;
        app.Main.ShowServers();
        var servers = Assert.IsType<ServersViewModel>(app.Main.CurrentPage);

        // The transport went away, did not answer in time, or is not this app's: this refresh
        // reports that failure itself.
        Assert.Equal(UserMessages.For(failure), Assert.Single(servers.Servers).Problem);

        // No node came up, and the one-time blob is gone: only a new approval helps.
        await servers.RefreshAsync(CancellationToken.None);

        var stuck = Assert.Single(servers.Servers);
        Assert.Equal("Setup failed", stuck.StatusText);
        Assert.Equal("Setting up this server failed. Ask the server owner to approve this PC again.", stuck.Problem);
        Assert.Single(app.Transport.Enrollments);
        Assert.Empty(app.Broker.Bound);
    }

    [Fact]
    public async Task An_enrollment_cancelled_after_the_key_was_handed_over_is_failed_not_pending()
    {
        using var app = new AppHarness();
        var membership = app.AddMembership(MembershipState.Approved);
        app.Broker.Enrollments[membership.MembershipId] = app.EnrollmentFor(membership, AuthKey, "kTest1CNTRL");
        var enrollment = Coordinator(app, consumedPath: null);

        // Say the friend left the servers page while the transport was bringing the node up.
        app.Transport.NextEnrollFailure = new OperationCanceledException();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => enrollment.TryCompleteAsync(membership, CancellationToken.None));

        // No node came up, and the one-time blob is gone.
        var next = await enrollment.TryCompleteAsync(membership, CancellationToken.None);

        Assert.Equal(EnrollmentOutcome.Failed, next.Outcome);
        Assert.Single(app.Transport.Enrollments);
        Assert.Empty(app.Broker.Bound);
    }

    [Fact]
    public async Task After_a_restart_a_used_blob_that_left_no_node_is_still_failed_not_pending()
    {
        var directory = Path.Combine(Path.GetTempPath(), "1salem-connect-app-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, ConsumedEnrollments.FileName);
        try
        {
            using var app = new AppHarness();
            var membership = app.AddMembership(MembershipState.Approved);
            var untouched = app.AddMembership(MembershipState.Approved, idLetter: 'b');
            app.Broker.Enrollments[membership.MembershipId] = app.EnrollmentFor(membership, AuthKey, "kTest1CNTRL");
            app.Transport.NextEnrollFailure = new TransportException(TransportErrorCodes.Unavailable);
            await Assert.ThrowsAsync<TransportException>(() => Coordinator(app, path).TryCompleteAsync(membership, CancellationToken.None));

            // The next run remembers nothing by itself; the broker has no blob any more, and no node came up.
            var restarted = Coordinator(app, path);

            Assert.Equal(EnrollmentOutcome.Failed, (await restarted.TryCompleteAsync(membership, CancellationToken.None)).Outcome);
            Assert.Equal(EnrollmentOutcome.Pending, (await restarted.TryCompleteAsync(untouched, CancellationToken.None)).Outcome);

            // Membership ids and replacement flags only, never the auth key; written whole,
            // nothing left beside it.
            var saved = File.ReadAllText(path);
            Assert.DoesNotContain(AuthKey, saved, StringComparison.Ordinal);
            using (var document = JsonDocument.Parse(saved))
            {
                Assert.Equal(
                    [membership.MembershipId],
                    document.RootElement.GetProperty("memberships").EnumerateArray().Select(value => value.GetString()));
                Assert.Empty(document.RootElement.GetProperty("replacements").EnumerateArray());
            }
            Assert.Equal([path], Directory.GetFiles(directory));

            // The owner approves again, the new blob enrolls, and the mark is gone.
            app.Broker.Enrollments[membership.MembershipId] = app.EnrollmentFor(membership, "tskey-auth-kTest2CNTRL-abcdef0123456789", "kTest2CNTRL");

            Assert.Equal(EnrollmentOutcome.Completed, (await restarted.TryCompleteAsync(membership, CancellationToken.None)).Outcome);
            using (var document = JsonDocument.Parse(File.ReadAllText(path)))
            {
                Assert.Empty(document.RootElement.GetProperty("memberships").EnumerateArray());
            }
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task A_blob_for_another_device_fails_without_reaching_the_transport()
    {
        using var app = new AppHarness();
        using var other = new AppHarness();
        var membership = app.AddMembership(MembershipState.Approved);
        var foreign = other.EnrollmentFor(membership with { OwnerId = other.OwnerId }, AuthKey, "kTest1CNTRL");
        app.Broker.Enrollments[membership.MembershipId] = foreign with { OwnerId = membership.OwnerId };
        app.Main.ShowServers();
        var servers = Assert.IsType<ServersViewModel>(app.Main.CurrentPage);

        await servers.RefreshAsync(CancellationToken.None);

        Assert.Empty(app.Transport.Enrollments);
        Assert.Empty(app.Broker.Bound);
        Assert.True(Assert.Single(servers.Servers).HasProblem);
    }

    [Fact]
    public async Task A_second_server_of_the_same_owner_reuses_the_owner_node()
    {
        using var app = new AppHarness();
        app.Transport.Nodes.Add(new Transport.TransportNode(app.OwnerId, "nEXISTING1CNTRL", "running"));
        app.AddMembership(MembershipState.Approved, "nEXISTING1CNTRL", idLetter: 'a', MembershipNodeState.Confirmed);
        var membership = app.AddMembership(MembershipState.Approved, idLetter: 'b');
        app.Main.ShowServers();
        var servers = Assert.IsType<ServersViewModel>(app.Main.CurrentPage);

        await servers.RefreshAsync(CancellationToken.None);

        Assert.Empty(app.Transport.Enrollments);
        Assert.Equal(0, app.Broker.TakeEnrollmentCalls);
        Assert.Equal((membership.MembershipId, "nEXISTING1CNTRL"), Assert.Single(app.Broker.Bound));
        Assert.Empty(app.Transport.Forgotten);
    }

    [Fact]
    public async Task A_revoked_owners_stale_node_is_forgotten_before_fresh_enrollment()
    {
        using var app = new AppHarness();
        app.Transport.Nodes.Add(new Transport.TransportNode(app.OwnerId, "nSTALE1CNTRL", "running"));
        var membership = app.AddMembership(MembershipState.Approved);
        app.Broker.Enrollments[membership.MembershipId] = app.EnrollmentFor(membership, AuthKey, "kTest1CNTRL");
        var coordinator = Coordinator(app, consumedPath: null);

        var result = await coordinator.TryCompleteAsync(membership, app.Broker.Memberships, CancellationToken.None);

        Assert.Equal(EnrollmentOutcome.Completed, result.Outcome);
        Assert.Equal([app.OwnerId], app.Transport.Forgotten);
        Assert.Equal(app.Transport.NextNodeId, result.NodeId);
        Assert.Equal((membership.MembershipId, app.Transport.NextNodeId), Assert.Single(app.Broker.Bound));
    }

    [Fact]
    public async Task An_ambiguous_forget_never_rebinds_the_stale_node_even_after_restart()
    {
        var directory = Path.Combine(Path.GetTempPath(), "1salem-connect-app-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, ConsumedEnrollments.FileName);
        try
        {
            using var app = new AppHarness();
            app.Transport.Nodes.Add(new Transport.TransportNode(app.OwnerId, "nSTALE1CNTRL", "running"));
            var membership = app.AddMembership(MembershipState.Approved);
            app.Broker.Enrollments[membership.MembershipId] = app.EnrollmentFor(membership, AuthKey, "kTest1CNTRL");
            app.Transport.NextForgetFailure = new TransportException(TransportErrorCodes.NoAnswer);

            await Assert.ThrowsAsync<TransportException>(() =>
                Coordinator(app, path).TryCompleteAsync(membership, CancellationToken.None));
            var afterRestart = await Coordinator(app, path).TryCompleteAsync(membership, CancellationToken.None);

            Assert.Equal(EnrollmentOutcome.Failed, afterRestart.Outcome);
            Assert.Empty(app.Broker.Bound);
            Assert.Empty(app.Transport.Enrollments);
            Assert.Equal([app.OwnerId], app.Transport.Forgotten);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Build7_consumed_enrollment_array_remains_readable()
    {
        var directory = Path.Combine(Path.GetTempPath(), "1salem-connect-app-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, ConsumedEnrollments.FileName);
        try
        {
            Directory.CreateDirectory(directory);
            var membershipId = TestIds.MembershipId();
            File.WriteAllText(path, JsonSerializer.Serialize(new[] { membershipId }));
            using var app = new AppHarness();
            var consumed = new ConsumedEnrollments(path, app.Log);

            Assert.True(consumed.Contains(membershipId));
            Assert.True(consumed.CanReuseNode(membershipId));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(MembershipNodeState.Candidate, "Waiting for the owner to finish setting up this PC")]
    [InlineData(MembershipNodeState.Rejected, "Setting up this PC failed — ask the owner to invite you again")]
    public async Task Candidate_and_rejected_nodes_are_never_offered_as_connectable(
        MembershipNodeState nodeState,
        string expectedStatus)
    {
        using var app = new AppHarness();
        app.AddMembership(MembershipState.Approved, "nFAKE1CNTRL", nodeState: nodeState);
        app.Main.ShowServers();
        var servers = Assert.IsType<ServersViewModel>(app.Main.CurrentPage);

        await servers.RefreshAsync(CancellationToken.None);

        var item = Assert.Single(servers.Servers);
        Assert.Equal(expectedStatus, item.StatusText);
        Assert.False(item.CanOpen);
        Assert.False(item.Membership.CanConnect);
        app.Main.ShowConnection(item.Membership);
        var connection = Assert.IsType<ConnectionViewModel>(app.Main.CurrentPage);
        Assert.False(connection.CanConnect);
        await connection.ConnectAsync();
        Assert.Empty(app.Broker.SessionSpkis);
    }

    /// <summary>A coordinator of its own, as a fresh run of the app would build one.</summary>
    private static EnrollmentCoordinator Coordinator(AppHarness app, string? consumedPath) =>
        new(app.Broker, app.Transport, app.Process, app.Identity, new ConsumedEnrollments(consumedPath, app.Log), app.Log);
}
