using System.Globalization;
using ServerManager.Connect.App.Localization;
using ServerManager.Connect.App.Shell;
using ServerManager.Connect.App.Updates;

namespace ServerManager.Connect.App.ViewModels;

public sealed record ThemeOption(ThemeChoice Choice, string Label);

/// <summary>
/// Settings: Updates (version, build, channel, last check, check and update) and Appearance.
/// Updating an installed copy downloads and verifies the installer, asks for disconnection first
/// when a server is connected, then hands over to Setup and closes. A portable copy is only told
/// a newer portable package exists. Nothing technical is shown: no URLs, hashes or JSON.
/// </summary>
public sealed class SettingsViewModel : ObservableObject, IPageLifetime
{
    private readonly ConnectUpdater? _updater;
    private readonly ConnectPreferencesStore? _preferences;
    private readonly IAppShell? _shell;
    private readonly Func<IReadOnlyList<ConnectionViewModel>> _activeConnections;
    private bool _needsDisconnect;
    private string? _disconnectProblem;
    private ThemeOption _selectedTheme;

    public SettingsViewModel(
        ConnectUpdater? updater,
        ConnectPreferencesStore? preferences,
        IAppShell? shell,
        Func<IReadOnlyList<ConnectionViewModel>> activeConnections)
    {
        _updater = updater;
        _preferences = preferences;
        _shell = shell;
        _activeConnections = activeConnections ?? throw new ArgumentNullException(nameof(activeConnections));
        CheckCommand = new AsyncCommand(CheckAsync, () => _updater is { IsBusy: false });
        UpdateNowCommand = new AsyncCommand(UpdateNowAsync, () => ShowUpdateNow);
        DisconnectAndUpdateCommand = new AsyncCommand(DisconnectAndUpdateAsync, () => NeedsDisconnect && _updater is { IsBusy: false });
        LaterCommand = new RelayCommand(() =>
        {
            _disconnectProblem = null;
            NeedsDisconnect = false;
            Refresh();
        });
        DownloadPortableCommand = new RelayCommand(OpenPortableDownload, () => ShowPortableDownload);
        ThemeOptions =
        [
            new ThemeOption(ThemeChoice.System, Text.ThemeSystem),
            new ThemeOption(ThemeChoice.Dark, Text.ThemeDark),
            new ThemeOption(ThemeChoice.Light, Text.ThemeLight)
        ];
        var saved = _preferences?.Load().Theme ?? ThemeChoice.System;
        _selectedTheme = ThemeOptions.FirstOrDefault(option => option.Choice == saved) ?? ThemeOptions[0];
    }

    public string VersionText => Current.ProductVersion;

    public string BuildText => Current.BuildRevision.ToString(CultureInfo.CurrentCulture);

    public string ChannelText => Text.UpdatesChannelStable;

    public string LastCheckedText => _updater?.LastCheckedUtc is { } checkedAt
        ? checkedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
        : Text.UpdatesNever;

    public string StatusText => (_updater?.Status ?? UpdateStatus.NotChecked) switch
    {
        UpdateStatus.Checking => Text.UpdatesStatusChecking,
        UpdateStatus.UpToDate => Text.UpdatesStatusUpToDate,
        UpdateStatus.Available => Text.UpdatesStatusAvailable,
        UpdateStatus.Unavailable => Text.UpdatesStatusUnavailable,
        UpdateStatus.Downloading => Text.Format(
            Text.UpdatesStatusDownloading,
            _updater!.DownloadProgress.ToString("P0", CultureInfo.CurrentCulture)),
        UpdateStatus.Installing => Text.UpdatesStatusInstalling,
        UpdateStatus.Failed => Text.UpdatesStatusFailed,
        _ => Text.UpdatesStatusNotChecked
    };

    /// <summary>Why the last step did not work, in one plain sentence; null when nothing went wrong.</summary>
    public string? ProblemText => _disconnectProblem ?? (_updater?.Problem ?? UpdateProblem.None) switch
    {
        UpdateProblem.Unreachable => Text.UpdatesProblemUnreachable,
        UpdateProblem.NoInformation => Text.UpdatesProblemNoInformation,
        UpdateProblem.InvalidInformation => Text.UpdatesProblemInvalid,
        UpdateProblem.DownloadFailed => Text.UpdatesProblemDownload,
        UpdateProblem.HashMismatch => Text.UpdatesProblemHash,
        UpdateProblem.Cancelled => Text.UpdatesProblemCancelled,
        UpdateProblem.LaunchFailed => Text.UpdatesProblemLaunch,
        _ => null
    };

    public bool HasProblem => ProblemText is not null;

    public bool IsUpdateAvailable => _updater?.Offer is not null &&
        _updater.Status is UpdateStatus.Available or UpdateStatus.Downloading or UpdateStatus.Installing or UpdateStatus.Failed;

    public string? AvailableVersionText => _updater?.Offer is { } offer
        ? Text.Format(Text.UpdatesAvailableVersion, offer.Build.ProductVersion)
        : null;

    public string? AvailableBuildText => _updater?.Offer is { } offer
        ? Text.Format(Text.UpdatesAvailableBuild, offer.Build.BuildRevision.ToString(CultureInfo.CurrentCulture))
        : null;

    public bool IsInstalled => _updater?.Kind == InstallationKind.Installed;

    public bool IsPortable => _updater is { Kind: InstallationKind.Portable };

    public bool ShowUpdateNow => IsInstalled && IsUpdateAvailable && !NeedsDisconnect &&
        _updater is { IsBusy: false } && _shell is not null;

    public bool ShowPortableDownload => IsPortable && IsUpdateAvailable;

    public bool IsDownloading => _updater?.Status == UpdateStatus.Downloading;

    public double DownloadPercent => (_updater?.DownloadProgress ?? 0) * 100;

    /// <summary>A server is connected: the friend chooses between disconnecting now and later.</summary>
    public bool NeedsDisconnect
    {
        get => _needsDisconnect;
        private set
        {
            if (Set(ref _needsDisconnect, value))
            {
                Refresh();
            }
        }
    }

    public AsyncCommand CheckCommand { get; }

    public AsyncCommand UpdateNowCommand { get; }

    public AsyncCommand DisconnectAndUpdateCommand { get; }

    public RelayCommand LaterCommand { get; }

    public RelayCommand DownloadPortableCommand { get; }

    public IReadOnlyList<ThemeOption> ThemeOptions { get; }

    public ThemeOption SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            if (value is null || !Set(ref _selectedTheme, value))
            {
                return;
            }

            _shell?.ApplyTheme(value.Choice);
            try
            {
                _preferences?.Save(new ConnectPreferences(value.Choice));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The theme still applies for this run.
            }
        }
    }

    private ConnectBuild Current => _updater?.Current ?? ConnectBuild.Current;

    public void OnShown()
    {
        if (_updater is null)
        {
            return;
        }

        _updater.Changed += OnUpdaterChanged;
        Refresh();
        if (_updater.Status == UpdateStatus.NotChecked)
        {
            CheckCommand.Execute(null);
        }
    }

    public void OnHidden()
    {
        if (_updater is not null)
        {
            _updater.Changed -= OnUpdaterChanged;
        }
    }

    internal async Task CheckAsync()
    {
        if (_updater is null)
        {
            return;
        }

        _disconnectProblem = null;
        await _updater.CheckAsync(CancellationToken.None);
        Refresh();
    }

    internal async Task UpdateNowAsync()
    {
        if (_updater is null || _shell is null || !IsInstalled || !IsUpdateAvailable)
        {
            return;
        }

        _disconnectProblem = null;
        if (_activeConnections().Count > 0)
        {
            NeedsDisconnect = true;
            return;
        }

        await InstallAsync();
    }

    internal async Task DisconnectAndUpdateAsync()
    {
        if (_updater is null || _shell is null)
        {
            return;
        }

        foreach (var connection in _activeConnections())
        {
            await connection.DisconnectAsync();
        }

        // Only once every session is known to be closed: a close that could not be confirmed
        // leaves its address open, and replacing the app under it would cut the game off.
        if (_activeConnections().Count > 0)
        {
            _disconnectProblem = Text.UpdatesDisconnectFailed;
            Refresh();
            return;
        }

        NeedsDisconnect = false;
        await InstallAsync();
    }

    private async Task InstallAsync()
    {
        var installer = await _updater!.DownloadInstallerAsync(CancellationToken.None);
        Refresh();
        if (installer is null)
        {
            return;
        }

        // The download can take minutes, and the friend may have connected meanwhile.
        if (_activeConnections().Count > 0)
        {
            NeedsDisconnect = true;
            return;
        }

        if (_updater.StartInstaller(installer, _shell!.ProcessId) == InstallerLaunchOutcome.Started)
        {
            _shell.Shutdown();
        }

        Refresh();
    }

    private void OpenPortableDownload()
    {
        if (_updater?.Offer is { } offer)
        {
            _shell?.OpenReleaseLink(offer.Portable.Url);
        }
    }

    private void OnUpdaterChanged(object? sender, EventArgs e) => Refresh();

    private void Refresh()
    {
        OnPropertyChanged(nameof(LastCheckedText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ProblemText));
        OnPropertyChanged(nameof(HasProblem));
        OnPropertyChanged(nameof(IsUpdateAvailable));
        OnPropertyChanged(nameof(AvailableVersionText));
        OnPropertyChanged(nameof(AvailableBuildText));
        OnPropertyChanged(nameof(ShowUpdateNow));
        OnPropertyChanged(nameof(ShowPortableDownload));
        OnPropertyChanged(nameof(IsDownloading));
        OnPropertyChanged(nameof(DownloadPercent));
        CheckCommand.RaiseCanExecuteChanged();
        UpdateNowCommand.RaiseCanExecuteChanged();
        DisconnectAndUpdateCommand.RaiseCanExecuteChanged();
        DownloadPortableCommand.RaiseCanExecuteChanged();
    }
}
