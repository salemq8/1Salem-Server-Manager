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

    /// <summary>
    /// The port a tunnel should target: the server it was bound to if one was chosen, and
    /// otherwise the first server of that game, which is how single-server installations have
    /// always behaved.
    /// </summary>
    private static int? PortFor(
        IReadOnlyList<GameServerDefinition> servers,
        GameType game,
        Guid? boundServerId)
    {
        if (boundServerId is { } id)
        {
            var bound = servers.FirstOrDefault(server => server.Id == id);
            if (bound is not null)
            {
                return bound.Port;
            }
        }

        return servers.FirstOrDefault(server => server.Game == game)?.Port;
    }

    public async Task<PlayitStatusResponse> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        var installation = _locator.Detect();
        var settings = await LoadSettingsAsync(cancellationToken);
        var servers = await _serverStore.ListAsync(cancellationToken);

        // Several Minecraft servers can be registered, so remote access remembers which one
        // its tunnel points at. Until a server is chosen the first one is used, which is what
        // a single-server installation has always done, so existing tunnels do not move.
        var minecraftPort = PortFor(servers, GameType.Minecraft, settings.MinecraftServerId) ?? 25565;
        var palworldPort = PortFor(servers, GameType.Palworld, settings.PalworldServerId) ?? 8211;
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
            await PersistProcessIdentityAsync(process, cancellationToken);
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

            await ClearProcessIdentityAsync(cancellationToken);
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

    /// <summary>
    /// Detaches this instance's in-memory tracking of the managed Playit process WITHOUT
    /// stopping it -- the OS process keeps running untouched. This is the only thing an Agent
    /// host shutdown (a binary update, a service restart, a crash) is allowed to do to Playit;
    /// only an explicit <see cref="StopAsync"/> call (a real user/admin Stop-Playit action) may
    /// actually terminate it. The next Agent instance is expected to re-adopt the still-running
    /// process via <see cref="TryAdoptExistingAsync"/> rather than start a new one.
    /// </summary>
    public void ReleaseWithoutStopping()
    {
        if (_process is not null)
        {
            _process.Exited -= OnExited;
            _process.Dispose();
            _process = null;
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
        if (!settings.Enabled)
        {
            return;
        }

        // Re-adopt a Playit agent left running by a prior Agent instance (a binary update, a
        // service restart, or a crash never stops Playit -- see ReleaseWithoutStopping) before
        // ever considering starting a new one. StartAsync's own duplicate-prevention check
        // (existing.Count > 0 => RunningExternally, no Process.Start) is still the backstop if
        // adoption is skipped or declined here, so this can never result in two Playit
        // processes either way.
        if (await TryAdoptExistingAsync(cancellationToken))
        {
            return;
        }

        _ = await StartAsync(cancellationToken);
    }

    /// <summary>
    /// Attempts to re-adopt an already-running Playit agent instead of starting a new one.
    /// Returns true only if a process was actually adopted. Never adopts when more than one
    /// candidate process exists and none of them match a previously-recorded identity -- that
    /// is surfaced as an error rather than guessed at, and no process is started or stopped.
    /// </summary>
    public async Task<bool> TryAdoptExistingAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_process is { HasExited: false })
            {
                return true;
            }

            var installation = _locator.Detect();
            if (!installation.IsInstalled || installation.ExecutablePath is null)
            {
                return false;
            }

            var settings = await LoadSettingsAsync(cancellationToken);
            if (!settings.Enabled)
            {
                return false;
            }

            var candidateIds = _processDiscovery.FindRunningProcessIds(installation.ExecutablePath);
            if (candidateIds.Count == 0)
            {
                return false;
            }

            IPlayitProcess? adopted = candidateIds.Count == 1
                ? TryValidateAndAttach(candidateIds[0], settings)
                : settings.LastKnownProcessId is { } lastPid && candidateIds.Contains(lastPid)
                    ? TryValidateAndAttach(lastPid, settings)
                    : null;

            if (adopted is null)
            {
                if (candidateIds.Count > 1)
                {
                    _lastError =
                        "Multiple existing Playit processes were found and could not be " +
                        "safely disambiguated; none were adopted automatically.";
                    AddLog(_lastError);
                }

                return false;
            }

            adopted.Exited += OnExited;
            _process = adopted;
            _stopRequested = false;
            _lastError = null;
            _state = PlayitRuntimeState.Online;
            _linked = true;
            _verified = true;
            AddLog($"Re-adopted the already-running official Playit agent (PID {adopted.Id}).");
            await PersistProcessIdentityAsync(adopted, cancellationToken);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Attaches to the given PID and, only when a specific prior identity was recorded for that
    /// exact PID, confirms its start time still matches before trusting it -- protecting against
    /// Windows having recycled that PID for an unrelated process since it was last recorded. A
    /// PID with no prior recorded identity (e.g. the sole running candidate on first adoption
    /// ever) is trusted on executable-path identity alone, which FindRunningProcessIds already
    /// established.
    /// </summary>
    private IPlayitProcess? TryValidateAndAttach(int processId, PlayitSettings settings)
    {
        var process = _processFactory.Attach(processId);
        if (process is null)
        {
            return null;
        }

        if (settings.LastKnownProcessId == processId &&
            settings.LastKnownProcessStartTimeUtc is { } expectedStart)
        {
            var actualStart = process.StartTimeUtc;
            if (actualStart is null || !AreCloseEnough(expectedStart, actualStart.Value))
            {
                process.Dispose();
                return null;
            }
        }

        return process;
    }

    private static bool AreCloseEnough(DateTimeOffset expected, DateTimeOffset actual) =>
        (expected - actual).Duration() < TimeSpan.FromSeconds(2);

    private async Task PersistProcessIdentityAsync(
        IPlayitProcess process,
        CancellationToken cancellationToken)
    {
        try
        {
            var settings = await LoadSettingsAsync(cancellationToken);
            await _settingsStore.SetAsync(
                SettingsKey,
                settings with
                {
                    LastKnownProcessId = process.Id,
                    LastKnownProcessStartTimeUtc = process.StartTimeUtc
                },
                cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not persist the Playit process identity.");
        }
    }

    private async Task ClearProcessIdentityAsync(CancellationToken cancellationToken)
    {
        try
        {
            var settings = await LoadSettingsAsync(cancellationToken);
            if (settings.LastKnownProcessId is null &&
                settings.LastKnownProcessStartTimeUtc is null)
            {
                return;
            }

            await _settingsStore.SetAsync(
                SettingsKey,
                settings with
                {
                    LastKnownProcessId = null,
                    LastKnownProcessStartTimeUtc = null
                },
                cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not clear the persisted Playit process identity.");
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
        DateTimeOffset? LastConnectionAtUtc,
        int? LastKnownProcessId = null,
        DateTimeOffset? LastKnownProcessStartTimeUtc = null,

        // Which server each tunnel belongs to, now that several Minecraft servers can exist.
        // Null keeps the long-standing behaviour of using the first server of that game, so
        // an existing installation's tunnel is unaffected until someone chooses a server.
        Guid? MinecraftServerId = null,
        Guid? PalworldServerId = null)
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
