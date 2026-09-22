using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using ServerManager.Client.Shell;
using ServerManager.Contracts;

namespace ServerManager.Client.Controls;

/// <summary>
/// One plugin on the Discover list. Project names, authors, versions and file names stay in
/// the provider's own words; only the wording around them is translated.
/// </summary>
public sealed class ContentItemViewModel(ContentProject project) : INotifyPropertyChanged
{
    private bool _isBusy;
    private bool _isInstalled;
    private string _actionLabel = LocalizationService.Get("Content.Install");

    public event PropertyChangedEventHandler? PropertyChanged;

    public ContentProject Project { get; } = project;

    public ContentProviderId Provider => Project.Provider;

    public string ProjectId => Project.ProjectId;

    public string Name => Project.Name;

    public string Summary => Project.Summary ?? string.Empty;

    public string AuthorLine => string.IsNullOrWhiteSpace(Project.Author)
        ? string.Empty
        : LocalizationService.Format("Content.ByAuthor", Project.Author);

    public string SourceLine => LocalizationService.Format(
        "Content.Source",
        Project.Provider == ContentProviderId.Hangar ? "Hangar" : "Modrinth");

    public string DownloadsLine => Project.Downloads is { } downloads
        ? LocalizationService.Format("Content.Downloads", FormatCount(downloads))
        : string.Empty;

    public bool HasDownloads => Project.Downloads is not null;

    /// <summary>Reads "Paper · Minecraft 1.21.8" from what the provider actually returned.</summary>
    public string CompatibilityLine => Project.CompatibilitySummary ?? string.Empty;

    public string? IconUrl => Project.IconUrl?.ToString();

    public bool IsCompatible => Project.IsCompatible;

    public string AutomationName => LocalizationService.Format("Content.InstallNamed", Name);

    public string DetailsLabel => LocalizationService.Get("Content.Details");

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (_isBusy == value)
            {
                return;
            }

            _isBusy = value;
            Raise();
            Raise(nameof(CanInstall));
        }
    }

    public bool IsInstalled
    {
        get => _isInstalled;
        set
        {
            if (_isInstalled == value)
            {
                return;
            }

            _isInstalled = value;
            ActionLabel = LocalizationService.Get(value ? "Content.AlreadyInstalled" : "Content.Install");
            Raise();
            Raise(nameof(CanInstall));
        }
    }

    public bool CanInstall => IsCompatible && !IsBusy && !IsInstalled;

    public string ActionLabel
    {
        get => _actionLabel;
        private set
        {
            _actionLabel = value;
            Raise();
        }
    }

    private static string FormatCount(long value) =>
        value switch
        {
            >= 1_000_000 => $"{value / 1_000_000.0:0.#}M",
            >= 1_000 => $"{value / 1_000.0:0.#}K",
            _ => value.ToString("N0")
        };

    private void Raise([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>One row on Installed and Updates.</summary>
public sealed class InstalledItemViewModel(InstalledContent record) : INotifyPropertyChanged
{
    private bool _isBusy;

    public event PropertyChangedEventHandler? PropertyChanged;

    public InstalledContent Record { get; private set; } = record;

    public string FileName => Record.FileName;

    public string DisplayName => Record.ProjectName ?? Path.GetFileNameWithoutExtension(Record.FileName);

    public string VersionLine => string.IsNullOrWhiteSpace(Record.InstalledVersion)
        ? LocalizationService.Get("Content.VersionUnknown")
        : LocalizationService.Format("Content.VersionNamed", Record.InstalledVersion);

    public string SourceLine => Record.Provider switch
    {
        ContentProviderId.Modrinth => LocalizationService.Format("Content.Source", "Modrinth"),
        ContentProviderId.Hangar => LocalizationService.Format("Content.Source", "Hangar"),
        _ => LocalizationService.Get("Content.SourceUnknown")
    };

    public string StatusLabel => LocalizationService.Get(Record.State switch
    {
        InstalledContentState.UpToDate => "Content.State.UpToDate",
        InstalledContentState.UpdateAvailable => "Content.State.UpdateAvailable",
        InstalledContentState.RestartRequired => "Content.State.RestartRequired",
        InstalledContentState.InstalledManually => "Content.State.InstalledManually",
        InstalledContentState.MissingFile => "Content.State.MissingFile",
        InstalledContentState.ModifiedLocally => "Content.State.ModifiedLocally",
        _ => "Content.State.UnknownVersion"
    });

    public string UpdateLine => Record.AvailableVersionNumber is { Length: > 0 } available
        ? LocalizationService.Format(
            "Content.UpdateFromTo",
            Record.InstalledVersion ?? LocalizationService.Get("Content.VersionUnknown"),
            available)
        : string.Empty;

    public bool CanUpdate => !IsBusy && Record.State == InstalledContentState.UpdateAvailable;

    public bool CanRollback => !IsBusy && Record.PreviousFileName is { Length: > 0 };

    public bool CanUninstall => !IsBusy && Record.State != InstalledContentState.MissingFile;

    public bool CanViewProject => Record.ProjectUrl is not null;

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (_isBusy == value)
            {
                return;
            }

            _isBusy = value;
            Raise();
            Raise(nameof(CanUpdate));
            Raise(nameof(CanRollback));
            Raise(nameof(CanUninstall));
        }
    }

    public string ViewProjectLabel => LocalizationService.Get("Content.ViewProject");

    public string UpdateLabel => LocalizationService.Get("Content.Update");

    public string RollbackLabel => LocalizationService.Get("Content.Rollback");

    public string UninstallLabel => LocalizationService.Get("Content.Uninstall");

    public string UpdateAutomationName =>
        LocalizationService.Format("Content.UpdateNamed", DisplayName);

    public string UninstallAutomationName =>
        LocalizationService.Format("Content.UninstallNamed", DisplayName);

    public void Update(InstalledContent record)
    {
        Record = record;
        Raise(nameof(DisplayName));
        Raise(nameof(VersionLine));
        Raise(nameof(SourceLine));
        Raise(nameof(StatusLabel));
        Raise(nameof(UpdateLine));
        Raise(nameof(CanUpdate));
        Raise(nameof(CanRollback));
        Raise(nameof(CanUninstall));
        Raise(nameof(CanViewProject));
    }

    private void Raise([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
