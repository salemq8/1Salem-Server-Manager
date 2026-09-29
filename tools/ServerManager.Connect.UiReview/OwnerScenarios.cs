using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ServerManager.Client;
using ServerManager.Client.Controls;
using ServerManager.Client.Shell;
using ServerManager.Contracts;
using ServerManager.Core;

namespace ServerManager.Connect.UiReview;

internal static class OwnerScenarios
{
    private static readonly Guid ServerId = Guid.Parse("5b0f7f2e-3c1a-4d57-9a7e-2f1d8c0b6a41");

    public static async Task RunAsync(FakeAgent agent, Report report)
    {
        var now = DateTimeOffset.UtcNow;
        report.Note("owner scenarios start in UI culture " + System.Globalization.CultureInfo.CurrentUICulture.Name);

        agent.Status = Status(ConnectSetupState.NotSetUp, credential: false, ConnectPolicyState.Unknown, ConnectHostNodeState.NotEnrolled, now);
        await CaptureWindowAsync(report, "01-setup-not-set-up", new ConnectSetupWindow());
        agent.Status = Status(ConnectSetupState.Ready, credential: true, ConnectPolicyState.Safe, ConnectHostNodeState.Enrolled, now);
        await CaptureWindowAsync(report, "02-setup-ready", new ConnectSetupWindow());
        agent.Status = Status(ConnectSetupState.NeedsAttention, credential: true, ConnectPolicyState.Unsafe, ConnectHostNodeState.Enrolled, now) with
        {
            PolicyReasons = ["A rule lets tag:onesalem-client devices reach ports other than tcp:7780 on the host."],
            ErrorCode = ConnectErrorCodes.PolicyUnsafe
        };
        await CaptureWindowAsync(report, "03-setup-policy-unsafe", new ConnectSetupWindow());

        // Minecraft Connect card in Server Detail > Settings > Network.
        DashboardFeed.Shared.Servers.Add(new ServerCardViewModel(MinecraftCard(now), now));
        ServerDetailContext.Shared.Select(ServerId);
        agent.Server = Server(enabled: false, []);
        await CaptureSettingsAsync(report, "04-minecraft-connect-off");
        agent.Server = Server(enabled: true, [Friend('p', ConnectFriendState.Pending, ConnectFriendSetupState.None, null, now)]);
        await CaptureSettingsAsync(report, "05-minecraft-connect-on-pending-friend");

        // Invite: create, then copy through the real Windows clipboard.
        var secret = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        agent.Invite = new ConnectInviteCreated("inv_" + new string('r', 26), "https://onesalem-connect-broker-production.onesalemconnect.workers.dev/i#" + secret, secret, now.AddHours(24));
        var invite = new ConnectInviteWindow(ServerId);
        Report.Show(invite);
        await report.CaptureAsync("06-invite-before-create", invite);
        Click((Button)invite.FindName("CreateButton"));
        await report.CaptureAsync("07-invite-created", invite);
        CheckInviteClipboard(report, invite, agent.Invite.Link);
        invite.Close();

        // Friends: one pending, one ready, one the friend has not set up yet.
        agent.Server = Server(enabled: true,
        [
            Friend('p', ConnectFriendState.Pending, ConnectFriendSetupState.None, null, now),
            Friend('r', ConnectFriendState.Approved, ConnectFriendSetupState.Ready, "Omar", now),
            Friend('w', ConnectFriendState.Approved, ConnectFriendSetupState.WaitingForFriend, null, now)
        ]);
        var friends = new ConnectFriendsWindow(ServerId);
        Report.Show(friends);
        await report.CaptureAsync("08-friends-pending-and-approved", friends);

        // Revoke: the click opens a modal confirmation, so it is posted rather than awaited here.
        agent.Revoke = new ConnectRevokeResult(true, ConnectStepOutcome.Done, ConnectStepOutcome.Done, ConnectStepOutcome.Done, null);
        var revoke = Report.Descendants(friends).OfType<Button>()
            .First(button => Equals(button.Content, LocalizationService.Get("Connect.Friends.Revoke")));
        _ = friends.Dispatcher.BeginInvoke(() => Click(revoke));
        await Report.SettleAsync();
        var dialog = Application.Current.Windows.OfType<Window>().Single(window => window is ConfirmationDialog);
        await report.CaptureAsync("09-revoke-confirmation", dialog);
        dialog.DialogResult = true;
        agent.Server = Server(enabled: true, [Friend('p', ConnectFriendState.Pending, ConnectFriendSetupState.None, null, now),
            Friend('w', ConnectFriendState.Approved, ConnectFriendSetupState.WaitingForFriend, null, now)]);
        await report.CaptureAsync("10-revoke-complete", friends);
        friends.Close();

        report.Check(agent.Requests.All(request => !request.Contains("-> 404", StringComparison.Ordinal) ||
                                                  !request.Contains("/connect", StringComparison.Ordinal)),
            "every Connect request of the reviewed screens was answered");
    }

    private static async Task CaptureWindowAsync(Report report, string name, Window window)
    {
        Report.Show(window);
        await report.CaptureAsync(name, window);
        window.Close();
    }

    private static async Task CaptureSettingsAsync(Report report, string name)
    {
        var tab = new ServerSettingsTab();
        var host = new Window { Width = 1180, Height = 860, Content = tab, FlowDirection = LayoutDirectionService.ForCulture(System.Globalization.CultureInfo.CurrentUICulture) };
        Report.Show(host);
        tab.SetActive(true);
        var groups = (ListBox)tab.FindName("GroupList");
        groups.SelectedItem = groups.Items.OfType<ListBoxItem>().Single(item => Equals(item.Tag, "Network"));
        await Report.SettleAsync(1500);
        await report.CaptureAsync(name, host);
        tab.SetActive(false);
        host.Close();
    }

    private static void CheckInviteClipboard(Report report, Window invite, string link)
    {
        // Put back whatever text the owner had on the clipboard after the check.
        var previous = Clipboard.ContainsText() ? Clipboard.GetText() : null;
        try
        {
            var shown = ((TextBox)invite.FindName("LinkBox")).Text;
            Click((Button)invite.FindName("CopyLinkButton"));
            var data = Clipboard.GetDataObject();
            var text = data?.GetData(DataFormats.UnicodeText) as string;
            var status = ((TextBlock)invite.FindName("StatusText")).Text;
            report.Note($"clipboard: shown link {shown.Length} chars (matches invite: {shown == link}); read {text?.Length ?? -1} chars; " +
                        $"formats [{string.Join(", ", data?.GetFormats(false) ?? [])}]; status '{status}'");
            report.Check(text == link, "invite link copied to the Windows clipboard exactly");
            report.Check(IsZero(data, "CanIncludeInClipboardHistory") && IsZero(data, "CanUploadToCloudClipboard"),
                "invite link is excluded from clipboard history and cloud clipboard");
        }
        finally
        {
            if (previous is not null) Clipboard.SetText(previous);
            else Clipboard.Clear();
        }
    }

    private static bool IsZero(IDataObject? data, string format)
    {
        var value = data?.GetDataPresent(format) == true ? data.GetData(format) : null;
        var bytes = value switch { MemoryStream stream => stream.ToArray(), byte[] raw => raw, _ => null };
        return bytes is { Length: >= 4 } && bytes.Take(4).All(item => item == 0);
    }

    private static void Click(ButtonBase button) =>
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));

    private static ConnectStatusResponse Status(ConnectSetupState state, bool credential, ConnectPolicyState policy, ConnectHostNodeState host, DateTimeOffset now) =>
        new(state, credential, credential ? "kQx7…3F" : null, policy, [], host,
            host == ConnectHostNodeState.Enrolled ? "nH0stR3v1ewCNTRL" : null,
            host == ConnectHostNodeState.Enrolled ? "100.101.102.103:7780" : null,
            state == ConnectSetupState.Ready, state == ConnectSetupState.Ready, 0, credential,
            credential ? "own_" + new string('q', 26) : null, credential ? now : null, null);

    private static ServerConnectResponse Server(bool enabled, IReadOnlyList<ConnectFriendItem> friends) =>
        new(ServerId, AccountReady: true, Eligible: true, Issues: [], Enabled: enabled, Invites: [], Friends: friends);

    private static ConnectFriendItem Friend(char letter, ConnectFriendState state, ConnectFriendSetupState setup, string? nickname, DateTimeOffset now) =>
        new("mem_" + new string(letter, 26), ServerId, "dev_" + new string(letter, 26), nickname, state, setup,
            now.AddHours(-3), state == ConnectFriendState.Approved ? now.AddHours(-2) : null);

    private static ServerDashboardCard MinecraftCard(DateTimeOffset now) => new(
        ServerId,
        GameType.Minecraft,
        true,
        "Survival world",
        ServerState.Running,
        "192.168.3.66:25565",
        "1.21.8",
        "Paper",
        2,
        20,
        14620,
        12.4,
        2_402L * 1024 * 1024,
        2_116L * 1024 * 1024,
        4_096L * 1024 * 1024,
        TimeSpan.FromHours(5.5),
        now.AddDays(-12),
        "Current",
        25565,
        null,
        null,
        null,
        null,
        "Server ready",
        null,
        new ServerActionAvailability(false, true, true, true, true, true, true, true),
        Priority: ProcessPriorityClass.Normal,
        GameProcessId: 14704,
        ChildProcessCount: 1,
        RootExecutableName: "java",
        GameExecutableName: "java",
        InternetAddress: null,
        PlayitState: null,
        PublicTunnelOnline: false,
        PublicTunnelVerified: false,
        PalworldManagement: null,
        LocalPortOpen: true,
        PlayitOnline: false,
        RestManagementConnected: false,
        ThreadCount: 64);
}
