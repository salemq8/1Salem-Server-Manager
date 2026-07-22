using System.Text.Json;
using ServerManager.Contracts;
using ServerManager.Core;

namespace ServerManager.Infrastructure.Games.Palworld;

public sealed class PalworldServerProvider(ISecretStore secretStore) : IGameServerProvider
{
    public GameType Game => GameType.Palworld;

    public async Task<InstallationDetection> DetectInstallationAsync(
        string rootPath,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(rootPath);
        if (!Directory.Exists(root))
        {
            return new InstallationDetection(false, root, null, [], []);
        }

        var detected = new List<string>();
        if (File.Exists(Path.Combine(root, "PalServer.exe")))
        {
            detected.Add("PalServer.exe");
        }

        if (Directory.Exists(Path.Combine(root, "Pal")))
        {
            detected.Add("Pal");
        }

        if (Directory.Exists(Path.Combine(root, "Pal", "Saved")))
        {
            detected.Add(Path.Combine("Pal", "Saved"));
        }

        var metadata = await ReadMetadataAsync(root, cancellationToken);
        var installed = detected.Contains("PalServer.exe", StringComparer.OrdinalIgnoreCase) &&
                        detected.Contains("Pal", StringComparer.OrdinalIgnoreCase);
        return new InstallationDetection(
            installed,
            root,
            metadata?.BuildId,
            detected,
            installed ? [] : ["PalServer.exe and the Pal folder were not both detected."]);
    }

    public ProcessLaunchSpec CreateLaunchSpec(GameServerDefinition server)
    {
        if (server.Game != GameType.Palworld)
        {
            throw new ArgumentException("The server definition is not Palworld.", nameof(server));
        }

        var executable = Path.Combine(server.RootPath, "PalServer.exe");
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException("PalServer.exe is missing.", executable);
        }

        var metadata = ReadMetadata(server.RootPath);
        var argumentList = new List<string>
        {
            $"-port={metadata.Settings.Port}",
            $"-players={metadata.Settings.MaxPlayers}",
            "-logformat=json"
        };
        if (metadata.Settings.CommunityServer)
        {
            argumentList.Add("-publiclobby");
        }

        return new ProcessLaunchSpec(
            executable,
            string.Join(' ', argumentList),
            server.RootPath,
            new Dictionary<string, string>(),
            ArgumentList: argumentList);
    }

    public async Task PrepareForStartAsync(
        GameServerDefinition server,
        CancellationToken cancellationToken = default)
    {
        var metadata = await ReadMetadataAsync(server.RootPath, cancellationToken)
            ?? throw new InvalidDataException("Managed Palworld metadata is missing.");
        var settings = new PalworldServerSettings(
            metadata.Settings.ServerName,
            metadata.Settings.Description,
            secretStore.Unprotect(metadata.ProtectedServerPassword),
            secretStore.Unprotect(metadata.ProtectedAdminPassword),
            metadata.Settings.MaxPlayers,
            metadata.Settings.Port,
            metadata.Settings.CommunityServer,
            metadata.Settings.RconEnabled,
            metadata.Settings.RconPort,
            metadata.Settings.RestApiEnabled,
            metadata.Settings.RestApiPort);
        var config = PalworldConfigurationFile.ResolvePath(server.RootPath);
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        if (!File.Exists(config))
        {
            var content = PalworldSettingsSerializer.Serialize(settings);
            await File.WriteAllTextAsync(
                config,
                content,
                new System.Text.UTF8Encoding(false),
                cancellationToken);
            return;
        }

        var document = await PalworldConfigurationFile.ReadAsync(
            server.RootPath,
            cancellationToken);
        var updated = PalworldSettingsSerializer.Merge(document.Content, settings);
        if (updated.Equals(document.Content, StringComparison.Ordinal))
        {
            return;
        }

        _ = await PalworldConfigurationFile.CreateSafetyBackupAsync(
            server.RootPath,
            "start-sync",
            cancellationToken);
        await PalworldConfigurationFile.WriteAtomicAsync(
            document,
            updated,
            cancellationToken);
    }

    public Task CleanupAfterStopAsync(
        GameServerDefinition server,
        CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    private static PalworldServerMetadata ReadMetadata(string root)
    {
        var path = Path.Combine(root, ".1salem", "metadata.json");
        return JsonSerializer.Deserialize<PalworldServerMetadata>(
            File.ReadAllText(path),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("Managed Palworld metadata is invalid.");
    }

    private static async Task<PalworldServerMetadata?> ReadMetadataAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(root, ".1salem", "metadata.json");
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<PalworldServerMetadata>(
            stream,
            new JsonSerializerOptions(JsonSerializerDefaults.Web),
            cancellationToken);
    }
}
