using System.Diagnostics;
using System.Text.Json;
using ServerManager.Contracts;
using ServerManager.Core;
using ServerManager.Infrastructure.Processes;
using ServerManager.Infrastructure.Playit;
using ServerManager.Infrastructure.Games.Minecraft;

namespace ServerManager.Agent;

public sealed class DashboardSnapshotService(
    AgentRuntimeState runtimeState,
    AgentOptions options,
    ISystemResourceReader resourceReader,
    IResourceGovernor resourceGovernor,
    INetworkService networkService,
    IGameServerStore serverStore,
    IBackupService backupService,
    IProcessSupervisor processSupervisor,
    ProcessSupervisor processLogs,
    OfficialPlayitSupervisor playitSupervisor,
    PalworldManagementCache palworldManagement,
    MinecraftPlayerStateService? minecraftPlayers = null)
{
    public async Task<DashboardSnapshot> GetAsync(
        CancellationToken cancellationToken = default)
    {
        var system = resourceReader.Capture(resourceGovernor.ActivePolicy);
        var network = await networkService.GetSnapshotAsync(cancellationToken);
        var servers = await serverStore.ListAsync(cancellationToken);
        var processes = (await processSupervisor.GetAllSnapshotsAsync(cancellationToken))
            .ToDictionary(snapshot => snapshot.ServerId);
        var playit = await playitSupervisor.GetStatusAsync(cancellationToken);
        var cards = new List<ServerDashboardCard>(servers.Count);
        foreach (var registeredServer in servers)
        {
            var server = registeredServer;
            processes.TryGetValue(server.Id, out var process);
            var state = process?.State ?? server.State;
            if (process is null &&
                state is ServerState.Running or
                    ServerState.Starting or
                    ServerState.Stopping or
                    ServerState.Restarting)
            {
                state = ServerState.Error;
                var reconciliationError =
                    "The registered process is no longer attached to the Agent. " +
                    (processLogs.GetRecentLogs(server.Id).LastOrDefault()?.Message ??
                     "No final console line was captured.");
                await serverStore.SetStateWithErrorAsync(
                    server.Id,
                    state,
                    reconciliationError,
                    cancellationToken);
                server = server with { State = state, LastError = reconciliationError };
            }
            else if (process is not null && server.State != state)
            {
                await serverStore.SetStateWithErrorAsync(
                    server.Id,
                    state,
                    server.LastError,
                    cancellationToken);
                server = server with { State = state };
            }

            var backups = await backupService.ListAsync(server.Id, cancellationToken);
            var lastBackup = backups
                .Where(backup => backup.Status == BackupStatus.Completed)
                .OrderByDescending(backup => backup.CreatedAtUtc)
                .FirstOrDefault();
            var logs = processLogs.GetRecentLogs(server.Id);
            var (activeMinimum, activeMaximum) = ReadActiveMemory(process?.Arguments);
            var management = server.Game == GameType.Palworld
                ? palworldManagement.Get(server.Id)
                : null;
            var players = server.Game == GameType.Minecraft && minecraftPlayers is not null
                ? await minecraftPlayers.GetAsync(server.Id, cancellationToken: cancellationToken)
                : null;
            var tunnel = server.Game == GameType.Palworld
                ? playit.Palworld
                : playit.Minecraft;
            var tunnelOnline =
                playit.IsRunning &&
                playit.IsLinked &&
                playit.IsVerified &&
                tunnel.IsConfigured &&
                tunnel.IsVerified;
            var localPortOpen = false;
            if (state == ServerState.Running)
            {
                var portStatus = await networkService.TestPortAsync(
                    server.Port,
                    server.Game == GameType.Palworld ? "UDP" : "TCP",
                    cancellationToken);
                localPortOpen = !portStatus.IsAvailable;
            }
            cards.Add(new ServerDashboardCard(
                server.Id,
                server.Game,
                Directory.Exists(server.RootPath),
                server.Name,
                state,
                network.LocalIpv4 is null
                    ? null
                    : $"{network.LocalIpv4}:{server.Port}",
                server.InstalledVersion,
                ReadRuntimeVersion(server),
                server.Game == GameType.Minecraft ? players?.OnlinePlayers : management?.PlayersOnline ?? ReadOnlinePlayers(server, logs),
                server.Game == GameType.Minecraft ? players?.MaxPlayers : management?.MaximumPlayers ?? ReadMaximumPlayers(server),
                process?.ProcessId,
                process?.CpuPercent ?? 0,
                process?.WorkingSetBytes ?? 0,
                process?.PrivateMemoryBytes ?? 0,
                process?.PeakWorkingSetBytes ?? 0,
                process is null
                    ? null
                    : DateTimeOffset.UtcNow - process.StartedAtUtc,
                lastBackup?.CreatedAtUtc ?? server.LastBackupAtUtc,
                server.UpdateStatus,
                server.Port,
                activeMinimum,
                activeMaximum,
                server.MinimumMemoryMb,
                server.MaximumMemoryMb,
                logs.LastOrDefault()?.Message,
                server.LastError,
                ServerActionPolicy.For(state, lastBackup is not null),
                server.AutoStart,
                server.AutoRestart,
                server.Priority,
                server.CpuAffinityMask,
                process?.GameProcessId,
                process?.ChildProcessCount ?? 0,
                process?.RootExecutableName,
                process?.GameExecutableName,
                tunnel.PublicAddress,
                playit.State.ToString(),
                tunnelOnline,
                tunnel.IsVerified,
                management,
                localPortOpen,
                playit.IsRunning && playit.IsLinked && playit.IsVerified,
                management?.State == PalworldManagementState.Online,
                process?.ThreadCount ?? 0,
                server.Game == GameType.Minecraft && (players?.IsStale ?? true),
                players?.LastVerifiedAtUtc));
        }

        var warnings = system.Warnings.ToList();
        warnings.AddRange(cards
            .Where(card => card.State is ServerState.Error or ServerState.Crashed)
            .Select(card => $"{card.Name}: {card.LastError ?? card.State.ToString()}"));
        return new DashboardSnapshot(
            runtimeState.ToStatus(options),
            network.LocalIpv4,
            system.TotalMemoryBytes,
            Math.Max(0, system.TotalMemoryBytes - system.AvailableMemoryBytes),
            system.AvailableMemoryBytes,
            system.CpuPercent,
            system.SystemDriveFreeBytes,
            Process.GetCurrentProcess().WorkingSet64,
            cards.Count(card => card.State is ServerState.Running or ServerState.Starting),
            warnings.Count,
            warnings,
            cards,
            DateTimeOffset.UtcNow,
            ResourceProfileSummary.Build(
                resourceGovernor.ActivePolicy,
                servers.Select(server => server.Game)),
            TimeSpan.FromMilliseconds(Environment.TickCount64));
    }

    private static string? ReadRuntimeVersion(GameServerDefinition server)
    {
        if (server.Game == GameType.Minecraft)
        {
            var metadataPath = Path.Combine(
                server.RootPath,
                ".1salem",
                "metadata.json");
            try
            {
                if (File.Exists(metadataPath))
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(metadataPath));
                    if (document.RootElement.TryGetProperty(
                            "requiredJavaMajor",
                            out var required))
                    {
                        return $"Java {required.GetInt32()}";
                    }
                }
            }
            catch (Exception exception) when (
                exception is IOException or JsonException)
            {
            }

            return string.IsNullOrWhiteSpace(server.JavaExecutablePath)
                ? null
                : Path.GetFileName(
                    Directory.GetParent(server.JavaExecutablePath)?.Parent?.FullName);
        }

        return "Native";
    }

    private static int? ReadMaximumPlayers(GameServerDefinition server)
    {
        if (server.Game != GameType.Minecraft)
        {
            return null;
        }

        var propertiesPath = Path.Combine(server.RootPath, "server.properties");
        try
        {
            if (!File.Exists(propertiesPath))
            {
                return null;
            }

            foreach (var line in File.ReadLines(propertiesPath))
            {
                if (line.StartsWith("max-players=", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(line["max-players=".Length..], out var maximum))
                {
                    return maximum;
                }
            }
        }
        catch (IOException)
        {
        }

        return null;
    }

    private static int? ReadOnlinePlayers(
        GameServerDefinition server,
        IReadOnlyList<LogEntry> logs)
    {
        if (server.Game != GameType.Minecraft)
        {
            return null;
        }

        var players = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in logs)
        {
            const string joinedMarker = " joined the game";
            const string leftMarker = " left the game";
            var joined = line.Message.IndexOf(
                joinedMarker,
                StringComparison.OrdinalIgnoreCase);
            if (joined > 0)
            {
                var player = LastToken(line.Message[..joined]);
                if (!string.IsNullOrWhiteSpace(player))
                {
                    players.Add(player);
                }
            }

            var left = line.Message.IndexOf(
                leftMarker,
                StringComparison.OrdinalIgnoreCase);
            if (left > 0)
            {
                var player = LastToken(line.Message[..left]);
                if (!string.IsNullOrWhiteSpace(player))
                {
                    players.Remove(player);
                }
            }
        }

        return players.Count;
    }

    private static string LastToken(string value)
    {
        var index = value.LastIndexOfAny([' ', ':', ']']);
        return index >= 0 ? value[(index + 1)..].Trim() : value.Trim();
    }

    private static (int? Minimum, int? Maximum) ReadActiveMemory(
        IReadOnlyList<string>? arguments)
    {
        if (arguments is null)
        {
            return (null, null);
        }

        return (
            ReadMemory(arguments, "-Xms"),
            ReadMemory(arguments, "-Xmx"));
    }

    private static int? ReadMemory(
        IReadOnlyList<string> arguments,
        string prefix)
    {
        var value = arguments.FirstOrDefault(
            argument => argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (value is null)
        {
            return null;
        }

        value = value[prefix.Length..];
        return value.EndsWith('M') &&
               int.TryParse(value[..^1], out var mebibytes)
            ? mebibytes
            : null;
    }
}
