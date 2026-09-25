using System.Security.Cryptography;
using ServerManager.Connect.App.Broker;
using ServerManager.Connect.App.Configuration;
using ServerManager.Connect.App.Identity;
using ServerManager.Connect.App.Localization;
using ServerManager.Connect.App.Services;
using ServerManager.Connect.App.Transport;
using ServerManager.Connect.Core.Identity;

namespace ServerManager.Connect.App.Shell;

/// <summary>
/// Builds the real services at startup and owns their lifetime. The device identity is opened
/// (or created) only when a broker is configured: an unconfigured copy touches nothing in the
/// friend's profile.
/// </summary>
public sealed class ConnectAppComposition : IDisposable
{
    private readonly List<IDisposable> _owned;

    private ConnectAppComposition(ConnectAppContext context, List<IDisposable> owned)
    {
        Context = context;
        _owned = owned;
    }

    public ConnectAppContext Context { get; }

    public static ConnectAppComposition Create()
    {
        var clock = new SystemAppClock();
        var log = new DiagnosticsLog(clock);
        var loaded = ConnectAppSettingsLoader.Load(ConnectAppSettingsLoader.DefaultPath());
        if (loaded.Problem is not null)
        {
            log.Record("settings", loaded.Problem);
        }

        var pipeName = FriendPipeName.ForCurrentUser();

        // Shared by the client that checks each connection and the process owner that tells it
        // which transport this app started. Released last: it holds the reused transports open.
        var server = TransportServerVerifier.ForTransport(loaded.Settings.TransportExecutablePath);
        var transport = PipeTransportClient.ForPipe(pipeName, server);
        var owned = new List<IDisposable> { server, transport };
        ConnectServices? services = null;
        string? problem = null;
        if (loaded.Settings.IsConfigured)
        {
            try
            {
                var identity = DeviceIdentity.LoadOrCreateDefault();
                owned.Add(identity);
                var broker = new BrokerClient(loaded.Settings.BrokerUrl!, loaded.Settings.DevelopmentMode, identity);
                owned.Add(broker);
                var process = new TransportProcess(loaded.Settings, broker, transport, TransportProcess.DefaultDataDirectory(), pipeName, server, log);
                owned.Add(process);
                services = new ConnectServices(broker, identity, process);
            }
            catch (ConnectIdentityUnavailableException exception)
            {
                // Never replaced or deleted here (§16): the friend re-pairs.
                log.Record("identity", exception);
                problem = Text.ErrorIdentityUnavailable;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException)
            {
                log.Record("identity", exception);
                problem = Text.ErrorUnexpected;
            }
        }

        var context = new ConnectAppContext
        {
            Settings = loaded.Settings,
            Services = services,
            StartupProblem = problem,

            // Nothing is read or written there until an enrollment runs, so an unconfigured copy
            // still touches nothing.
            ConsumedEnrollmentsPath = ConsumedEnrollments.DefaultPath(),
            Transport = transport,
            Clock = clock,
            Log = log,
            Clipboard = new WpfClipboard()
        };
        return new ConnectAppComposition(context, owned);
    }

    public void Dispose()
    {
        for (var index = _owned.Count - 1; index >= 0; index--)
        {
            _owned[index].Dispose();
        }
    }
}
