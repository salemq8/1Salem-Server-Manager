using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using System.Windows.Threading;
using ServerManager.Client.Shell;
using ServerManager.Contracts;
using ServerManager.Core;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using UserControl = System.Windows.Controls.UserControl;

namespace ServerManager.Client.Controls;

/// <summary>
/// "Can people reach my server, and what address do I give them?" — answered without naming a
/// tunnel process, a port or an identity. Those live under Advanced.
///
/// The provider is read from the agent rather than assumed, so when a second remote-access
/// provider exists this page shows it by name with no layout change. Nothing here advertises
/// a provider that is not installed.
/// </summary>
public partial class NetworkPageControl : UserControl
{
    private const string EmDash = "—";
    private readonly DashboardFeed _feed = DashboardFeed.Shared;
    private readonly HttpClient _httpClient =
        AgentTransportDefaults.CreateLoopbackHttpClient(TimeSpan.FromSeconds(20));
    private readonly DispatcherTimer _timer;
    private PlayitStatusResponse? _playit;
    private NetworkSnapshot? _network;

    public NetworkPageControl()
    {
        InitializeComponent();
        _feed.PropertyChanged += (_, _) => Dispatcher.Invoke(Render);
        LocalizationService.LanguageChanged += (_, _) => Dispatcher.Invoke(Render);
        ThemeService.ThemeChanged += (_, _) => Dispatcher.Invoke(Render);
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(8)
        };
        _timer.Tick += async (_, _) => await LoadAsync();
        Loaded += OnLoaded;
        Unloaded += (_, _) => _timer.Stop();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _feed.Start();
        await _feed.RefreshAsync();
        _timer.Start();
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            _playit = await _httpClient.GetFromJsonAsync<PlayitStatusResponse>("/api/v1/playit");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _playit = null;
        }

        try
        {
            _network = await _httpClient.GetFromJsonAsync<NetworkSnapshot>("/api/v1/network");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _network = null;
        }

        Render();
    }

    private void Render()
    {
        if (AddressLabel is null)
        {
            return;
        }

        AddressLabel.Text = LocalizationService.Get("Network.PublicAddress");
        DestinationLabel.Text = LocalizationService.Get("Network.Destination");
        ProviderLabel.Text = LocalizationService.Get("Network.Provider");
        LocalHeading.Text = LocalizationService.Get("Network.LocalNetwork");
        CopyAddressButton.Content = LocalizationService.Get("Action.CopyAddress");
        AdvancedSection.Header = LocalizationService.Get("Advanced.Title");
        PairDeviceButton.Content = LocalizationService.Get("Network.PairDevice");
        NetworkDetailsButton.Content = LocalizationService.Get("Network.Details");

        var stateShown = StateView.Apply(_feed, "Empty.NoServers", "Empty.NoServersMessage");
        ContentRoot.Visibility = stateShown ? Visibility.Collapsed : Visibility.Visible;
        if (stateShown)
        {
            return;
        }

        // The server whose tunnel is actually published, else the first one.
        var server = _feed.Servers.FirstOrDefault(item => item.RemoteAccessOnline)
            ?? _feed.Servers.FirstOrDefault();
        var address = server?.InternetAddress;
        var configured = !string.IsNullOrWhiteSpace(address);
        var online = server?.RemoteAccessOnline == true;

        var health = ServerPresentation.DescribeRemoteAccess(online, address);
        HealthText.Text = health.Label;
        HealthDot.Fill = ToneBrush(health.Tone);
        HealthDetail.Text = BuildDetail(configured, online);

        AddressValue.Text = configured ? address! : EmDash;
        CopyAddressButton.IsEnabled = configured;
        DestinationValue.Text = server?.Name ?? EmDash;
        ProviderValue.Text = DescribeProvider();
        LocalValue.Text = string.IsNullOrWhiteSpace(server?.Source?.LocalAddress)
            ? _network?.LocalIpv4 ?? EmDash
            : server!.Source!.LocalAddress!;

        AdvancedDetails.Text = BuildAdvanced(server);
    }

    private string BuildDetail(bool configured, bool online)
    {
        if (!configured)
        {
            return LocalizationService.Get("Network.LocalOnlyDetail");
        }

        return online
            ? LocalizationService.Get("Network.ReachableDetail")
            : LocalizationService.Get("Network.UnreachableDetail");
    }

    /// <summary>
    /// Named from what is actually installed. A future provider appears here by name without
    /// this page changing shape, and an absent one is never advertised.
    /// </summary>
    private string DescribeProvider()
    {
        if (_playit is null)
        {
            return LocalizationService.Get("Status.Unavailable");
        }

        return _playit.IsInstalled
            ? LocalizationService.Get("Network.ProviderPlayit")
            : LocalizationService.Get("Network.ProviderNone");
    }

    private string BuildAdvanced(ServerCardViewModel? server)
    {
        var lines = new List<string>();
        if (_playit is { } playit)
        {
            lines.Add($"Playit  {playit.State}  " +
                      $"({(playit.IsRunning ? LocalizationService.Get("Advanced.Connected") : LocalizationService.Get("Advanced.Disconnected"))})");
            if (!string.IsNullOrWhiteSpace(playit.Version))
            {
                lines.Add($"{LocalizationService.Get("Metric.Version")}  {playit.Version}");
            }

            if (playit.ProcessId is { } pid)
            {
                lines.Add($"{LocalizationService.Get("Advanced.RootProcess")}  {pid.ToString(CultureInfo.InvariantCulture)}");
            }

            // Tunnel targets, not credentials: agent name, claim URL and tokens stay out.
            foreach (var tunnel in new[] { playit.Minecraft, playit.Palworld })
            {
                if (tunnel is null || !tunnel.IsConfigured)
                {
                    continue;
                }

                lines.Add($"{tunnel.Game}  {tunnel.LocalHost}:{tunnel.LocalPort}  ->  " +
                          $"{tunnel.PublicAddress ?? EmDash}  " +
                          $"({(tunnel.IsVerified ? LocalizationService.Get("Network.Verified") : LocalizationService.Get("Network.Unverified"))})");
            }
        }

        if (server?.Source is { } source)
        {
            lines.Add($"{LocalizationService.Get("Advanced.Port")}  {source.Port}  " +
                      $"({(source.LocalPortOpen ? LocalizationService.Get("Advanced.PortOpen") : LocalizationService.Get("Advanced.PortClosed"))})");
        }

        if (_network is { } network)
        {
            lines.Add($"{LocalizationService.Get("Network.Adapter")}  {network.LocalIpv4 ?? EmDash}");
        }

        return lines.Count == 0
            ? LocalizationService.Get("Status.Unavailable")
            : string.Join(Environment.NewLine, lines);
    }

    private static Brush ToneBrush(UiStatusTone tone)
    {
        var key = tone switch
        {
            UiStatusTone.Positive => "SuccessBrush",
            UiStatusTone.Caution => "WarningBrush",
            UiStatusTone.Negative => "DangerBrush",
            _ => "TextSecondaryBrush"
        };
        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    private void CopyAddress_Click(object sender, RoutedEventArgs e)
    {
        var address = AddressValue.Text;
        if (!string.IsNullOrWhiteSpace(address) &&
            !string.Equals(address, EmDash, StringComparison.Ordinal))
        {
            SafeClipboard.TrySetText(address);
        }
    }

    /// <summary>Pairing another device to this machine — the proven Build 5 flow.</summary>
    private void PairDevice_Click(object sender, RoutedEventArgs e) =>
        new LanPairingWindow { Owner = Window.GetWindow(this) }.ShowDialog();

    private void NetworkDetails_Click(object sender, RoutedEventArgs e) =>
        new NetworkStatusWindow { Owner = Window.GetWindow(this) }.ShowDialog();

    private async void State_RetryRequested(object? sender, EventArgs e)
    {
        await _feed.RefreshAsync();
        await LoadAsync();
    }
}
