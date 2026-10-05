using System.Globalization;
using ServerManager.Client;
using ServerManager.Client.Controls;
using ServerManager.Client.Shell;
using ServerManager.Contracts;
using ServerManager.Core;

namespace ServerManager.Client.Tests;

public sealed class ConnectOwnerUiTests
{
    private static readonly string[] ConnectKeys =
    [
        "Network.ConnectTitle",
        "Network.ConnectManage",
        "Connect.State.NotSetUp",
        "Connect.State.Checking",
        "Connect.State.Policy",
        "Connect.State.PolicyIncomplete",
        "Connect.State.PolicyNotPermitted",
        "Connect.State.PolicyUnverifiable",
        "Connect.State.TailnetLock",
        "Connect.State.HostStarting",
        "Connect.State.Ready",
        "Connect.State.Error",
        "Connect.Setup.Title",
        "Connect.Setup.Intro",
        "Connect.Setup.Privacy",
        "Connect.Setup.ClientId",
        "Connect.Setup.ClientSecret",
        "Connect.Server.NotAvailable",
        "Connect.Server.NotEligible",
        "Connect.Eligibility.PortTooLow",
        "Connect.Eligibility.SensitivePort",
        "Connect.Eligibility.AgentPort",
        "Connect.Eligibility.SharedPort",
        "Connect.Eligibility.PreventProxyConnections",
        "Connect.Invite.Title",
        "Connect.Invite.Validity",
        "Connect.Invite.ShownOnce",
        "Connect.Invite.CopyLink",
        "Connect.Invite.CopyCode",
        "Connect.Friends.Title",
        "Connect.Friends.Pending",
        "Connect.Friends.Approved",
        "Connect.Friends.Waiting",
        "Connect.Friends.Checking",
        "Connect.Friends.Ready",
        "Connect.Friends.Failed",
        "Connect.Friends.Revoke",
        "Connect.Revoke.Broker",
        "Connect.Revoke.ThisPc",
        "Connect.Revoke.Tailnet",
        "Connect.State.ErrorDetailWith",
        "Connect.Check.TimedOut",
        "Connect.Check.ServiceUnreachable",
        "ServerSettings.ConnectRetry"
    ];

    [Fact]
    public void FailedServerCheck_SaysWhatWentWrong()
    {
        WithCulture("en-US", () =>
        {
            Assert.Equal(
                "The last Connect check did not finish: The process cannot access the file.",
                ConnectPresentation.CheckFailed(new ServerManager.Client.Transport.ConnectOwnerClientException(
                    "FileOrPortConflict", "The process cannot access the file.")));
            Assert.Contains("not answering", ConnectPresentation.CheckFailed(new System.Net.Http.HttpRequestException("refused")), StringComparison.Ordinal);
            Assert.Contains("30 seconds", ConnectPresentation.CheckFailed(new TaskCanceledException()), StringComparison.Ordinal);
            Assert.Equal("The last Connect check did not finish.", ConnectPresentation.CheckFailed(new InvalidOperationException("x")));
        });
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("ar-SA")]
    public void EveryOwnerUiString_ResolvesInBothLanguages(string culture)
    {
        WithCulture(culture, () =>
        {
            foreach (var key in ConnectKeys)
            {
                Assert.True(LocalizationService.HasKey(key), $"{key} has no {culture} text.");
                Assert.NotEqual(key, LocalizationService.Get(key));
            }
        });
    }

    [Fact]
    public void SetupPresentation_CoversEveryRequiredState()
    {
        WithCulture("en-US", () =>
        {
            Assert.Equal("Checking", ConnectPresentation.Setup(null).State);
            Assert.Equal("Not set up", ConnectPresentation.Setup(Status()).State);
            Assert.Equal("Host starting", ConnectPresentation.Setup(Status(
                state: ConnectSetupState.Starting,
                credentialStored: true,
                hostNode: ConnectHostNodeState.Enrolling)).State);
            Assert.Equal("Policy needs changes", ConnectPresentation.Setup(Status(
                state: ConnectSetupState.NeedsAttention,
                credentialStored: true,
                policy: ConnectPolicyState.Unsafe)).State);
            Assert.Equal("Tailnet Lock is not supported", ConnectPresentation.Setup(Status(
                state: ConnectSetupState.NeedsAttention,
                credentialStored: true,
                hostNode: ConnectHostNodeState.TailnetLockUnsupported)).State);
            Assert.Equal("Ready", ConnectPresentation.Setup(Status(
                state: ConnectSetupState.Ready,
                credentialStored: true,
                policy: ConnectPolicyState.Safe,
                hostNode: ConnectHostNodeState.Enrolled)).State);
            Assert.Equal("Needs attention", ConnectPresentation.Setup(Status(
                state: ConnectSetupState.Error,
                credentialStored: true)).State);
        });
    }

    [Fact]
    public void ServerPresentation_IsHonestAboutGameEligibilityAndActions()
    {
        WithCulture("en-US", () =>
        {
            var palworld = ConnectPresentation.Server(GameType.Palworld, null);
            Assert.Equal(ConnectServerAction.None, palworld.PrimaryAction);
            Assert.Contains("Not available", palworld.State, StringComparison.Ordinal);

            var ineligible = ConnectPresentation.Server(GameType.Minecraft, Server(
                eligible: false,
                issues: [ConnectEligibilityIssue.PortTooLow, ConnectEligibilityIssue.PreventProxyConnections]));
            Assert.False(ineligible.CanRunPrimaryAction);
            Assert.Contains("below 1024", ineligible.Detail, StringComparison.Ordinal);
            Assert.Contains("prevent-proxy-connections=false", ineligible.Detail, StringComparison.Ordinal);

            var disabled = ConnectPresentation.Server(GameType.Minecraft, Server());
            Assert.Equal(ConnectServerAction.Enable, disabled.PrimaryAction);
            Assert.False(disabled.CanInvite);

            var enabled = ConnectPresentation.Server(GameType.Minecraft, Server(
                enabled: true,
                friends: [Friend(ConnectFriendState.Pending), Friend(ConnectFriendState.Approved)]));
            Assert.Equal(ConnectServerAction.Disable, enabled.PrimaryAction);
            Assert.True(enabled.CanInvite);
            Assert.True(enabled.CanManageFriends);
            Assert.Equal(1, enabled.PendingCount);
        });
    }

    [Fact]
    public void FriendPresentation_ExposesApprovalAndAllSetupStates()
    {
        WithCulture("en-US", () =>
        {
            var pending = ConnectPresentation.Friend(Friend(ConnectFriendState.Pending));
            Assert.True(pending.IsPending);
            Assert.False(pending.IsApproved);

            foreach (var expected in new[]
                     {
                         (ConnectFriendSetupState.WaitingForFriend, "Waiting for the friend"),
                         (ConnectFriendSetupState.Checking, "Checking this PC"),
                         (ConnectFriendSetupState.Ready, "Ready"),
                         (ConnectFriendSetupState.Failed, "Setup failed")
                     })
            {
                var view = ConnectPresentation.Friend(Friend(
                    ConnectFriendState.Approved,
                    expected.Item1));
                Assert.Equal(expected.Item2, view.Setup);
                Assert.True(view.IsApproved);
            }
        });
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void UnreadyOrIneligibleServer_KeepsCleanupActions(bool accountReady, bool eligible)
    {
        var response = Server(eligible: eligible, enabled: true,
            friends: [Friend(ConnectFriendState.Approved)]) with
        { AccountReady = accountReady };
        var view = ConnectPresentation.Server(GameType.Minecraft, response);
        Assert.Equal(ConnectServerAction.Disable, view.PrimaryAction);
        Assert.True(view.CanRunPrimaryAction);
        Assert.True(view.CanManageFriends);
        Assert.False(view.CanInvite);

        var disabled = ConnectPresentation.Server(GameType.Minecraft, response with { Enabled = false });
        Assert.False(disabled.CanRunPrimaryAction);
        Assert.True(disabled.CanManageFriends);
    }

    [Theory]
    [InlineData(ConnectPolicyState.Unsafe)]
    [InlineData(ConnectPolicyState.Incomplete)]
    [InlineData(ConnectPolicyState.NotPermitted)]
    [InlineData(ConnectPolicyState.Unverifiable)]
    public void PolicyDetails_AreLocalizedInsteadOfShowingRawAnalyzerEnglish(ConnectPolicyState policy)
    {
        WithCulture("ar-SA", () =>
        {
            var status = Status(ConnectSetupState.NeedsAttention, true, policy) with
            {
                PolicyReasons = ["Raw English analyzer diagnostic"]
            };
            var view = ConnectPresentation.Setup(status);
            Assert.DoesNotContain("Raw English", view.Detail, StringComparison.Ordinal);
            Assert.Contains(view.Detail, character => character is >= '\u0600' and <= '\u06ff');
        });
    }

    [Fact]
    public void ServerRequestTracker_DiscardsRepliesAfterSelectionChanges()
    {
        var tracker = new ConnectServerRequestTracker();
        var firstServer = Guid.NewGuid();
        var firstRequest = tracker.Capture(firstServer);
        Assert.True(tracker.IsCurrent(firstRequest));
        Assert.False(tracker.Select(firstServer));
        Assert.True(tracker.IsCurrent(firstRequest));

        Assert.True(tracker.Select(Guid.NewGuid()));
        Assert.False(tracker.IsCurrent(firstRequest));
        var secondVisit = tracker.Capture(firstServer);
        Assert.False(tracker.IsCurrent(firstRequest));
        Assert.True(tracker.IsCurrent(secondVisit));
    }

    [Fact]
    public void NicknameDraft_SurvivesRefreshAndSaveWhileTyping()
    {
        var drafts = new ConnectNicknameDrafts();
        drafts.Set("member", "My friend");
        Assert.Equal("My friend", drafts.Get("member", "Old saved name"));
        drafts.Retain(["member"]);
        Assert.Equal("My friend", drafts.Get("member", "Server refresh"));

        drafts.Set("member", "My friend updated");
        drafts.Saved("member", "My friend");
        Assert.Equal("My friend updated", drafts.Get("member", "My friend"));
        drafts.Saved("member", "My friend updated");
        Assert.Equal("New server value", drafts.Get("member", "New server value"));

        drafts.Set("removed", "Unsaved name");
        drafts.Retain(["member"]);
        Assert.Equal("Fallback", drafts.Get("removed", "Fallback"));
    }

    [Theory]
    [InlineData("tskey-client-a1b2c3d4", "tskey-client-[REDACTED]")]
    [InlineData("{\"clientSecret\":\"owner-secret\"}", "{\"clientSecret\":\"[REDACTED]\"}")]
    [InlineData("https://onesalem-connect-broker-production.onesalemconnect.workers.dev/i#invite_secret_123", "https://onesalem-connect-broker-production.onesalemconnect.workers.dev/i#[REDACTED]")]
    public void Diagnostics_ChainsConnectSecretRedactor(string input, string expected) =>
        Assert.Equal(expected, DiagnosticsService.Redact(input));

    [Fact]
    public void SetupAndInviteWindows_KeepSecretsOutOfOrdinaryControlsAndClipboardHistory()
    {
        var setup = ReadSource("src", "ServerManager.Client", "ConnectSetupWindow.xaml");
        var setupCode = ReadSource("src", "ServerManager.Client", "ConnectSetupWindow.xaml.cs");
        var invite = ReadSource("src", "ServerManager.Client", "ConnectInviteWindow.xaml");
        var inviteCode = ReadSource("src", "ServerManager.Client", "ConnectInviteWindow.xaml.cs");
        var clipboard = ReadSource("src", "ServerManager.Client", "Shell", "SafeClipboard.cs");

        Assert.Contains("<PasswordBox x:Name=\"ClientSecretBox\"", setup, StringComparison.Ordinal);
        Assert.Contains("ClientSecretBox.Clear();", setupCode, StringComparison.Ordinal);
        Assert.DoesNotContain("ClientIdBox.Text = _status.ClientIdHint", setupCode, StringComparison.Ordinal);
        Assert.DoesNotContain("ClientIdBox.Text = _status?.ClientIdHint", setupCode, StringComparison.Ordinal);
        Assert.Contains("FlowDirection=\"LeftToRight\"", invite, StringComparison.Ordinal);
        Assert.Contains("TrySetSensitiveText", inviteCode, StringComparison.Ordinal);
        Assert.Contains("AddCopyingHandler(LinkBox, SensitiveText_Copying)", inviteCode, StringComparison.Ordinal);
        Assert.Contains("AddCopyingHandler(CodeBox, SensitiveText_Copying)", inviteCode, StringComparison.Ordinal);
        Assert.Contains("e.CancelCommand();", inviteCode, StringComparison.Ordinal);
        Assert.Contains("CanIncludeInClipboardHistory", clipboard, StringComparison.Ordinal);
        Assert.Contains("CanUploadToCloudClipboard", clipboard, StringComparison.Ordinal);
        Assert.Contains("new byte[sizeof(uint)]", clipboard, StringComparison.Ordinal);
        Assert.Contains("autoConvert: false", clipboard, StringComparison.Ordinal);
    }

    [Fact]
    public void ConnectPolling_IsLimitedToVisibleSurfaces()
    {
        var settings = ReadSource("src", "ServerManager.Client", "Controls", "ServerSettingsTab.xaml.cs");
        var setup = ReadSource("src", "ServerManager.Client", "ConnectSetupWindow.xaml.cs");
        var friends = ReadSource("src", "ServerManager.Client", "ConnectFriendsWindow.xaml.cs");
        var network = ReadSource("src", "ServerManager.Client", "Controls", "NetworkPageControl.xaml.cs");

        Assert.Contains("!IsVisible || CurrentGroup != \"Network\"", settings, StringComparison.Ordinal);
        Assert.Contains("_connectTimer.Stop();", settings, StringComparison.Ordinal);
        Assert.Contains("_loading", setup, StringComparison.Ordinal);
        Assert.Contains("!IsVisible", setup, StringComparison.Ordinal);
        Assert.Contains("_timer.Stop();", setup, StringComparison.Ordinal);
        Assert.Contains("_busy || !IsVisible", friends, StringComparison.Ordinal);
        Assert.Contains("_timer.Stop();", friends, StringComparison.Ordinal);
        Assert.Contains("!IsVisible", network, StringComparison.Ordinal);
        Assert.Contains("IsVisibleChanged", network, StringComparison.Ordinal);
        Assert.Contains("_timer.Stop();", network, StringComparison.Ordinal);
    }

    [Fact]
    public void NetworkAndServerCards_AreWiredToRealOwnerActions()
    {
        var network = ReadSource("src", "ServerManager.Client", "Controls", "NetworkPageControl.xaml");
        var server = ReadSource("src", "ServerManager.Client", "Controls", "ServerSettingsTab.xaml");
        var code = ReadSource("src", "ServerManager.Client", "Controls", "ServerSettingsTab.xaml.cs");

        Assert.Contains("ConnectOwner_Click", network, StringComparison.Ordinal);
        Assert.Contains("ConnectEnable_Click", server, StringComparison.Ordinal);
        Assert.Contains("ConnectInvite_Click", server, StringComparison.Ordinal);
        Assert.Contains("ConnectFriends_Click", server, StringComparison.Ordinal);
        Assert.Contains("ConnectPresentation.Server", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Development preview", server, StringComparison.OrdinalIgnoreCase);
    }

    private static ConnectStatusResponse Status(
        ConnectSetupState state = ConnectSetupState.NotSetUp,
        bool credentialStored = false,
        ConnectPolicyState policy = ConnectPolicyState.Unknown,
        ConnectHostNodeState hostNode = ConnectHostNodeState.NotEnrolled) => new(
        state,
        credentialStored,
        null,
        policy,
        [],
        hostNode,
        null,
        null,
        false,
        false,
        0,
        false,
        null,
        null,
        state == ConnectSetupState.Error ? "ConnectBrokerUnavailable" : null);

    private static ServerConnectResponse Server(
        bool eligible = true,
        bool enabled = false,
        IReadOnlyList<ConnectEligibilityIssue>? issues = null,
        IReadOnlyList<ConnectFriendItem>? friends = null) => new(
        Guid.NewGuid(),
        true,
        eligible,
        issues ?? [],
        enabled,
        [],
        friends ?? []);

    private static ConnectFriendItem Friend(
        ConnectFriendState state,
        ConnectFriendSetupState setup = ConnectFriendSetupState.None) => new(
        Guid.NewGuid().ToString("N"),
        Guid.NewGuid(),
        "node-123",
        "Friend",
        state,
        setup,
        DateTimeOffset.UtcNow,
        state == ConnectFriendState.Approved ? DateTimeOffset.UtcNow : null);

    private static void WithCulture(string culture, Action action)
    {
        var original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            action();
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    private static string ReadSource(params string[] parts)
    {
        var root = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Directory.Build.props")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine([root!.FullName, .. parts]));
    }
}
