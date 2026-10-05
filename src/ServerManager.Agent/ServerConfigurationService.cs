using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ServerManager.Contracts;
using ServerManager.Core;
using ServerManager.Infrastructure.Games.Minecraft;
using ServerManager.Infrastructure.Games.Palworld;
using ServerManager.Infrastructure.Processes;

namespace ServerManager.Agent;

public sealed partial class ServerConfigurationService(
    IGameServerStore serverStore,
    ISystemResourceReader resourceReader,
    IResourceGovernor resourceGovernor,
    IJavaRuntimeLocator javaRuntimeLocator,
    INetworkService networkService,
    ISecretStore secretStore,
    GameServerOrchestrator orchestrator,
    ProcessSupervisor processSupervisor,
    ConfigurationRestorePointService restorePoints)
{
    public async Task<MinecraftConfigurationResponse> GetMinecraftAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
    {
        var server = await GetMinecraftServerAsync(serverId, cancellationToken);
        var values = ReadProperties(server.RootPath);
        var levelName = ReadString(values, "level-name", "world");
        return new MinecraftConfigurationResponse(
            server.Id,
            server.Name,
            server.Port,
            server.MinimumMemoryMb ?? 1024,
            server.MaximumMemoryMb ?? 4096,
            server.JavaExecutablePath,
            ReadInt(values, "max-players", 20),
            ReadString(values, "motd", "A 1Salem Minecraft Server"),
            ReadString(values, "gamemode", "survival"),
            ReadString(values, "difficulty", "normal"),
            ReadBool(values, "white-list", false),
            ReadInt(values, "view-distance", 10),
            ReadInt(values, "simulation-distance", 10),
            ReadBool(values, "online-mode", true),
            ReadBool(values, "hardcore", false),
            ReadBool(values, "pvp", true),
            server.AutoStart,
            server.AutoRestart,
            server.PreferredAdapterId,
            ReadInt(values, "spawn-protection", 16),
            ReadBool(values, "enable-command-block", false),
            ReadBool(values, "allow-flight", false),
            ReadBool(values, "spawn-animals", true),
            ReadBool(values, "spawn-monsters", true),
            ReadBool(values, "spawn-npcs", true),
            levelName,
            ReadString(values, "level-seed", string.Empty),
            ReadString(values, "level-type", "minecraft:normal"),
            ReadBool(values, "generate-structures", true),
            !File.Exists(Path.Combine(
                server.RootPath,
                levelName,
                "level.dat")),
            values.Keys
                .Where(key =>
                    !MinecraftPropertiesSerializer.ManagedKeys.Contains(key))
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray());
    }

    public async Task<OperationResult> UpdateMinecraftMemoryAsync(
        Guid serverId,
        MinecraftMemoryUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        var server = await GetMinecraftServerAsync(serverId, cancellationToken);
        var system = resourceReader.Capture(resourceGovernor.ActivePolicy);
        var recommendation = MinecraftMemoryPolicy.Evaluate(
            system.TotalMemoryBytes,
            system.AvailableMemoryBytes,
            request.MinimumMemoryMb,
            request.MaximumMemoryMb);
        if (!recommendation.IsSafe)
        {
            return OperationResult.Fail(
                "UnsafeMemoryAllocation",
                string.Join(" ", recommendation.Warnings));
        }

        var restorePoint = await restorePoints.CreateAsync(
            server,
            "minecraft-memory",
            ["user_jvm_args.txt"],
            new Dictionary<string, string>
            {
                ["Xms"] = $"{server.MinimumMemoryMb ?? 1024} MB → {request.MinimumMemoryMb} MB",
                ["Xmx"] = $"{server.MaximumMemoryMb ?? 4096} MB → {request.MaximumMemoryMb} MB"
            },
            cancellationToken);
        try
        {
            var argumentsPath = Path.Combine(
                server.RootPath,
                "user_jvm_args.txt");
            var existingArguments = File.Exists(argumentsPath)
                ? await File.ReadAllTextAsync(argumentsPath, cancellationToken)
                : string.Empty;
            await WriteManagedFileAsync(
                server.RootPath,
                "user_jvm_args.txt",
                MergeMinecraftMemoryArguments(
                    existingArguments,
                    request.MinimumMemoryMb,
                    request.MaximumMemoryMb),
                cancellationToken);
            var updated = server with
            {
                MinimumMemoryMb = request.MinimumMemoryMb,
                MaximumMemoryMb = request.MaximumMemoryMb
            };
            await serverStore.UpsertAsync(updated, server.State, cancellationToken);
            var restarted = MinecraftMemoryPolicy.ShouldApplyRestart(
                request.ApplyAndRestart,
                await IsRunningAsync(server.Id, cancellationToken));
            if (restarted)
            {
                _ = await orchestrator.RestartAsync(server.Id, cancellationToken);
            }

            await restorePoints.CompleteAsync(
                server.Id,
                restorePoint.Id,
                restarted,
                cancellationToken);
            return OperationResult.Ok();
        }
        catch (Exception exception)
        {
            return await RecoverConfigurationFailureAsync(
                server.Id,
                restorePoint.Id,
                "MinecraftMemoryApplyFailed",
                exception,
                cancellationToken);
        }
    }

    public async Task<OperationResult> UpdateMinecraftSettingsAsync(
        Guid serverId,
        ServerSettingsUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        var server = await GetMinecraftServerAsync(serverId, cancellationToken);
        var settings = new MinecraftServerSettings(
            request.Motd,
            request.MaxPlayers,
            request.Difficulty,
            request.GameMode,
            request.OnlineMode,
            request.ViewDistance,
            request.SimulationDistance,
            request.WhitelistEnabled,
            request.Hardcore,
            request.Pvp,
            request.SpawnProtection,
            request.EnableCommandBlocks,
            request.AllowFlight,
            request.SpawnAnimals,
            request.SpawnMonsters,
            request.SpawnNpcs,
            request.LevelName,
            request.LevelSeed,
            request.LevelType,
            request.GenerateStructures);
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return OperationResult.Fail(
                "ServerNameRequired",
                "Enter a server name before saving.");
        }

        try
        {
            _ = MinecraftPropertiesSerializer.Serialize(settings, request.Port);
        }
        catch (ArgumentException exception)
        {
            return OperationResult.Fail(
                "InvalidMinecraftSetting",
                exception.Message);
        }
        var currentProperties = ReadProperties(server.RootPath);
        var currentLevelName = ReadString(
            currentProperties,
            "level-name",
            "world");
        var currentSeed = ReadString(
            currentProperties,
            "level-seed",
            string.Empty);
        if (File.Exists(Path.Combine(
                server.RootPath,
                currentLevelName,
                "level.dat")) &&
            !request.LevelSeed.Equals(currentSeed, StringComparison.Ordinal))
        {
            return OperationResult.Fail(
                "ExistingWorldSeedLocked",
                "The seed can only be changed before the selected world is created. Keep the current seed or choose a new level name.");
        }
        if (request.Port != server.Port)
        {
            var port = await networkService.TestPortAsync(
                request.Port,
                "TCP",
                cancellationToken);
            if (!port.IsAvailable)
            {
                return OperationResult.Fail("PortAlreadyInUse", port.Message);
            }
        }

        var javaPath = server.JavaExecutablePath;
        if (!string.IsNullOrWhiteSpace(request.JavaExecutablePath))
        {
            var java = await javaRuntimeLocator.InspectAsync(
                request.JavaExecutablePath,
                cancellationToken);
            if (java is null)
            {
                return OperationResult.Fail(
                    "InvalidJavaPath",
                    "The selected Java executable could not report a valid Java version.");
            }

            javaPath = java.ExecutablePath;
        }

        var restorePaths = new List<string> { "server.properties" };
        if (!string.IsNullOrWhiteSpace(javaPath))
        {
            restorePaths.Add(Path.Combine(".1salem", "java-path.txt"));
        }

        var restorePoint = await restorePoints.CreateAsync(
            server,
            "minecraft-settings",
            restorePaths,
            new Dictionary<string, string>
            {
                ["Server name"] = $"{server.Name} → {request.Name.Trim()}",
                ["Port"] = $"{server.Port} → {request.Port}",
                ["Maximum players"] = request.MaxPlayers.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                ["MOTD"] = request.Motd,
                ["Game mode"] = request.GameMode,
                ["Difficulty"] = request.Difficulty
            },
            cancellationToken);
        try
        {
            var propertiesPath = Path.Combine(server.RootPath, "server.properties");
            var existing = File.Exists(propertiesPath)
                ? await File.ReadAllTextAsync(propertiesPath, cancellationToken)
                : string.Empty;
            await WriteManagedFileAsync(
                server.RootPath,
                "server.properties",
                MinecraftPropertiesSerializer.Merge(
                    existing,
                    settings,
                    request.Port),
                cancellationToken);
            var verifiedProperties = MinecraftPropertiesSerializer.Parse(
                await File.ReadAllTextAsync(propertiesPath, cancellationToken));
            if (ReadInt(verifiedProperties, "server-port", 0) != request.Port ||
                ReadInt(verifiedProperties, "max-players", 0) !=
                request.MaxPlayers)
            {
                throw new InvalidDataException(
                    "The staged Minecraft settings did not pass read-back verification.");
            }

            if (!string.IsNullOrWhiteSpace(javaPath))
            {
                await WriteManagedFileAsync(
                    server.RootPath,
                    Path.Combine(".1salem", "java-path.txt"),
                    javaPath,
                    cancellationToken);
            }

            var updated = server with
            {
                Name = request.Name.Trim(),
                Port = request.Port,
                JavaExecutablePath = javaPath
            };
            await serverStore.UpsertAsync(updated, server.State, cancellationToken);
            var restarted = request.ApplyAndRestart &&
                await IsRunningAsync(server.Id, cancellationToken);
            if (restarted)
            {
                _ = await orchestrator.RestartAsync(server.Id, cancellationToken);
            }

            await restorePoints.CompleteAsync(
                server.Id,
                restorePoint.Id,
                restarted,
                cancellationToken);
            return OperationResult.Ok();
        }
        catch (Exception exception)
        {
            return await RecoverConfigurationFailureAsync(
                server.Id,
                restorePoint.Id,
                "MinecraftSettingsApplyFailed",
                exception,
                cancellationToken);
        }
    }

    public async Task<OperationResult> UpdateAutomationAsync(
        Guid serverId,
        ServerAutomationUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        var server = await serverStore.GetAsync(serverId, cancellationToken);
        if (server is null)
        {
            return OperationResult.Fail("ServerNotFound", "The server is not registered.");
        }

        await serverStore.UpsertAsync(
            server with
            {
                AutoStart = request.AutoStart,
                AutoRestart = request.AutoRestart
            },
            server.State,
            cancellationToken);
        return OperationResult.Ok();
    }

    public async Task<PalworldConfigurationResponse> GetPalworldAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
    {
        var server = await GetPalworldServerAsync(serverId, cancellationToken);
        var metadata = await ReadPalworldMetadataAsync(
            server.RootPath,
            cancellationToken);
        var iniEnabled = false;
        var iniPort = metadata.Settings.RestApiPort;
        string? configurationPath = null;
        try
        {
            var document = await PalworldConfigurationFile.ReadAsync(
                server.RootPath,
                cancellationToken);
            var values = PalworldSettingsSerializer.ParseValues(document.Content);
            configurationPath = document.Path;
            iniEnabled = values.TryGetValue("RESTAPIEnabled", out var enabled) &&
                         bool.TryParse(enabled, out var parsedEnabled) &&
                         parsedEnabled;
            if (values.TryGetValue("RESTAPIPort", out var port) &&
                int.TryParse(
                    port,
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var parsedPort))
            {
                iniPort = parsedPort;
            }
        }
        catch (FileNotFoundException)
        {
            configurationPath = PalworldConfigurationFile.ResolvePath(
                server.RootPath);
        }

        return new PalworldConfigurationResponse(
            server.Id,
            metadata.Settings.ServerName,
            metadata.Settings.Description,
            metadata.Settings.MaxPlayers,
            metadata.Settings.Port,
            metadata.Settings.CommunityServer,
            metadata.Settings.RconEnabled,
            metadata.Settings.RconPort,
            HasProtectedValue(metadata.ProtectedServerPassword),
            HasProtectedValue(metadata.ProtectedAdminPassword),
            server.AutoStart,
            server.AutoRestart,
            metadata.Settings.RestApiEnabled,
            metadata.Settings.RestApiPort,
            iniEnabled,
            iniPort,
            iniEnabled != metadata.Settings.RestApiEnabled ||
            iniPort != metadata.Settings.RestApiPort,
            configurationPath);
    }

    public async Task<OperationResult> UpdatePalworldSettingsAsync(
        Guid serverId,
        PalworldSettingsUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        var server = await GetPalworldServerAsync(serverId, cancellationToken);
        var metadata = await ReadPalworldMetadataAsync(
            server.RootPath,
            cancellationToken);
        var currentServerPassword = secretStore.Unprotect(
            metadata.ProtectedServerPassword);
        var currentAdminPassword = secretStore.Unprotect(
            metadata.ProtectedAdminPassword);
        var settings = new PalworldServerSettings(
            request.ServerName,
            request.Description,
            string.IsNullOrEmpty(request.NewServerPassword)
                ? currentServerPassword
                : request.NewServerPassword,
            string.IsNullOrEmpty(request.NewAdminPassword)
                ? currentAdminPassword
                : request.NewAdminPassword,
            request.MaxPlayers,
            request.Port,
            request.CommunityServer,
            request.RconEnabled,
            request.RconPort,
            metadata.Settings.RestApiEnabled,
            metadata.Settings.RestApiPort);
        try
        {
            PalworldSettingsSerializer.Validate(settings);
        }
        catch (ArgumentException exception)
        {
            return OperationResult.Fail(
                "InvalidPalworldSetting",
                exception.Message);
        }
        if (request.Port != server.Port)
        {
            var port = await networkService.TestPortAsync(
                request.Port,
                "UDP",
                cancellationToken);
            if (!port.IsAvailable)
            {
                return OperationResult.Fail("PortAlreadyInUse", port.Message);
            }
        }

        var updatedMetadata = metadata with
        {
            ProtectedServerPassword =
                secretStore.Protect(settings.ServerPassword),
            ProtectedAdminPassword =
                secretStore.Protect(settings.AdminPassword),
            Settings = new PalworldServerSettingsTemplate(
                settings.ServerName,
                settings.Description,
                settings.MaxPlayers,
                settings.Port,
                settings.CommunityServer,
                settings.RconEnabled,
                settings.RconPort,
                settings.RestApiEnabled,
                settings.RestApiPort)
        };
        var metadataRelativePath = Path.Combine(".1salem", "metadata.json");
        var configurationRelativePath =
            PalworldConfigurationFile.RelativePath;
        var restorePoint = await restorePoints.CreateAsync(
            server,
            "palworld-settings",
            [metadataRelativePath, configurationRelativePath],
            new Dictionary<string, string>
            {
                ["Server name"] = $"{server.Name} → {settings.ServerName}",
                ["Description"] = settings.Description,
                ["Port"] = $"{server.Port} → {settings.Port}",
                ["Maximum players"] = settings.MaxPlayers.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                ["Community server"] = settings.CommunityServer.ToString(),
                ["Server password"] = string.IsNullOrEmpty(request.NewServerPassword)
                    ? "unchanged"
                    : "changed",
                ["Admin password"] = string.IsNullOrEmpty(request.NewAdminPassword)
                    ? "unchanged"
                    : "changed"
            },
            cancellationToken);
        try
        {
            await WriteManagedFileAsync(
                server.RootPath,
                metadataRelativePath,
                JsonSerializer.Serialize(
                    updatedMetadata,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)
                    {
                        WriteIndented = true
                    }),
                cancellationToken);
            var configurationPath =
                PalworldConfigurationFile.ResolvePath(server.RootPath);
            if (File.Exists(configurationPath))
            {
                var document = await PalworldConfigurationFile.ReadAsync(
                    server.RootPath,
                    cancellationToken);
                await PalworldConfigurationFile.WriteAtomicAsync(
                    document,
                    PalworldSettingsSerializer.Merge(
                        document.Content,
                        settings),
                    cancellationToken);
            }
            else
            {
                await WriteManagedFileAsync(
                    server.RootPath,
                    configurationRelativePath,
                    PalworldSettingsSerializer.Serialize(settings),
                    cancellationToken);
            }

            var verifiedDocument = await PalworldConfigurationFile.ReadAsync(
                server.RootPath,
                cancellationToken);
            var verifiedValues = PalworldSettingsSerializer.ParseValues(
                verifiedDocument.Content);
            if (!verifiedValues.TryGetValue("ServerName", out var serverName) ||
                !PalworldSettingsSerializer.ReadText(serverName).Equals(
                    settings.ServerName,
                    StringComparison.Ordinal) ||
                !verifiedValues.TryGetValue("PublicPort", out var publicPort) ||
                !int.TryParse(
                    publicPort,
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var verifiedPort) ||
                verifiedPort != settings.Port)
            {
                throw new InvalidDataException(
                    "The Palworld configuration did not pass read-back verification.");
            }

            await serverStore.UpsertAsync(
                server with
                {
                    Name = settings.ServerName,
                    Port = settings.Port
                },
                server.State,
                cancellationToken);
            var restarted = request.ApplyAndRestart &&
                await IsRunningAsync(server.Id, cancellationToken);
            if (restarted)
            {
                _ = await orchestrator.RestartAsync(server.Id, cancellationToken);
            }

            await restorePoints.CompleteAsync(
                server.Id,
                restorePoint.Id,
                restarted,
                cancellationToken);
            return OperationResult.Ok();
        }
        catch (Exception exception)
        {
            return await RecoverConfigurationFailureAsync(
                server.Id,
                restorePoint.Id,
                "PalworldSettingsApplyFailed",
                exception,
                cancellationToken);
        }
    }

    public async Task<MinecraftPlayersSnapshot> GetMinecraftPlayersAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
    {
        var server = await GetMinecraftServerAsync(serverId, cancellationToken);
        var online = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in processSupervisor.GetRecentLogs(serverId))
        {
            var joined = JoinedPlayerRegex().Match(entry.Message);
            if (joined.Success)
            {
                online.Add(joined.Groups["player"].Value);
            }

            var left = LeftPlayerRegex().Match(entry.Message);
            if (left.Success)
            {
                online.Remove(left.Groups["player"].Value);
            }
        }

        return new MinecraftPlayersSnapshot(
            online.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            ReadJsonNames(server.RootPath, "whitelist.json", "name"),
            ReadJsonNames(server.RootPath, "ops.json", "name"),
            ReadJsonNames(server.RootPath, "banned-players.json", "name"),
            ReadJsonNames(server.RootPath, "banned-ips.json", "ip"),
            DateTimeOffset.UtcNow);
    }

    private async Task<GameServerDefinition> GetMinecraftServerAsync(
        Guid serverId,
        CancellationToken cancellationToken)
    {
        var server = await serverStore.GetAsync(serverId, cancellationToken)
            ?? throw new KeyNotFoundException($"Server {serverId} is not registered.");
        if (server.Game != GameType.Minecraft)
        {
            throw new ArgumentException("The selected server is not Minecraft.", nameof(serverId));
        }

        return server;
    }

    private async Task<GameServerDefinition> GetPalworldServerAsync(
        Guid serverId,
        CancellationToken cancellationToken)
    {
        var server = await serverStore.GetAsync(serverId, cancellationToken)
            ?? throw new KeyNotFoundException($"Server {serverId} is not registered.");
        if (server.Game != GameType.Palworld)
        {
            throw new ArgumentException("The selected server is not Palworld.", nameof(serverId));
        }

        return server;
    }

    private static async Task<PalworldServerMetadata> ReadPalworldMetadataAsync(
        string rootPath,
        CancellationToken cancellationToken)
    {
        var path = SafePathPolicy.ResolveWithinRoot(
            rootPath,
            Path.Combine(".1salem", "metadata.json"));
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<PalworldServerMetadata>(
            stream,
            new JsonSerializerOptions(JsonSerializerDefaults.Web),
            cancellationToken)
            ?? throw new InvalidDataException("Managed Palworld metadata is invalid.");
    }

    private bool HasProtectedValue(string value)
    {
        try
        {
            return !string.IsNullOrEmpty(secretStore.Unprotect(value));
        }
        catch (Exception exception) when (
            exception is System.Security.Cryptography.CryptographicException or
            FormatException)
        {
            return false;
        }
    }

    private async Task<bool> IsRunningAsync(
        Guid serverId,
        CancellationToken cancellationToken) =>
        await processSupervisor.GetSnapshotAsync(serverId, cancellationToken) is not null;

    private async Task<OperationResult> RecoverConfigurationFailureAsync(
        Guid serverId,
        string restorePointId,
        string errorCode,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var recovery = await restorePoints.RestoreAsync(
            serverId,
            new ConfigurationRestorePointRestoreRequest(
                restorePointId,
                ConfirmRestart: true),
            cancellationToken);
        return OperationResult.Fail(
            errorCode,
            recovery.Success
                ? $"The settings operation failed and the previous known-working configuration was restored: {exception.Message}"
                : $"The settings operation failed: {exception.Message} Automatic recovery also failed: {recovery.Message}");
    }

    private static IReadOnlyDictionary<string, string> ReadProperties(string rootPath)
    {
        var path = Path.Combine(rootPath, "server.properties");
        return File.Exists(path)
            ? MinecraftPropertiesSerializer.Parse(MinecraftPropertiesSerializer.ReadFile(path))
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    internal static string MergeMinecraftMemoryArguments(
        string existing,
        int minimumMemoryMb,
        int maximumMemoryMb)
    {
        var lines = existing
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line =>
                !line.StartsWith("-Xms", StringComparison.OrdinalIgnoreCase) &&
                !line.StartsWith("-Xmx", StringComparison.OrdinalIgnoreCase))
            .ToList();
        lines.Insert(0, $"-Xmx{maximumMemoryMb}M");
        lines.Insert(0, $"-Xms{minimumMemoryMb}M");
        return string.Join(Environment.NewLine, lines) +
               Environment.NewLine;
    }

    private static async Task WriteManagedFileAsync(
        string rootPath,
        string relativePath,
        string content,
        CancellationToken cancellationToken)
    {
        var target = SafePathPolicy.ResolveWithinRoot(rootPath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (File.Exists(target))
        {
            var backupRoot = Path.Combine(
                rootPath,
                "backups",
                "config-edits",
                DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmssfff"));
            Directory.CreateDirectory(backupRoot);
            File.Copy(
                target,
                Path.Combine(backupRoot, Path.GetFileName(target)),
                false);
        }

        var temporary = $"{target}.1salem-{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporary,
                content,
                new UTF8Encoding(false),
                cancellationToken);
            File.Move(temporary, target, true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static IReadOnlyList<string> ReadJsonNames(
        string rootPath,
        string fileName,
        string propertyName)
    {
        var path = Path.Combine(rootPath, fileName);
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray()
                    .Where(item =>
                        item.ValueKind == JsonValueKind.Object &&
                        item.TryGetProperty(propertyName, out _))
                    .Select(item => item.GetProperty(propertyName).GetString())
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Cast<string>()
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToArray()
                : [];
        }
        catch (Exception exception) when (
            exception is IOException or JsonException)
        {
            return [];
        }
    }

    private static int ReadInt(
        IReadOnlyDictionary<string, string> values,
        string key,
        int fallback) =>
        values.TryGetValue(key, out var value) && int.TryParse(value, out var parsed)
            ? parsed
            : fallback;

    private static bool ReadBool(
        IReadOnlyDictionary<string, string> values,
        string key,
        bool fallback) =>
        values.TryGetValue(key, out var value) && bool.TryParse(value, out var parsed)
            ? parsed
            : fallback;

    private static string ReadString(
        IReadOnlyDictionary<string, string> values,
        string key,
        string fallback) =>
        values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : fallback;

    [GeneratedRegex(@"(?<player>[A-Za-z0-9_]{1,16}) joined the game")]
    private static partial Regex JoinedPlayerRegex();

    [GeneratedRegex(@"(?<player>[A-Za-z0-9_]{1,16}) left the game")]
    private static partial Regex LeftPlayerRegex();
}
