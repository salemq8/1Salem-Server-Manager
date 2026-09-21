using System.ComponentModel;
using System.Net.Http;
using ServerManager.Contracts;

namespace ServerManager.Client.Controls;

/// <summary>
/// Which server Server Detail is showing, and the live card behind it. The five tabs bind to
/// this rather than each re-reading the dashboard, so they cannot disagree about the server's
/// state and only one poll feeds them all.
/// </summary>
public sealed class ServerDetailContext : INotifyPropertyChanged
{
    private static readonly Lazy<ServerDetailContext> SharedInstance =
        new(() => new ServerDetailContext());

    private readonly DashboardFeed _feed = DashboardFeed.Shared;
    private ServerCardViewModel? _card;

    private ServerDetailContext()
    {
        _feed.PropertyChanged += (_, _) => Resolve();
        _feed.Servers.CollectionChanged += (_, _) => Resolve();
    }

    public static ServerDetailContext Shared => SharedInstance.Value;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised whenever the selected server, or its data, changes.</summary>
    public event EventHandler? Changed;

    public Guid ServerId { get; private set; }

    public ServerCardViewModel? Card
    {
        get => _card;
        private set
        {
            if (ReferenceEquals(_card, value))
            {
                return;
            }

            _card = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Card)));
        }
    }

    /// <summary>The agent's own record for this server, or null when it is not in the feed.</summary>
    public ServerDashboardCard? Source => Card?.Source;

    /// <summary>
    /// What the agent says may be done right now. Defaults to everything disabled so a
    /// control is never enabled merely because the data has not arrived yet.
    /// </summary>
    public ServerActionAvailability Actions =>
        Source?.Actions ?? new ServerActionAvailability(
            CanStart: false,
            CanStop: false,
            CanRestart: false,
            CanForceStop: false,
            CanBackup: false,
            CanRestore: false,
            CanUpdate: false,
            CanSendCommand: false);

    public HttpClient CreateClient(TimeSpan timeout) =>
        AgentTransportDefaults.CreateLoopbackHttpClient(timeout);

    public void Select(Guid serverId)
    {
        ServerId = serverId;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ServerId)));
        Resolve();
    }

    private void Resolve()
    {
        Card = _feed.Servers.FirstOrDefault(server => server.ServerId == ServerId);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
