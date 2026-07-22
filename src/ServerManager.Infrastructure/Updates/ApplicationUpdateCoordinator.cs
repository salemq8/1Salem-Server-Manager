using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ServerManager.Contracts;
using ServerManager.Core;
using ServerManager.Infrastructure.Persistence;
using ServerManager.Infrastructure.Windows;

namespace ServerManager.Infrastructure.Updates;

public sealed record ApplicationUpdateSourceOptions(
    Func<ApplicationUpdateChannel, Uri> ManifestResolver,
    bool AllowLoopbackTestSources = false)
{
    public static ApplicationUpdateSourceOptions Production { get; } =
        new(ApplicationUpdateEndpointCatalog.GetManifestUri);
}

public sealed class ApplicationUpdateCoordinator
{
    public const string SettingsKey = "application-update.settings.v1";
    public const string StateKey = "application-update.state.v1";
    private readonly HttpClient _httpClient;
    private readonly ISettingsStore _settingsStore;
    private readonly IGameServerStore _serverStore;
    private readonly SqliteStorageOptions _storageOptions;
    private readonly ApplicationUpdateSourceOptions _sourceOptions;
    private readonly ILogger<ApplicationUpdateCoordinator> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile int _downloadPercent;
    private long _downloadedBytes;
    private ApplicationUpdateStage? _transientStage;
    private string? _transientError;

    public ApplicationUpdateCoordinator(
        HttpClient httpClient,
        ISettingsStore settingsStore,
        IGameServerStore serverStore,
        SqliteStorageOptions storageOptions,
        ApplicationUpdateSourceOptions sourceOptions,
        ILogger<ApplicationUpdateCoordinator> logger)
    {
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromMinutes(30);
        _settingsStore = settingsStore;
        _serverStore = serverStore;
        _storageOptions = storageOptions;
        _sourceOptions = sourceOptions;
        _logger = logger;
    }

    public async Task<ApplicationUpdateStatusResponse> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        var settings = await LoadSettingsAsync(cancellationToken);
        var state = await LoadStateAsync(cancellationToken);
        var servers = await _serverStore.ListAsync(cancellationToken);
        var busy = servers.Any(item =>
            item.State is ServerState.Starting or
                ServerState.Running or
                ServerState.Restarting or
                ServerState.Updating or
                ServerState.BackingUp or
                ServerState.Restoring);
        var lastResult = await ReadLastResultAsync(cancellationToken);
        var rollbackStatus = lastResult is null
            ? state.RollbackStatus
            : lastResult.RolledBack
                ? "Last failed update rolled back successfully."
                : lastResult.Success
                    ? $"Rollback snapshot ready: {lastResult.RollbackPath}"
                    : "Last rollback was incomplete; repair with Setup.exe.";
        var stage = _transientStage ?? state.Stage;
        if (lastResult is { Success: true } &&
            SemanticVersion.TryParse(CurrentVersion, out var current) &&
            SemanticVersion.TryParse(state.LatestVersion, out var latest) &&
            current.CompareTo(latest) >= 0)
        {
            stage = ApplicationUpdateStage.Succeeded;
        }

        return new ApplicationUpdateStatusResponse(
            CurrentVersion,
            state.LatestVersion,
            settings.Channel,
            stage,
            IsAvailable(CurrentVersion, state.LatestVersion),
            settings.AutomaticChecksEnabled,
            _downloadPercent,
            Interlocked.Read(ref _downloadedBytes),
            state.Manifest?.PackageSize,
            state.ReleaseNotes,
            state.LastCheckedAtUtc,
            state.StagedPackagePath,
            rollbackStatus,
            _transientError ?? state.LastError,
            IsCurrentBuildSigned(),
            busy,
            state.History);
    }

    public async Task<ApplicationUpdateStatusResponse> SaveSettingsAsync(
        ApplicationUpdateSettingsRequest request,
        CancellationToken cancellationToken = default)
    {
        await _settingsStore.SetAsync(
            SettingsKey,
            new UpdateSettings(request.Channel, request.AutomaticChecksEnabled),
            cancellationToken);
        return await GetStatusAsync(cancellationToken);
    }

    public async Task<ApplicationUpdateStatusResponse> CheckAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _transientStage = ApplicationUpdateStage.Checking;
            _transientError = null;
            var settings = await LoadSettingsAsync(cancellationToken);
            var uri = _sourceOptions.ManifestResolver(settings.Channel);
            ValidateManifestEndpoint(uri);
            var json = await _httpClient.GetStringAsync(uri, cancellationToken);
            var manifest = ApplicationUpdateManifestReader.Read(
                json,
                settings.Channel,
                _sourceOptions.AllowLoopbackTestSources);
            var releaseNotes = await DownloadReleaseNotesAsync(
                manifest.ReleaseNotesUrl,
                cancellationToken);
            var previous = await LoadStateAsync(cancellationToken);
            var available = IsAvailable(CurrentVersion, manifest.Version);
            var belowMinimum = SemanticVersion.Parse(CurrentVersion).CompareTo(
                SemanticVersion.Parse(manifest.MinimumSupportedVersion)) < 0;
            var stage = (manifest.RequiresFullSetup || belowMinimum) && available
                ? ApplicationUpdateStage.FullSetupRequired
                : available
                    ? ApplicationUpdateStage.Available
                    : ApplicationUpdateStage.Idle;
            var state = previous with
            {
                LatestVersion = manifest.Version,
                Manifest = manifest,
                ReleaseNotes = releaseNotes,
                LastCheckedAtUtc = DateTimeOffset.UtcNow,
                Stage = stage,
                LastError = null
            };
            await SaveStateAsync(state, cancellationToken);
            _transientStage = null;
            return await GetStatusAsync(cancellationToken);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or
            TaskCanceledException or
            InvalidDataException or
            JsonException or
            FormatException)
        {
            _transientStage = ApplicationUpdateStage.Failed;
            _transientError = SafeError(exception);
            var state = await LoadStateAsync(CancellationToken.None);
            await SaveStateAsync(
                state with
                {
                    Stage = ApplicationUpdateStage.Failed,
                    LastError = _transientError,
                    LastCheckedAtUtc = DateTimeOffset.UtcNow
                },
                CancellationToken.None);
            return await GetStatusAsync(CancellationToken.None);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ApplicationUpdateStatusResponse> DownloadAsync(
        ApplicationUpdateActionRequest request,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await LoadStateAsync(cancellationToken);
            var manifest = state.Manifest
                ?? throw new InvalidOperationException("Check for updates before downloading.");
            if (!IsAvailable(CurrentVersion, manifest.Version))
            {
                throw new InvalidOperationException("No newer application update is available.");
            }

            if (manifest.RequiresFullSetup ||
                SemanticVersion.Parse(CurrentVersion).CompareTo(
                    SemanticVersion.Parse(manifest.MinimumSupportedVersion)) < 0)
            {
                throw new InvalidOperationException(
                    "This release changes system prerequisites and requires Setup.exe.");
            }

            var packageUri = new Uri(manifest.PackageUrl);
            ValidatePackageUri(packageUri);
            var stagingDirectory = Path.Combine(
                _storageOptions.DataRoot,
                "updates",
                "staging",
                manifest.Version);
            Directory.CreateDirectory(stagingDirectory);
            var partialPath = Path.Combine(
                stagingDirectory,
                "1SalemServerManager-Update.partial");
            var packagePath = Path.Combine(
                stagingDirectory,
                $"1SalemServerManager-Update-{manifest.Version}.zip");
            if (File.Exists(partialPath))
            {
                File.Delete(partialPath);
            }

            _transientStage = ApplicationUpdateStage.Downloading;
            _downloadPercent = 0;
            Interlocked.Exchange(ref _downloadedBytes, 0);
            using var response = await _httpClient.GetAsync(
                packageUri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();
            ValidateContentLength(response.Content.Headers, manifest.PackageSize);
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(
                             partialPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             128 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[128 * 1024];
                long received = 0;
                while (true)
                {
                    var read = await input.ReadAsync(buffer, cancellationToken);
                    if (read == 0)
                    {
                        break;
                    }

                    received += read;
                    if (received > manifest.PackageSize)
                    {
                        throw new InvalidDataException(
                            "Update download exceeded the signed manifest size.");
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    Interlocked.Exchange(ref _downloadedBytes, received);
                    _downloadPercent = (int)Math.Min(
                        100,
                        received * 100L / manifest.PackageSize);
                }
            }

            await UpdatePackageSecurity.VerifyFileAsync(
                partialPath,
                manifest.PackageSize,
                manifest.Sha256,
                cancellationToken);
            _ = UpdatePackageSecurity.ValidateArchive(partialPath);
            File.Move(partialPath, packagePath, overwrite: true);
            var updated = state with
            {
                Stage = ApplicationUpdateStage.ReadyToInstall,
                StagedPackagePath = packagePath,
                LastError = null
            };
            await SaveStateAsync(updated, cancellationToken);
            _downloadPercent = 100;
            _transientStage = null;
            return await GetStatusAsync(cancellationToken);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or
            TaskCanceledException or
            IOException or
            UnauthorizedAccessException or
            InvalidDataException or
            InvalidOperationException)
        {
            _transientStage = ApplicationUpdateStage.Failed;
            _transientError = SafeError(exception);
            var state = await LoadStateAsync(CancellationToken.None);
            await SaveStateAsync(
                state with
                {
                    Stage = ApplicationUpdateStage.Failed,
                    LastError = _transientError
                },
                CancellationToken.None);
            return await GetStatusAsync(CancellationToken.None);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ApplicationUpdateLaunchResponse> PrepareInstallAsync(
        ApplicationUpdateActionRequest request,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await LoadStateAsync(cancellationToken);
            var manifest = state.Manifest;
            if (manifest is null ||
                state.StagedPackagePath is null ||
                !File.Exists(state.StagedPackagePath))
            {
                return new ApplicationUpdateLaunchResponse(
                    false,
                    "Download and verify the update first.",
                    null,
                    [],
                    await GetStatusAsync(cancellationToken));
            }

            var servers = await _serverStore.ListAsync(cancellationToken);
            var busy = servers.Any(item => item.State == ServerState.Running);
            if (!ApplicationUpdatePolicy.CanInstall(
                    busy,
                    request.ApproveWhileGameServerBusy,
                    manifest.RequiresServiceRestart))
            {
                return new ApplicationUpdateLaunchResponse(
                    false,
                    "A game server is running. Update Later is recommended; approve explicitly only when it is safe to restart the Agent.",
                    null,
                    [],
                    await GetStatusAsync(cancellationToken));
            }

            var installRoot = ResolveInstallRoot();
            var sourceUpdater = Path.Combine(
                installRoot,
                "Client",
                "Updater");
            var updaterExecutable = Path.Combine(
                sourceUpdater,
                "1Salem.ServerManager.Updater.exe");
            if (!File.Exists(updaterExecutable))
            {
                return new ApplicationUpdateLaunchResponse(
                    false,
                    "Updater files are missing. Repair this installation once with Setup.exe.",
                    null,
                    [],
                    await GetStatusAsync(cancellationToken));
            }

            var runnerRoot = Path.Combine(
                _storageOptions.DataRoot,
                "updates",
                "runner",
                manifest.Version);
            CopyDirectory(sourceUpdater, runnerRoot);
            var runner = Path.Combine(runnerRoot, Path.GetFileName(updaterExecutable));
            var arguments = BuildUpdaterArguments(
                state.StagedPackagePath,
                installRoot,
                _storageOptions.DataRoot,
                manifest);
            var history = state.History.Prepend(new ApplicationUpdateHistoryEntry(
                    manifest.Version,
                    ApplicationUpdateStage.Installing,
                    DateTimeOffset.UtcNow,
                    null,
                    "Updater launched."))
                .Take(20)
                .ToArray();
            await SaveStateAsync(
                state with
                {
                    Stage = ApplicationUpdateStage.Installing,
                    History = history,
                    LastError = null
                },
                cancellationToken);
            return new ApplicationUpdateLaunchResponse(
                true,
                "Updater is ready. The dashboard will close and reopen after validation.",
                runner,
                arguments,
                await GetStatusAsync(cancellationToken));
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            InvalidDataException or
            InvalidOperationException)
        {
            _transientError = SafeError(exception);
            return new ApplicationUpdateLaunchResponse(
                false,
                _transientError,
                null,
                [],
                await GetStatusAsync(CancellationToken.None));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (await _settingsStore.GetAsync<UpdateSettings>(
                SettingsKey,
                cancellationToken) is null)
        {
            await _settingsStore.SetAsync(
                SettingsKey,
                UpdateSettings.Default,
                cancellationToken);
        }

        if (await _settingsStore.GetAsync<UpdateState>(
                StateKey,
                cancellationToken) is null)
        {
            await SaveStateAsync(UpdateState.Default, cancellationToken);
        }
    }

    public async Task CheckAutomaticallyAsync(
        CancellationToken cancellationToken = default)
    {
        var settings = await LoadSettingsAsync(cancellationToken);
        if (settings.AutomaticChecksEnabled)
        {
            _ = await CheckAsync(cancellationToken);
        }
    }

    private static string CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    private static bool IsAvailable(string current, string? latest) =>
        latest is not null &&
        SemanticVersion.TryParse(current, out var currentVersion) &&
        SemanticVersion.TryParse(latest, out var latestVersion) &&
        latestVersion.CompareTo(currentVersion) > 0;

    private void ValidateManifestEndpoint(Uri uri)
    {
        var expected = _sourceOptions.ManifestResolver(
            uri.AbsoluteUri.Equals(
                _sourceOptions.ManifestResolver(ApplicationUpdateChannel.Beta).AbsoluteUri,
                StringComparison.OrdinalIgnoreCase)
                ? ApplicationUpdateChannel.Beta
                : ApplicationUpdateChannel.Stable);
        if (!uri.AbsoluteUri.Equals(expected.AbsoluteUri, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The update manifest endpoint is not approved.");
        }

        ValidateTransport(uri);
    }

    private void ValidatePackageUri(Uri uri)
    {
        ValidateTransport(uri);
        var stableHost = _sourceOptions.ManifestResolver(ApplicationUpdateChannel.Stable).Host;
        var betaHost = _sourceOptions.ManifestResolver(ApplicationUpdateChannel.Beta).Host;
        if (!uri.IsLoopback &&
            !uri.Host.Equals(stableHost, StringComparison.OrdinalIgnoreCase) &&
            !uri.Host.Equals(betaHost, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The package host is not an approved update host.");
        }
    }

    private void ValidateTransport(Uri uri)
    {
        if (uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (_sourceOptions.AllowLoopbackTestSources &&
            uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            uri.IsLoopback)
        {
            return;
        }

        throw new InvalidDataException("Application updates require HTTPS.");
    }

    private async Task<string?> DownloadReleaseNotesAsync(
        string url,
        CancellationToken cancellationToken)
    {
        var uri = new Uri(url);
        ValidatePackageUri(uri);
        using var response = await _httpClient.GetAsync(
            uri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 262_144)
        {
            throw new InvalidDataException("Release notes exceed the 256 KB safety limit.");
        }

        var notes = await response.Content.ReadAsStringAsync(cancellationToken);
        return notes.Length <= 262_144 ? notes : notes[..262_144];
    }

    private static void ValidateContentLength(
        HttpContentHeaders headers,
        long expected)
    {
        if (headers.ContentLength is { } length && length != expected)
        {
            throw new InvalidDataException(
                $"Update response size mismatch. Expected {expected}, server reported {length}.");
        }
    }

    private static string ResolveInstallRoot()
    {
        var baseDirectory = new DirectoryInfo(
            Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
        if (baseDirectory.Name.Equals("Agent", StringComparison.OrdinalIgnoreCase) &&
            baseDirectory.Parent is not null)
        {
            return baseDirectory.Parent.FullName;
        }

        throw new InvalidOperationException(
            "Self-update is available from an installed release. Portable/development builds can check and download but are not replaced in place.");
    }

    private static IReadOnlyList<string> BuildUpdaterArguments(
        string package,
        string installRoot,
        string dataRoot,
        ApplicationUpdateManifest manifest)
    {
        var result = new List<string>
        {
            "--package", package,
            "--install-root", installRoot,
            "--data-root", dataRoot,
            "--version", manifest.Version,
            "--service-name", WindowsServiceManager.ServiceName,
            "--client", Path.Combine(
                installRoot,
                "Client",
                "1Salem.ServerManager.exe")
        };
        if (manifest.RequiresServiceRestart)
        {
            result.Add("--restart-agent");
        }

        if (manifest.RequiresElevation)
        {
            result.Add("--requires-elevation");
        }

        return result;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(
                     source,
                     "*",
                     SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static bool IsCurrentBuildSigned()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            var executable = Environment.ProcessPath;
            if (executable is null)
            {
                return false;
            }

#pragma warning disable SYSLIB0026
            using var certificate = new X509Certificate2(
                X509Certificate.CreateFromSignedFile(executable));
#pragma warning restore SYSLIB0026
            return certificate.Handle != IntPtr.Zero;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return false;
        }
    }

    private async Task<UpdateSettings> LoadSettingsAsync(
        CancellationToken cancellationToken) =>
        await _settingsStore.GetAsync<UpdateSettings>(SettingsKey, cancellationToken)
        ?? UpdateSettings.Default;

    private async Task<UpdateState> LoadStateAsync(
        CancellationToken cancellationToken) =>
        await _settingsStore.GetAsync<UpdateState>(StateKey, cancellationToken)
        ?? UpdateState.Default;

    private Task SaveStateAsync(
        UpdateState state,
        CancellationToken cancellationToken) =>
        _settingsStore.SetAsync(StateKey, state, cancellationToken);

    private async Task<UpdateApplyResult?> ReadLastResultAsync(
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(_storageOptions.DataRoot, "updates", "last-result.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken);
            return JsonSerializer.Deserialize<UpdateApplyResult>(
                json,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (Exception exception) when (
            exception is IOException or JsonException)
        {
            _logger.LogWarning(exception, "Could not read the last updater result.");
            return null;
        }
    }

    private static string SafeError(Exception exception)
    {
        var message = exception.Message.Replace('\r', ' ').Replace('\n', ' ');
        return message.Length <= 500 ? message : message[..500];
    }

    public sealed record UpdateSettings(
        ApplicationUpdateChannel Channel,
        bool AutomaticChecksEnabled)
    {
        public static UpdateSettings Default { get; } =
            new(ApplicationUpdateChannel.Stable, true);
    }

    public sealed record UpdateState(
        string? LatestVersion,
        ApplicationUpdateManifest? Manifest,
        string? ReleaseNotes,
        DateTimeOffset? LastCheckedAtUtc,
        ApplicationUpdateStage Stage,
        string? StagedPackagePath,
        string? RollbackStatus,
        string? LastError,
        IReadOnlyList<ApplicationUpdateHistoryEntry> History)
    {
        public static UpdateState Default { get; } = new(
            null,
            null,
            null,
            null,
            ApplicationUpdateStage.Idle,
            null,
            null,
            null,
            []);
    }
}
