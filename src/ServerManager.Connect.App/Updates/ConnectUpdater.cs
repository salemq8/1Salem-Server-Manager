using ServerManager.Connect.App.Services;

namespace ServerManager.Connect.App.Updates;

public enum UpdateStatus
{
    NotChecked,
    Checking,
    UpToDate,
    Available,

    /// <summary>The last check could not tell (GitHub unreachable, or nothing usable published).</summary>
    Unavailable,
    Downloading,

    /// <summary>Setup has started; this app is about to close for it.</summary>
    Installing,
    Failed
}

public enum UpdateProblem
{
    None,
    Unreachable,
    NoInformation,
    InvalidInformation,
    DownloadFailed,
    HashMismatch,
    Cancelled,
    LaunchFailed
}

public enum PostUpdateOutcome
{
    None,

    /// <summary>This run is the build the last update installed.</summary>
    Updated,

    /// <summary>The update did not complete; this is still the earlier build.</summary>
    NotUpdated,

    /// <summary>Setup may still be working (this copy was opened while it runs).</summary>
    InProgress
}

public sealed record PostUpdateReport(PostUpdateOutcome Outcome, int FromBuild = 0, int ToBuild = 0, InstallerResult? Result = null);

/// <summary>
/// The update flow without any window: check the newest release, decide by version then build,
/// download and verify the installer, hand it the update, and on the next start tell whether
/// the update took effect. Automatic checks happen at most once a day and never download or
/// install anything; that always waits for the friend.
/// </summary>
public sealed class ConnectUpdater
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromDays(1);

    /// <summary>An update handed to Setup this recently with no result yet may still be running.</summary>
    public static readonly TimeSpan InstallGrace = TimeSpan.FromMinutes(10);

    private readonly IUpdateHttp _http;
    private readonly UpdateStateStore _store;
    private readonly IUpdateInstallerLauncher _launcher;
    private readonly IUpdateHandshake _handshake;
    private readonly IAppClock _clock;
    private readonly DiagnosticsLog _log;
    private readonly string _downloadDirectory;
    private UpdateState _state;

    public ConnectUpdater(
        IUpdateHttp http,
        UpdateStateStore store,
        IUpdateInstallerLauncher launcher,
        IUpdateHandshake handshake,
        IAppClock clock,
        DiagnosticsLog log,
        ConnectBuild current,
        InstallationKind kind,
        string downloadDirectory)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _handshake = handshake ?? throw new ArgumentNullException(nameof(handshake));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _downloadDirectory = downloadDirectory ?? throw new ArgumentNullException(nameof(downloadDirectory));
        Current = current;
        Kind = kind;
        _state = store.Load();
    }

    public event EventHandler? Changed;

    public ConnectBuild Current { get; }

    public InstallationKind Kind { get; }

    public UpdateStatus Status { get; private set; } = UpdateStatus.NotChecked;

    public UpdateProblem Problem { get; private set; }

    /// <summary>The newer release, once a check found one.</summary>
    public ConnectUpdateManifest? Offer { get; private set; }

    public DateTimeOffset? LastCheckedUtc => _state.LastCheckedUtc;

    /// <summary>0..1 while downloading.</summary>
    public double DownloadProgress { get; private set; }

    public bool IsBusy => Status is UpdateStatus.Checking or UpdateStatus.Downloading or UpdateStatus.Installing;

    public bool IsAutomaticCheckDue =>
        _state.LastCheckedUtc is not { } last || _clock.UtcNow - last >= CheckInterval || last > _clock.UtcNow;

    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        if (IsBusy)
        {
            return;
        }

        Set(UpdateStatus.Checking, UpdateProblem.None);
        byte[]? bytes;
        try
        {
            bytes = await _http.GetManifestAsync(cancellationToken);
        }
        catch (UpdateDownloadException exception)
        {
            _log.Record("update-check", exception);
            Set(UpdateStatus.Unavailable, UpdateProblem.Unreachable);
            return;
        }

        if (bytes is null)
        {
            _log.Record("update-check", "the newest release publishes no 1Salem Connect update information");
            Set(UpdateStatus.Unavailable, UpdateProblem.NoInformation);
            return;
        }

        ConnectUpdateManifest manifest;
        try
        {
            manifest = ConnectUpdateManifestReader.Read(bytes);
        }
        catch (ConnectUpdateManifestException exception)
        {
            _log.Record("update-check", exception);
            Set(UpdateStatus.Unavailable, UpdateProblem.InvalidInformation);
            return;
        }

        Remember(_state with { LastCheckedUtc = _clock.UtcNow });
        switch (ConnectUpdatePolicy.Decide(Current, manifest.Build))
        {
            case UpdateDecision.UpdateAvailable:
                Offer = manifest;
                Set(UpdateStatus.Available, UpdateProblem.None);
                break;
            case UpdateDecision.OlderRejected:
                // Never offered: an older release is not an update (no downgrades).
                _log.Record("update-check", $"ignored {manifest.Build}: this app is already {Current}");
                Offer = null;
                Set(UpdateStatus.UpToDate, UpdateProblem.None);
                break;
            default:
                Offer = null;
                Set(UpdateStatus.UpToDate, UpdateProblem.None);
                break;
        }
    }

    /// <summary>Downloads and verifies the offered installer. Null (with <see cref="Problem"/> set) when that failed.</summary>
    public async Task<string?> DownloadInstallerAsync(CancellationToken cancellationToken)
    {
        if (Kind != InstallationKind.Installed || Offer is not { } offer || IsBusy)
        {
            return null;
        }

        var folder = Path.Combine(_downloadDirectory, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"build-{offer.Build.BuildRevision}"));
        var target = Path.Combine(folder, ConnectUpdateSource.InstallerFileName);
        DeleteOtherDownloads(folder);
        DownloadProgress = 0;
        Set(UpdateStatus.Downloading, UpdateProblem.None);
        try
        {
            var progress = new Progress<double>(value =>
            {
                DownloadProgress = value;
                Changed?.Invoke(this, EventArgs.Empty);
            });
            await _http.DownloadAsync(offer.Installer, target, progress, cancellationToken);
            return target;
        }
        catch (UpdateDownloadException exception)
        {
            _log.Record("update-download", exception);
            Set(UpdateStatus.Failed, exception.Failure == UpdateDownloadFailure.HashMismatch
                ? UpdateProblem.HashMismatch
                : UpdateProblem.DownloadFailed);
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log.Record("update-download", exception);
            Set(UpdateStatus.Failed, UpdateProblem.DownloadFailed);
            return null;
        }
    }

    /// <summary>
    /// Records the update in progress, then starts Setup with the Windows administrator prompt.
    /// <see cref="InstallerLaunchOutcome.Started"/> means this app must now close so Setup can
    /// replace its files; any other outcome leaves everything as it was.
    /// </summary>
    public InstallerLaunchOutcome StartInstaller(string verifiedInstaller, int processId)
    {
        if (Kind != InstallationKind.Installed || Offer is not { } offer)
        {
            return InstallerLaunchOutcome.Failed;
        }

        var token = UpdateProtocol.NewToken();
        var pending = new PendingUpdate(
            token,
            Current.ProductVersion,
            Current.BuildRevision,
            offer.Build.ProductVersion,
            offer.Build.BuildRevision,
            _clock.UtcNow);
        InstallerLaunchOutcome outcome;
        try
        {
            Remember(_state with { Pending = pending }, required: true);
            outcome = _launcher.Launch(verifiedInstaller, offer.Installer.Sha256, UpdateProtocol.Arguments(processId, offer.Build, token));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or UpdateDownloadException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _log.Record("update-install", exception);
            outcome = InstallerLaunchOutcome.Failed;
        }

        switch (outcome)
        {
            case InstallerLaunchOutcome.Started:
                Set(UpdateStatus.Installing, UpdateProblem.None);
                break;
            case InstallerLaunchOutcome.Cancelled:
                Remember(_state with { Pending = null });
                Set(UpdateStatus.Available, UpdateProblem.Cancelled);
                break;
            default:
                Remember(_state with { Pending = null });
                Set(UpdateStatus.Failed, UpdateProblem.LaunchFailed);
                break;
        }

        return outcome;
    }

    /// <summary>
    /// At startup: if an update was handed to Setup, say how it ended. The new build tells Setup
    /// it started (Setup keeps the previous build until then and restores it otherwise).
    /// </summary>
    public PostUpdateReport ReviewAtStartup()
    {
        if (_state.Pending is not { } pending)
        {
            return new PostUpdateReport(PostUpdateOutcome.None);
        }

        if (!UpdateProtocol.IsToken(pending.Token))
        {
            Remember(_state with { Pending = null });
            return new PostUpdateReport(PostUpdateOutcome.None);
        }

        if (Current.BuildRevision == pending.ToBuild &&
            string.Equals(Current.ProductVersion, pending.ToVersion, StringComparison.Ordinal))
        {
            if (!_handshake.SignalStarted(pending.Token))
            {
                _log.Record("update", "the installer was no longer waiting for this build to start");
            }

            Remember(_state with { Pending = null });
            DeleteOtherDownloads(keep: null);
            return new PostUpdateReport(PostUpdateOutcome.Updated, pending.FromBuild, pending.ToBuild);
        }

        var result = _handshake.ReadResult(pending.Token);
        if (result is null && _clock.UtcNow - pending.StartedUtc < InstallGrace && pending.StartedUtc <= _clock.UtcNow)
        {
            return new PostUpdateReport(PostUpdateOutcome.InProgress, pending.FromBuild, pending.ToBuild);
        }

        if (result is not null)
        {
            _log.Record("update", $"Setup reported {result.Outcome}: {result.Message}");
        }

        Remember(_state with { Pending = null });
        return new PostUpdateReport(PostUpdateOutcome.NotUpdated, pending.FromBuild, pending.ToBuild, result);
    }

    private void Set(UpdateStatus status, UpdateProblem problem)
    {
        Status = status;
        Problem = problem;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Remember(UpdateState state, bool required = false)
    {
        try
        {
            _store.Save(state);
            _state = state;
        }
        catch (Exception exception) when (!required && exception is IOException or UnauthorizedAccessException)
        {
            // Only a convenience (the last-checked time); the app keeps working without it.
            _log.Record("update-state", exception);
            _state = state;
        }
    }

    private void DeleteOtherDownloads(string? keep)
    {
        try
        {
            if (!Directory.Exists(_downloadDirectory))
            {
                return;
            }

            foreach (var folder in Directory.EnumerateDirectories(_downloadDirectory, "build-*"))
            {
                if (keep is null || !string.Equals(Path.GetFullPath(folder), Path.GetFullPath(keep), StringComparison.OrdinalIgnoreCase))
                {
                    Directory.Delete(folder, recursive: true);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log.Record("update-download", exception);
        }
    }
}
