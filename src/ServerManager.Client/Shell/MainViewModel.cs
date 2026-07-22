using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using ServerManager.Infrastructure.Transport;

namespace ServerManager.Client.Shell;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly NamedPipeAgentClient _agentClient = new();
    private readonly DispatcherTimer _timer;
    private readonly CancellationTokenSource _lifetime = new();
    private NavigationItem _selectedSection;
    private string _connectionLabel = "Agent unavailable";
    private string _agentMachineName = "—";
    private string _agentVersion = "—";
    private string _databaseLabel = "Unknown";
    private string _lastError = string.Empty;
    private int _refreshing;

    public MainViewModel(ClientLaunchMode launchMode)
    {
        LaunchModeLabel = launchMode == ClientLaunchMode.Administrator
            ? "Administrator mode"
            : "Normal mode";
        IsAdministrator = launchMode == ClientLaunchMode.Administrator;
        FlowDirection = LayoutDirectionService.ForCulture(CultureInfo.CurrentUICulture);
        Sections =
        [
            CreateNavigationItem("Home"),
            CreateNavigationItem("Minecraft"),
            CreateNavigationItem("Palworld"),
            CreateNavigationItem("RemoteAccess"),
            CreateNavigationItem("Backups"),
            CreateNavigationItem("Updates"),
            CreateNavigationItem("System"),
            CreateNavigationItem("Settings")
        ];
        _selectedSection = Sections[0];
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _timer.Tick += OnTimerTick;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<NavigationItem> Sections { get; }

    public AsyncRelayCommand RefreshCommand { get; }

    public string LaunchModeLabel { get; }

    public bool IsAdministrator { get; }

    public System.Windows.FlowDirection FlowDirection { get; }

    public NavigationItem SelectedSection
    {
        get => _selectedSection;
        set
        {
            if (EqualityComparer<NavigationItem>.Default.Equals(_selectedSection, value))
            {
                return;
            }

            _selectedSection = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedSection)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsHomeSelected)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsMinecraftSelected)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPalworldSelected)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRemoteAccessSelected)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsBackupsSelected)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsUpdatesSelected)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsResourcesSelected)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsNetworkSelected)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSettingsSelected)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAboutSelected)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsFilesSelected)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLogsSelected)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsHubSelected)));
        }
    }

    public bool IsHomeSelected =>
        SelectedSection.Key.Equals("Home", StringComparison.Ordinal);

    public bool IsMinecraftSelected =>
        SelectedSection.Key.Equals("Minecraft", StringComparison.Ordinal);

    public bool IsPalworldSelected =>
        SelectedSection.Key.Equals("Palworld", StringComparison.Ordinal);

    public bool IsRemoteAccessSelected =>
        SelectedSection.Key.Equals("RemoteAccess", StringComparison.Ordinal);

    public bool IsBackupsSelected =>
        SelectedSection.Key.Equals("Backups", StringComparison.Ordinal);

    public bool IsUpdatesSelected =>
        SelectedSection.Key.Equals("Updates", StringComparison.Ordinal);

    public bool IsResourcesSelected =>
        SelectedSection.Key.Equals("System", StringComparison.Ordinal);

    public bool IsNetworkSelected =>
        SelectedSection.Key.Equals("Network", StringComparison.Ordinal);

    public bool IsSettingsSelected =>
        SelectedSection.Key.Equals("Settings", StringComparison.Ordinal);

    public bool IsAboutSelected =>
        SelectedSection.Key.Equals("About", StringComparison.Ordinal);

    public bool IsFilesSelected =>
        SelectedSection.Key.Equals("Files", StringComparison.Ordinal);

    public bool IsLogsSelected =>
        SelectedSection.Key.Equals("Logs", StringComparison.Ordinal);

    public bool IsHubSelected =>
        IsBackupsSelected ||
        IsResourcesSelected ||
        IsFilesSelected ||
        IsLogsSelected ||
        IsSettingsSelected ||
        IsAboutSelected;

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

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private async void OnTimerTick(object? sender, EventArgs e) =>
        await RefreshAsync();

    private async Task RefreshAsync()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1)
        {
            return;
        }

        try
        {
            var status = await _agentClient.GetStatusAsync(_lifetime.Token);
            ConnectionLabel = "Connected locally";
            AgentMachineName = status.MachineName;
            AgentVersion = status.Version;
            DatabaseLabel = status.DatabaseReady ? "Ready" : "Starting";
            LastError = string.Empty;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (
            exception is IOException or TimeoutException or UnauthorizedAccessException or OperationCanceledException)
        {
            ConnectionLabel = "Agent unavailable";
            DatabaseLabel = "Unavailable";
            LastError = "The local Agent is not reachable. Start the Agent service, then retry.";
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private static NavigationItem CreateNavigationItem(string key) =>
        new(
            key,
            LocalizationService.Get(key),
            LocalizationService.Get($"{key}.Description"));
}
