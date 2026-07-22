using System.Text.Json;
using ServerManager.Contracts;
using ServerManager.Core;

namespace ServerManager.Infrastructure.Games.Palworld;

public sealed class PalworldInstaller(
    ISteamCmdService steamCmdService,
    ISecretStore secretStore,
    IGameServerStore gameServerStore) : IPalworldInstaller
{
    public async Task<PalworldInstallResult> InstallAsync(
        PalworldInstallRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        PalworldSettingsSerializer.Validate(request.Settings);
        if (request.AppId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "A valid Steam app ID is required.");
        }

        var destination = Path.GetFullPath(request.DestinationPath);
        if (Directory.Exists(destination) || File.Exists(destination))
        {
            throw new IOException(
                "The Palworld destination must not already exist. Import existing data instead.");
        }

        var parent = Directory.GetParent(destination)
            ?? throw new DirectoryNotFoundException("The destination parent folder is invalid.");
        Directory.CreateDirectory(parent.FullName);
        var staging = Path.Combine(
            parent.FullName,
            $".1salem-palworld-staging-{Guid.NewGuid():N}");
        try
        {
            var install = await steamCmdService.InstallOrUpdateAsync(
                staging,
                request.AppId,
                cancellationToken);
            ValidateInstallation(staging);
            var metadataRoot = Path.Combine(staging, ".1salem");
            Directory.CreateDirectory(metadataRoot);
            Directory.CreateDirectory(
                Path.Combine(staging, "Pal", "Saved", "Config", "WindowsServer"));
            Directory.CreateDirectory(Path.Combine(staging, "backups"));

            var template = new PalworldServerSettingsTemplate(
                request.Settings.ServerName,
                request.Settings.Description,
                request.Settings.MaxPlayers,
                request.Settings.Port,
                request.Settings.CommunityServer,
                request.Settings.RconEnabled,
                request.Settings.RconPort);
            var metadata = new PalworldServerMetadata(
                "PalworldVanilla",
                request.AppId,
                install.BuildId,
                secretStore.Protect(request.Settings.ServerPassword),
                secretStore.Protect(request.Settings.AdminPassword),
                template,
                DateTimeOffset.UtcNow);
            await using (var stream = File.Create(Path.Combine(metadataRoot, "metadata.json")))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    metadata,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true },
                    cancellationToken);
            }

            Directory.Move(staging, destination);
            var serverId = Guid.NewGuid();
            await gameServerStore.UpsertAsync(
                new GameServerDefinition(
                    serverId,
                    GameType.Palworld,
                    request.Settings.ServerName,
                    destination,
                    request.Settings.Port,
                    install.BuildId,
                    DateTimeOffset.UtcNow),
                ServerState.Stopped,
                cancellationToken);
            return new PalworldInstallResult(
                serverId,
                destination,
                install.BuildId,
                DateTimeOffset.UtcNow);
        }
        catch
        {
            DeleteOwnedStaging(staging, parent.FullName);
            throw;
        }
    }

    private static void ValidateInstallation(string root)
    {
        if (!File.Exists(Path.Combine(root, "PalServer.exe")) ||
            !Directory.Exists(Path.Combine(root, "Pal")))
        {
            throw new InvalidDataException(
                "SteamCMD completed but PalServer.exe or the Pal folder was not found.");
        }
    }

    private static void DeleteOwnedStaging(string staging, string parent)
    {
        if (Directory.Exists(staging) &&
            SafePathPolicy.IsWithinRoot(staging, parent) &&
            Path.GetFileName(staging).StartsWith(
                ".1salem-palworld-staging-",
                StringComparison.Ordinal))
        {
            Directory.Delete(staging, true);
        }
    }
}

