using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using ServerManager.Contracts;
using ServerManager.Core;

namespace ServerManager.Infrastructure.Games.Minecraft;

public sealed class MinecraftUpdateService(
    HttpClient httpClient,
    IMinecraftVersionCatalog versionCatalog,
    MinecraftJarSwapService jarSwapService,
    MinecraftServerProvider provider,
    IProcessSupervisor processSupervisor,
    IGameServerStore gameServerStore) : IUpdateService
{
    public async Task<UpdateCheckResult> CheckAsync(
        GameServerDefinition server,
        CancellationToken cancellationToken = default)
    {
        EnsureMinecraft(server);
        var releases = await versionCatalog.GetReleasesAsync(cancellationToken);
        var latest = releases.FirstOrDefault()
            ?? throw new InvalidDataException("No official Minecraft releases were returned.");
        return new UpdateCheckResult(
            server.InstalledVersion,
            latest.Id,
            GameVersionComparer.Compare(latest.Id, server.InstalledVersion) > 0,
            DateTimeOffset.UtcNow);
    }

    public async Task<OperationResult> UpdateAsync(
        GameServerDefinition server,
        CancellationToken cancellationToken = default)
    {
        EnsureMinecraft(server);
        var releases = await versionCatalog.GetReleasesAsync(cancellationToken);
        var latest = releases.FirstOrDefault()
            ?? throw new InvalidDataException("No official Minecraft releases were returned.");
        if (GameVersionComparer.Compare(latest.Id, server.InstalledVersion) <= 0)
        {
            return OperationResult.Ok();
        }

        var previousSnapshot = await processSupervisor.GetSnapshotAsync(server.Id, cancellationToken);
        var wasRunning = previousSnapshot is { State: ServerState.Running };
        if (wasRunning)
        {
            var stopped = await processSupervisor.StopAsync(server.Id, false, cancellationToken);
            if (!stopped.Success)
            {
                return stopped;
            }
        }

        await gameServerStore.SetStateAsync(server.Id, ServerState.Updating, cancellationToken);
        var metadataPath = Path.Combine(server.RootPath, ".1salem", "metadata.json");
        var oldMetadata = File.Exists(metadataPath)
            ? await File.ReadAllTextAsync(metadataPath, cancellationToken)
            : null;
        JarSwapResult? swap = null;
        string? downloaded = null;
        try
        {
            await CreatePreUpdateSnapshotAsync(server, cancellationToken);
            var downloadRoot = SafePathPolicy.ResolveWithinRoot(
                server.RootPath,
                Path.Combine(".1salem", "downloads"));
            Directory.CreateDirectory(downloadRoot);
            downloaded = Path.Combine(downloadRoot, $"{Guid.NewGuid():N}.jar");
            using (var response = await httpClient.GetAsync(
                       latest.ServerDownloadUrl,
                       HttpCompletionOption.ResponseHeadersRead,
                       cancellationToken))
            {
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var target = File.Create(downloaded);
                await source.CopyToAsync(target, cancellationToken);
            }

            swap = await jarSwapService.SwapAsync(
                server.RootPath,
                downloaded,
                latest.Sha1,
                cancellationToken);
            var metadata = new MinecraftServerMetadata(
                "MinecraftJavaVanilla",
                latest.Id,
                "server.jar",
                latest.Sha1,
                latest.RequiredJavaMajor,
                DateTimeOffset.UtcNow);
            Directory.CreateDirectory(Path.GetDirectoryName(metadataPath)!);
            await File.WriteAllTextAsync(
                metadataPath,
                JsonSerializer.Serialize(
                    metadata,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }),
                cancellationToken);

            var updatedServer = server with { InstalledVersion = latest.Id };
            await gameServerStore.UpsertAsync(
                updatedServer,
                wasRunning ? ServerState.Starting : ServerState.Stopped,
                cancellationToken);
            if (wasRunning)
            {
                await processSupervisor.StartAsync(
                    updatedServer,
                    provider.CreateLaunchSpec(updatedServer),
                    cancellationToken);
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                var verification = await processSupervisor.GetSnapshotAsync(
                    server.Id,
                    cancellationToken);
                if (verification is not { State: ServerState.Running })
                {
                    throw new InvalidOperationException(
                        "Minecraft did not remain running after the update.");
                }
            }

            await gameServerStore.SetStateAsync(
                server.Id,
                wasRunning ? ServerState.Running : ServerState.Stopped,
                cancellationToken);
            return OperationResult.Ok();
        }
        catch (Exception exception)
        {
            if (swap is not null)
            {
                jarSwapService.Rollback(swap);
            }

            if (oldMetadata is not null)
            {
                await File.WriteAllTextAsync(metadataPath, oldMetadata, cancellationToken);
            }

            await gameServerStore.UpsertAsync(server, ServerState.Error, cancellationToken);
            if (wasRunning)
            {
                try
                {
                    await processSupervisor.StartAsync(
                        server,
                        provider.CreateLaunchSpec(server),
                        cancellationToken);
                }
                catch
                {
                    // The original error is returned; diagnostics contain both startup attempts.
                }
            }

            return OperationResult.Fail("MinecraftUpdateFailed", exception.Message);
        }
        finally
        {
            if (downloaded is not null && File.Exists(downloaded))
            {
                File.Delete(downloaded);
            }
        }
    }

    private static async Task CreatePreUpdateSnapshotAsync(
        GameServerDefinition server,
        CancellationToken cancellationToken)
    {
        var backupsRoot = SafePathPolicy.ResolveWithinRoot(server.RootPath, "backups");
        Directory.CreateDirectory(backupsRoot);
        var archivePath = Path.Combine(
            backupsRoot,
            $"Minecraft-pre-update-{DateTimeOffset.UtcNow:yyyy-MM-dd_HH-mm-ss}-{Guid.NewGuid():N}.zip");
        await using (var archiveStream = new FileStream(
                         archivePath,
                         FileMode.CreateNew,
                         FileAccess.ReadWrite,
                         FileShare.None,
                         128 * 1024,
                         FileOptions.Asynchronous))
        using (var archive = new ZipArchive(archiveStream, ZipArchiveMode.Create, true))
        {
            foreach (var file in EnumerateBackupFiles(server.RootPath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(server.RootPath, file);
                var entry = archive.CreateEntry(relative, CompressionLevel.Optimal);
                await using var source = File.OpenRead(file);
                await using var destination = entry.Open();
                await source.CopyToAsync(destination, cancellationToken);
            }
        }

        await using var verification = File.OpenRead(archivePath);
        var hash = Convert.ToHexString(
            await SHA256.HashDataAsync(verification, cancellationToken)).ToLowerInvariant();
        await File.WriteAllTextAsync($"{archivePath}.sha256", hash, cancellationToken);
    }

    private static IEnumerable<string> EnumerateBackupFiles(string root)
    {
        foreach (var name in new[]
                 {
                     "server.properties",
                     "whitelist.json",
                     "ops.json",
                     "banned-players.json",
                     "banned-ips.json",
                     "eula.txt",
                     "user_jvm_args.txt"
                 })
        {
            var path = Path.Combine(root, name);
            if (File.Exists(path))
            {
                yield return path;
            }
        }

        foreach (var directory in Directory.EnumerateDirectories(root, "world*"))
        {
            var info = new DirectoryInfo(directory);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"Backup rejected reparse point: {directory}");
            }

            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                var fileInfo = new FileInfo(file);
                if ((fileInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException($"Backup rejected reparse point: {file}");
                }

                yield return file;
            }
        }
    }

    private static void EnsureMinecraft(GameServerDefinition server)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (server.Game != GameType.Minecraft)
        {
            throw new ArgumentException("The update target is not Minecraft.", nameof(server));
        }
    }
}
