using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ServerManager.Contracts;
using ServerManager.Core;

namespace ServerManager.Infrastructure.Playit;

public sealed class OfficialPlayitSupervisor : IDisposable
{
    public const string SettingsKey = "remote-access.playit.v1";
    private const int MaximumLogLines = 200;
    private readonly PlayitInstallationLocator _locator;
    private readonly IPlayitProcessFactory _processFactory;
    private readonly IPlayitProcessDiscovery _processDiscovery;
    private readonly ISettingsStore _settingsStore;
    private readonly IGameServerStore _serverStore;
    private readonly ILogger<OfficialPlayitSupervisor> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentQueue<string> _recentLog = new();
    private IPlayitProcess? _process;
    private PlayitRuntimeState _state = PlayitRuntimeState.Stopped;
    private string? _claimUrl;
    private string? _lastError;
    private DateTimeOffset? _lastConnectionAtUtc;
    private bool _linked;
    private bool _verified;
    private int _unexpectedExitCount;
    private bool _stopRequested;
    private bool _disposed;

    public OfficialPlayitSupervisor(
        PlayitInstallationLocator locator,
        IPlayitProcessFactory processFactory,
        IPlayitProcessDiscovery processDiscovery,
        ISettingsStore settingsStore,
        IGameServerStore serverStore,
        ILogger<OfficialPlayitSupervisor> logger)
    {
        _locator = locator;
        _processFactory = processFactory;
        _processDiscovery = processDiscovery;
        _settingsStore = settingsStore;
        _serverStore = serverStore;
        _logger = logger;
    }

    public async Task<PlayitStatusResponse> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        var installation = _locator.Detect();
        var settings = await LoadSettingsAsync(cancellationToken);
        var servers = await _serverStore.ListAsync(cancellationToken);
        var minecraftPort = servers.FirstOrDefault(item => item.Game == GameType.Minecraft)?.Port
            ?? 25565;
        var palworldPort = servers.FirstOrDefault(item => item.Game == GameType.Palworld)?.Port
            ?? 8211;
        var managedId = _process is { HasExited: false } ? _process.Id : (int?)null;
        var externalIds = installation.ExecutablePath is null
            ? []
            : _processDiscovery.FindRunningProcessIds(installation.ExecutablePath)
                .Where(id => id != managedId)
                .ToArray();
        var state = externalIds.Length > 0 && managedId is null
            ? PlayitRuntimeState.RunningExternally
            : installation.IsInstalled
                ? _state
                : PlayitRuntimeState.NotInstalled;
        int? processId = managedId ??
            (externalIds.Length > 0 ? externalIds[0] : null);

        var minecraft = CreateTunnel(
            GameType.Minecraft,
            PlayitTunnelProtocol.Tcp,
            minecraftPort,
            settings.MinecraftPublicAddress);
        var palworld = CreateTunnel(
            GameType.Palworld,
            PlayitTunnelProtocol.Udp,
            palworldPort,
            settings.PalworldPublicAddress ??
            PlayitSettings.Default.PalworldPublicAddress);
        return new PlayitStatusResponse(
            installation.IsInstalled,
            installation.ExecutablePath,
            installation.Version,
            state,
            managedId is not null || externalIds.Length > 0,
            _linked ||
            settings.LinkedPreviously ||
            (settings.SecretPath is not null && File.Exists(settings.SecretPath)),
            _verified && managedId is not null,
            settings.Enabled,
            managedId is not null,
            processId,
            settings.AgentName,
            _claimUrl,
            _lastConnectionAtUtc ?? settings.LastConnectionAtUtc,
            _lastError,
            minecraft,
            palworld,
            _recentLog.ToArray());
    }

    public async Task<PlayitActionResponse> DetectAsync(
        CancellationToken cancellationToken = default)
    {
        var status = await GetStatusAsync(cancellationToken);
        return new PlayitActionResponse(
            status.IsInstalled,
            status.IsInstalled
                ? $"Official Playit {status.Version ?? "agent"} detected."
                : "Playit is not installed. Use the official Playit download page.",
            status);
    }

    public async Task<PlayitActionResponse> SaveSettingsAsync(
        PlayitSettingsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidatePublicAddress(request.MinecraftPublicAddress, "Minecraft");
        ValidatePublicAddress(request.PalworldPublicAddress, "Palworld");
        var current = await LoadSettingsAsync(cancellationToken);
        var updated = current with
        {
            Enabled = request.Enabled,
            AgentName = Normalize(request.AgentName) ?? current.AgentName,
            MinecraftPublicAddress = Normalize(request.MinecraftPublicAddress),
            PalworldPublicAddress = Normalize(request.PalworldPublicAddress)
        };
        await _settingsStore.SetAsync(SettingsKey, updated, cancellationToken);
        var status = await GetStatusAsync(cancellationToken);
        return new PlayitActionResponse(true, "Remote access settings saved.", status);
    }

    public async Task<PlayitActionResponse> StartAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            var installation = _locator.Detect();
            if (!installation.IsInstalled || installation.ExecutablePath is null)
            {
                _state = PlayitRuntimeState.NotInstalled;
                return new PlayitActionResponse(
                    false,
                    "The official Playit agent is not installed.",
                    await GetStatusAsync(cancellationToken));
            }

            if (_process is { HasExited: false })
            {
                return new PlayitActionResponse(
                    true,
                    "Playit is already running under Server Manager.",
                    await GetStatusAsync(cancellationToken));
            }

            var existing = _processDiscovery.FindRunningProcessIds(installation.ExecutablePath);
            if (existing.Count > 0)
            {
                _state = PlayitRuntimeState.RunningExternally;
                return new PlayitActionResponse(
                    false,
                    "Playit is already running. Server Manager did not start a duplicate.",
                    await GetStatusAsync(cancellationToken));
            }

            var settings = await LoadSettingsAsync(cancellationToken);
            _process?.Dispose();
            _process = null;
            var startInfo = CreateStartInfo(
                installation.ExecutablePath,
                settings.SecretPath ?? PlayitInstallationLocator.FindExistingSecretPath(),
                SemanticVersion.TryParse(installation.Version, out var playitVersion) &&
                playitVersion.Major >= 1);
            _stopRequested = false;
            _lastError = null;
            _claimUrl = null;
            _linked = false;
            _verified = false;
            _state = PlayitRuntimeState.Starting;
            var process = _processFactory.Create(startInfo);
            process.OutputReceived += OnOutput;
            process.ErrorReceived += OnError;
            process.Exited += OnExited;
            process.Start();
            _process = process;
            AddLog("Official Playit agent started in hidden mode.");
            return new PlayitActionResponse(
                true,
                "Playit started.",
                await GetStatusAsync(cancellationToken));
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
            System.ComponentModel.Win32Exception or
            UnauthorizedAccessException)
        {
            _state = PlayitRuntimeState.Error;
            _lastError = PlayitOutputParser.Redact(exception.Message);
            return new PlayitActionResponse(
                false,
                "Playit could not be started.",
                await GetStatusAsync(cancellationToken));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PlayitActionResponse> StopAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _stopRequested = true;
            if (_process is not null)
            {
                _state = PlayitRuntimeState.Stopping;
                await _process.StopAsync(TimeSpan.FromSeconds(10), cancellationToken);
                _process.Dispose();
                _process = null;
            }

            _state = PlayitRuntimeState.Stopped;
            _verified = false;
            return new PlayitActionResponse(
                true,
                "Playit stopped. Local game servers remain available.",
                await GetStatusAsync(cancellationToken));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PlayitActionResponse> RestartAsync(
        CancellationToken cancellationToken = default)
    {
        await StopAsync(cancellationToken);
        return await StartAsync(cancellationToken);
    }

    public async Task<bool> RecoverIfNeededAsync(
        CancellationToken cancellationToken = default)
    {
        var settings = await LoadSettingsAsync(cancellationToken);
        if (!settings.Enabled ||
            _stopRequested ||
            _process is { HasExited: false } ||
            _state == PlayitRuntimeState.RunningExternally)
        {
            return false;
        }

        if (_unexpectedExitCount >= 5)
        {
            _lastError = "Playit stopped repeatedly; automatic restart is paused.";
            _state = PlayitRuntimeState.Error;
            return false;
        }

        await Task.Delay(
            TimeSpan.FromSeconds(Math.Min(30, Math.Max(2, _unexpectedExitCount * 3))),
            cancellationToken);
        var result = await StartAsync(cancellationToken);
        return result.Success;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var current = await _settingsStore.GetAsync<PlayitSettings>(
            SettingsKey,
            cancellationToken);
        if (current is null)
        {
            await _settingsStore.SetAsync(
                SettingsKey,
                PlayitSettings.Default with
                {
                    SecretPath = PlayitInstallationLocator.FindExistingSecretPath()
                },
                cancellationToken);
        }

        var settings = await LoadSettingsAsync(cancellationToken);
        if (settings.Enabled)
        {
            _ = await StartAsync(cancellationToken);
        }
    }

    public static ProcessStartInfo CreateStartInfo(
        string executablePath,
        string? secretPath,
        bool useKebabCaseOptions = false)
    {
        var information = new ProcessStartInfo
        {
            FileName = Path.GetFullPath(executablePath),
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executablePath))!,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false
        };
        if (!string.IsNullOrWhiteSpace(secretPath))
        {
            information.ArgumentList.Add(
                useKebabCaseOptions ? "--secret-path" : "--secret_path");
            information.ArgumentList.Add(Path.GetFullPath(secretPath));
        }

        information.ArgumentList.Add("--stdout");
        information.ArgumentList.Add("start");
        return information;
    }

    private void OnOutput(string line) => ProcessLine(line, false);

    private void OnError(string line) => ProcessLine(line, true);

    private void ProcessLine(string line, bool error)
    {
        var claim = PlayitOutputParser.FindClaimUrl(line);
        if (claim is not null)
        {
            _claimUrl = claim;
            _state = PlayitRuntimeState.WaitingForLink;
        }

        if (PlayitOutputParser.IndicatesLinked(line))
        {
            _linked = true;
        }

        if (PlayitOutputParser.IndicatesVerified(line))
        {
            _linked = true;
            _verified = true;
            _state = PlayitRuntimeState.Online;
            _lastConnectionAtUtc = DateTimeOffset.UtcNow;
            _unexpectedExitCount = 0;
            _ = PersistConnectionAsync();
        }

        var safeLine = PlayitOutputParser.Redact(line);
        AddLog(error ? $"error: {safeLine}" : safeLine);
        if (error &&
            (line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
             line.Contains("failed", StringComparison.OrdinalIgnoreCase)))
        {
            _lastError = safeLine;
        }
    }

    private void OnExited(int exitCode)
    {
        _verified = false;
        if (_stopRequested)
        {
            _state = PlayitRuntimeState.Stopped;
            return;
        }

        _unexpectedExitCount++;
        _state = PlayitRuntimeState.Error;
        _lastError = $"Playit exited unexpectedly with code {exitCode}.";
        AddLog(_lastError);
    }

    private async Task PersistConnectionAsync()
    {
        try
        {
            var settings = await LoadSettingsAsync(CancellationToken.None);
            await _settingsStore.SetAsync(
                SettingsKey,
                settings with
                {
                    LinkedPreviously = true,
                    LastConnectionAtUtc = _lastConnectionAtUtc
                },
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not persist Playit connection status.");
        }
    }

    private PlayitTunnelStatus CreateTunnel(
        GameType game,
        PlayitTunnelProtocol protocol,
        int port,
        string? publicAddress)
    {
        var error = PlayitTunnelPolicy.Validate(
            game,
            protocol,
            PlayitTunnelPolicy.LoopbackHost,
            port,
            port);
        var configured = !string.IsNullOrWhiteSpace(publicAddress);
        return new PlayitTunnelStatus(
            game,
            protocol,
            PlayitTunnelPolicy.LoopbackHost,
            port,
            publicAddress,
            configured,
            configured && _verified && error is null,
            error ?? (configured
                ? _verified
                    ? "Mapping matches the required local target."
                    : "Saved mapping; start Playit to verify connectivity."
                : "Create this tunnel in the official Playit portal, then save its public address."));
    }

    private async Task<PlayitSettings> LoadSettingsAsync(
        CancellationToken cancellationToken) =>
        await _settingsStore.GetAsync<PlayitSettings>(SettingsKey, cancellationToken)
        ?? PlayitSettings.Default;

    private void AddLog(string line)
    {
        _recentLog.Enqueue(PlayitOutputParser.Redact(line));
        while (_recentLog.Count > MaximumLogLines &&
               _recentLog.TryDequeue(out _))
        {
        }
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void ValidatePublicAddress(string? address, string game)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return;
        }

        var normalized = address.Trim();
        if (normalized.Length > 255 ||
            normalized.Contains("://", StringComparison.Ordinal) ||
            normalized.Any(char.IsWhiteSpace) ||
            normalized.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) ||
                  character is '.' or '-' or ':' or '[' or ']')))
        {
            throw new ArgumentException(
                $"{game} public address must be a host name with an optional port.",
                nameof(address));
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _process?.Dispose();
        _gate.Dispose();
    }

    public sealed record PlayitSettings(
        bool Enabled,
        string? AgentName,
        string? MinecraftPublicAddress,
        string? PalworldPublicAddress,
        string? SecretPath,
        bool LinkedPreviously,
        DateTimeOffset? LastConnectionAtUtc)
    {
        public static PlayitSettings Default { get; } = new(
            false,
            "ALSarabeetMC",
            "click-jackets.gl.joinmc.link",
            "click-jackets.gl.at.ply.gg:7551",
            null,
            false,
            null);
    }
}
