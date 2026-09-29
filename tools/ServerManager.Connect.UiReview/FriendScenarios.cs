using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using ServerManager.Connect.App;
using ServerManager.Connect.App.Broker;
using ServerManager.Connect.App.Configuration;
using ServerManager.Connect.App.Services;
using ServerManager.Connect.App.Shell;
using ServerManager.Connect.App.Tests.Fakes;
using ServerManager.Connect.App.Transport;
using ServerManager.Connect.App.ViewModels;
using ServerManager.Connect.Core.Crypto;
using ServerManager.Connect.Core.Enrollment;

namespace ServerManager.Connect.UiReview;

/// <summary>The real friend view models and pages, wired to the friend-app test fakes.</summary>
internal static class FriendScenarios
{
    private const string Label = "Salem's world";
    private const string ServerIdText = "5b0f7f2e-3c1a-4d57-9a7e-2f1d8c0b6a41";

    public static async Task RunAsync(Report report)
    {
        var clock = new ManualClock();
        var broker = new FakeBroker { SessionExpiresAt = clock.UtcNow.AddMinutes(10) };
        var fake = new FakeTransport();
        var transport = new GatedTransport(fake);
        using var identity = TestIds.NewDevice();
        var ownerId = TestIds.NewOwnerId();
        var context = new ConnectAppContext
        {
            Settings = new ConnectAppSettings
            {
                BrokerUrl = new Uri("https://broker.test/"),
                TransportExecutablePath = @"C:\1salem-connect-review-missing\1Salem.Connect.Transport.exe"
            },
            Services = new ConnectServices(broker, identity, new FakeTransportProcess()),
            Transport = transport,
            Clock = clock,
            Log = new DiagnosticsLog(clock),
            Clipboard = new FakeClipboard()
        };
        var main = new MainViewModel(context);
        var window = new MainWindow { DataContext = main };
        Report.Show(window);

        main.ShowWaiting(new InviteRedemption("mem_" + new string('w', 26), Label));
        await report.CaptureAsync("01-invite-accepted-waiting-for-owner", window);

        var member = new Membership("mem_" + new string('a', 26), ownerId, ServerIdText, Label, MembershipState.Approved, "nFAKE1CNTRL", MembershipNodeState.Confirmed);
        broker.Memberships.Add(member);
        main.ShowConnection(member);
        var connection = (ConnectionViewModel)main.CurrentPage!;
        transport.Hold();
        var connecting = connection.ConnectAsync();
        await report.CaptureAsync("02-connecting", window, keyboard: false);
        transport.Release();
        await connecting;
        await report.CaptureAsync("03-connected", window);
        report.Check(connection.StateText == ServerManager.Connect.App.Localization.Text.StateConnected && connection.LocalAddress == "127.0.0.1:18211", "connected page shows the loopback address");

        var clipboard = new WpfClipboard();
        var previous = Clipboard.ContainsText() ? Clipboard.GetText() : null;
        try
        {
            report.Check(clipboard.TrySetText(connection.LocalAddress!) && Clipboard.GetText() == "127.0.0.1:18211",
                "friend app copies the local address to the Windows clipboard");
        }
        finally
        {
            if (previous is not null) Clipboard.SetText(previous);
            else Clipboard.Clear();
        }

        broker.SetState(member.MembershipId, MembershipState.Revoked);
        fake.Log.Add("2026-09-29T10:00:00Z friend: session ses_test1 connection ended (512 bytes up, 2048 bytes down)");
        await (Task)typeof(ConnectionViewModel).GetMethod("CheckSessionAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(connection, [CancellationToken.None])!;
        await report.CaptureAsync("04-access-revoked", window);
        report.Check(connection.StateText == ServerManager.Connect.App.Localization.Text.StateAccessRevoked, "revocation shows Access revoked");

        // Setup failed: the one-time key was used but no device came up.
        var failed = new Membership("mem_" + new string('f', 26), ownerId, ServerIdText, "Creative world", MembershipState.Approved, null, MembershipNodeState.None);
        broker.Memberships.Add(failed);
        var envelope = EnrollmentCrypto.Encrypt(new EnrollmentSecret("tskey-auth-kReview1CNTRL-" + new string('x', 30), "kReview1CNTRL"),
            Base64Url.Decode(identity.PublicKeySpkiBase64Url), new EnrollmentBinding(failed.MembershipId, identity.DeviceId, ownerId));
        broker.Enrollments[failed.MembershipId] = new EnrollmentPackage(failed.MembershipId, ownerId, envelope.ToJson());
        fake.NextEnrollFailure = new TransportException(TransportErrorCodes.Unavailable);
        main.ShowServers();
        var servers = (ServersViewModel)main.CurrentPage!;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await Report.SettleAsync(700);
            ((ICommand)servers.RefreshCommand).Execute(null);
        }

        await report.CaptureAsync("05-servers-setup-failed", window);
        window.Close();
        main.Shutdown();
    }

    /// <summary>The fake transport, except that opening a session can be held to show "Connecting…".</summary>
    private sealed class GatedTransport(FakeTransport inner) : ITransportClient
    {
        private TaskCompletionSource _gate = CompletedGate();

        public void Hold() => _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _gate.TrySetResult();

        public Task<TransportHello> HelloAsync(CancellationToken cancellationToken) => inner.HelloAsync(cancellationToken);

        public Task<TransportStatus> StatusAsync(CancellationToken cancellationToken) => inner.StatusAsync(cancellationToken);

        public Task<string> EnrollAsync(string node, string authKey, string hostname, CancellationToken cancellationToken) =>
            inner.EnrollAsync(node, authKey, hostname, cancellationToken);

        public Task ForgetAsync(string node, CancellationToken cancellationToken) => inner.ForgetAsync(node, cancellationToken);

        public async Task<TransportOpened> OpenAsync(string node, string ticket, string sessionKey, int preferredPort, CancellationToken cancellationToken)
        {
            await _gate.Task.WaitAsync(cancellationToken);
            return await inner.OpenAsync(node, ticket, sessionKey, preferredPort, cancellationToken);
        }

        public Task RefreshAsync(string sessionId, string ticket, string sessionKey, CancellationToken cancellationToken) =>
            inner.RefreshAsync(sessionId, ticket, sessionKey, cancellationToken);

        public Task CloseAsync(string sessionId, CancellationToken cancellationToken) => inner.CloseAsync(sessionId, cancellationToken);

        public Task<TransportDiagnostics> DiagnosticsAsync(CancellationToken cancellationToken) => inner.DiagnosticsAsync(cancellationToken);

        private static TaskCompletionSource CompletedGate()
        {
            var gate = new TaskCompletionSource();
            gate.SetResult();
            return gate;
        }
    }
}
