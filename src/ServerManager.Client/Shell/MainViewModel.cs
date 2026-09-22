using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using ServerManager.Infrastructure.Transport;
using ServerManager.Contracts;

namespace ServerManager.Client.Shell;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    /// <summary>
    /// The top-level destinations, in sidebar order. Build 6 deliberately collapsed the old
    /// twelve technical sections into five task-shaped ones: the individual games live inside
    /// Servers, remote access became Network, and Updates/Resources/Files/Logs/About moved
    /// behind a server, Settings, or Advanced rather than occupying the sidebar.
    /// </summary>
    public static readonly IReadOnlyList<string> DestinationKeys =
    [
        "Home",
        "Servers",
        "Backups",
        "Network",
        "Settings"
    ];

    private static readonly IReadOnlyDictionary<string, string> DestinationGlyphs =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Home"] = "",
            ["Servers"] = "",
            ["Backups"] = "",
            ["Network"] = "",
            ["Settings"] = ""
        };

    private readonly InstalledVersionReport _installedVersions =
        InstalledVersionDetector.Detect();
    private readonly NamedPipeAgentClient _agentClient = new();
    private readonly Controls.DashboardFeed _feed = Controls.DashboardFeed.Shared;
    private readonly DispatcherTimer _timer;
    private readonly CancellationTokenSource _lifetime = new();
    private NavigationItem _selectedSection;
    private System.Windows.FlowDirection _flowDirection;
    // Before the first poll returns, nothing is known: claiming the connection has failed is
    // as wrong as claiming it succeeded, and "Agent" is internal vocabulary besides.
    private string _connectionLabel = LocalizationService.Get("Shell.Connecting");
    private string _agentMachineName = "—";
    private string _agentVersion = "—";
    private string _databaseLabel = "Unknown";
    private string _lastError = string.Empty;
    private bool _agentConnected;
    private bool _pipeAnswered;
    private int _pipeFailures;
    private bool _serverDetailOpen;
    private bool _sidebarCollapsed;
    private int _refreshing;

    public MainViewModel(ClientLaunchMode launchMode)
    {
        IsAdministrator = launchMode == ClientLaunchMode.Administrator;
        _flowDirection = LayoutDirectionService.ForCulture(CultureInfo.CurrentUICulture);
        Sections = [.. DestinationKeys.Select(CreateNavigationItem)];
        PrimarySections = [.. Sections.Where(IsPrimaryDestination)];
        _selectedSection = Sections[0];
        RefreshCommand = new AsyncRelayCommand(RefreshAllAsync);
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _timer.Tick += OnTimerTick;
        _feed.PropertyChanged += OnFeedChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<NavigationItem> Sections { get; }

    /// <summary>
    /// The destinations shown in the upper sidebar list. Settings is deliberately excluded so
    /// it can sit pinned at the bottom, which is why it is exposed separately below.
    /// </summary>
    public ObservableCollection<NavigationItem> PrimarySections { get; }

    public NavigationItem SettingsSection =>
        Sections.First(item => item.Key.Equals("Settings", StringComparison.Ordinal));

    public void SelectSection(string key)
    {
        var match = Sections.FirstOrDefault(item =>
            item.Key.Equals(key, StringComparison.Ordinal));
        if (match is not null)
        {
            SelectedSection = match;
        }
    }

    private static bool IsPrimaryDestination(NavigationItem item) =>
        !item.Key.Equals("Settings", StringComparison.Ordinal);

    public AsyncRelayCommand RefreshCommand { get; }

    public string LaunchModeLabel => LocalizationService.Get(
        IsAdministrator ? "Shell.AdministratorMode" : "Shell.NormalMode");

    public bool IsAdministrator { get; }

    public System.Windows.FlowDirection FlowDirection
    {
        get => _flowDirection;
        private set => SetField(ref _flowDirection, value);
    }

    public string ProductVersion => ProductInfo.VersionLabel;

    public string InstalledClientVersion =>
        _installedVersions.Client.Version ?? ProductInfo.Version;

    public string InstalledAgentVersion =>
        _installedVersions.Agent.Version ?? "Unavailable";

    public string InstalledUpdaterVersion =>
        _installedVersions.Updater.Version ?? "Unavailable";

    public string InstalledUpdateState =>
        $"{_installedVersions.ReleaseChannel} · {_installedVersions.OverallState}";

    public string InstalledVersionHistory =>
        $"Build: {_installedVersions.ManifestBuildRevision} · " +
        $"Previous: {BuildLabel(_installedVersions.PreviousVersion, _installedVersions.PreviousBuildRevision)} · " +
        $"Rollback: {BuildLabel(_installedVersions.RollbackVersion, _installedVersions.RollbackBuildRevision)} · " +
        $"Last update: {_installedVersions.LastSuccessfulUpdateUtc?.ToLocalTime().ToString("g") ?? "Not recorded"}";

    public NavigationItem SelectedSection
    {
        get => _selectedSection;
        set
        {
            // The primary list cannot represent Settings, so when Settings is chosen that
            // ListBox clears its own selection and its TwoWay binding writes null back here.
            // "No destination" is not a state this shell has; accepting it would make every
            // Is*Selected getter throw, and WPF would silently keep the last good visibility,
            // leaving two pages drawn on top of each other.
            if (value is null ||
                EqualityComparer<NavigationItem>.Default.Equals(_selectedSection, value))
            {
                return;
            }

            _selectedSection = value;

            // Leaving Servers closes the detail view, so returning to Servers always lands
            // on the list rather than whatever server happened to be open before.
            if (!value.Key.Equals("Servers", StringComparison.Ordinal))
            {
                _serverDetailOpen = false;
            }

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedSection)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PageTitle)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PageSubtitle)));
            foreach (var key in DestinationKeys)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs($"Is{key}Selected"));
            }

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsServerDetailOpen)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsServerListVisible)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsServerDetailVisible)));
        }
    }

    public bool IsHomeSelected => IsSelected("Home");

    public bool IsServersSelected => IsSelected("Servers");

    public bool IsBackupsSelected => IsSelected("Backups");

    public bool IsNetworkSelected => IsSelected("Network");

    public bool IsSettingsSelected => IsSelected("Settings");

    /// <summary>
    /// Server Detail is a view *inside* Servers, not a sixth destination: the sidebar keeps
    /// Servers highlighted while it is open, so the five-destination structure is unchanged.
    /// </summary>
    public bool IsServerDetailOpen
    {
        get => _serverDetailOpen;
        private set
        {
            if (!SetField(ref _serverDetailOpen, value))
            {
                return;
            }

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsServerListVisible)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsServerDetailVisible)));
        }
    }

    public Guid OpenServerId { get; private set; }

    public bool IsServerListVisible => IsServersSelected && !IsServerDetailOpen;

    public bool IsServerDetailVisible => IsServersSelected && IsServerDetailOpen;

    public void OpenServerDetail(Guid serverId)
    {
        OpenServerId = serverId;
        SelectSection("Servers");
        IsServerDetailOpen = true;
    }

    public void CloseServerDetail() => IsServerDetailOpen = false;

    /// <summary>The current destination's own title, shown once in the top bar.</summary>
    public string PageTitle => SelectedSection.Title;

    public string PageSubtitle => SelectedSection.Description;

    public bool SidebarCollapsed
    {
        get => _sidebarCollapsed;
        set
        {
            if (!SetField(ref _sidebarCollapsed, value))
            {
                return;
            }

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SidebarLabelsVisible)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SidebarToggleTooltip)));
        }
    }

    public bool SidebarLabelsVisible => !SidebarCollapsed;

    public string SidebarToggleTooltip => LocalizationService.Get(
        SidebarCollapsed ? "Shell.ExpandSidebar" : "Shell.CollapseSidebar");

    public string RefreshTooltip => LocalizationService.Get("Shell.Refresh");

    /// <summary>
    /// The header badge answers one question: can this app show and control the servers right
    /// now? Reaching the status pipe is not enough — if the data channel is down every page is
    /// empty, and a green badge over an error message reads as a lie. Before the first poll
    /// comes back there is nothing to contradict, so the pipe alone decides.
    /// </summary>
    public bool IsAgentConnected =>
        _agentConnected && !_feed.ShowErrorState;

    /// <summary>
    /// Nothing has come back yet from either channel. The badge is neutral then: a red dot
    /// beside "Connecting…" announces a failure that has not happened.
    /// </summary>
    public bool IsConnecting =>
        !_agentConnected && (!_pipeAnswered || _pipeFailures < PipeFailuresBeforeLost) &&
        !_feed.ShowErrorState;

    /// <summary>The Agent refuses this non-elevated session: a caution, not a failure.</summary>
    public bool NeedsElevation => !IsAgentConnected && _feed.NeedsElevation;

    /// <summary>Consecutive failed pipe polls tolerated, matching the dashboard feed's grace.</summary>
    private const int PipeFailuresBeforeLost = 2;

    private bool IsSelected(string key) =>
        SelectedSection.Key.Equals(key, StringComparison.Ordinal);

    public string ConnectionLabel
    {
        get => _connectionLabel;
        private set => SetField(ref _connectionLabel, value);
    }

    public string AgentMachineName
    {
        get => _agentMachineName;
        private set => SetField(ref _agentMachineName, value);
    }

    public string AgentVersion
    {
        get => _agentVersion;
        private set => SetField(ref _agentVersion, value);
    }

    public string DatabaseLabel
    {
        get => _databaseLabel;
        private set => SetField(ref _databaseLabel, value);
    }

    public string LastError
    {
        get => _lastError;
        private set => SetField(ref _lastError, value);
    }

    public async Task StartAsync()
    {
        await RefreshAsync();
        _timer.Start();
    }

    public void ApplyUiPreferences(UiPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        var selectedKey = SelectedSection.Key;
        FlowDirection = LayoutDirectionService.ForCulture(
            CultureInfo.GetCultureInfo(preferences.Language));
        SidebarCollapsed = preferences.SidebarCollapsed;
        var refreshed = DestinationKeys.Select(CreateNavigationItem).ToArray();
        Sections.Clear();
        foreach (var item in refreshed)
        {
            Sections.Add(item);
        }

        PrimarySections.Clear();
        foreach (var item in refreshed.Where(IsPrimaryDestination))
        {
            PrimarySections.Add(item);
        }

        SelectedSection = Sections.FirstOrDefault(item =>
                item.Key.Equals(selectedKey, StringComparison.Ordinal))
            ?? Sections[0];
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SettingsSection)));
        // Same rule as every poll, so a language switch cannot show "Connected" by a red dot.
        UpdateConnectionPresentation();
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(LaunchModeLabel)));
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
        _feed.PropertyChanged -= OnFeedChanged;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private async void OnTimerTick(object? sender, EventArgs e) =>
        await RefreshAsync();

    /// <summary>Raised by the visible Refresh button so pages with their own data reload it.</summary>
    public event EventHandler? RefreshRequested;

    /// <summary>
    /// The top-bar Refresh. It used to re-read only the pipe status, so on every page it
    /// visibly did nothing; it now refreshes what the pages actually show.
    /// </summary>
    private async Task RefreshAllAsync()
    {
        await RefreshAsync();
        await _feed.RefreshAsync();
        RefreshRequested?.Invoke(this, EventArgs.Empty);
    }

    private async Task RefreshAsync()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1)
        {
            return;
        }

        try
        {
            var status = await _agentClient.GetStatusAsync(_lifetime.Token);
            _pipeAnswered = true;
            _pipeFailures = 0;
            _agentConnected = true;
            UpdateConnectionPresentation();
            AgentMachineName = status.MachineName;
            AgentVersion = status.Version;
            DatabaseLabel = status.DatabaseReady ? "Ready" : "Starting";
            LastError = string.Empty;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        // Runs from an async void timer tick: a malformed pipe frame (InvalidDataException,
        // JsonException, InvalidOperationException) must read as "not connected", not crash
        // the app. Only the second consecutive failure counts, like the dashboard feed.
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _pipeAnswered = true;
            if (_pipeFailures < int.MaxValue)
            {
                _pipeFailures++;
            }

            if (_pipeFailures >= PipeFailuresBeforeLost)
            {
                _agentConnected = false;
                DatabaseLabel = LocalizationService.Get("Status.Unavailable");
                LastError = LocalizationService.Get("Error.ServiceHint");
            }

            UpdateConnectionPresentation();
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    private void OnFeedChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Controls.DashboardFeed.ShowErrorState)
            or nameof(Controls.DashboardFeed.HasCurrentData))
        {
            UpdateConnectionPresentation();
        }
    }

    private void UpdateConnectionPresentation()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAgentConnected)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsConnecting)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NeedsElevation)));
        ConnectionLabel = LocalizationService.Get(
            IsAgentConnected
                ? "Shell.Connected"
                : IsConnecting
                    ? "Shell.Connecting"
                    : _feed.NeedsElevation ? "Shell.NeedsAdministrator" : "Shell.AgentUnavailable");
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    private static NavigationItem CreateNavigationItem(string key) =>
        new(
            key,
            LocalizationService.Get(key),
            LocalizationService.Get($"{key}.Description"),
            DestinationGlyphs.TryGetValue(key, out var glyph) ? glyph : string.Empty);

    private static string BuildLabel(string? version, int? revision) =>
        version is null
            ? "None"
            : revision is > 0
                ? $"{version} Build {revision}"
                : version;
}
