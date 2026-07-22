using System.IO.Compression;
using System.Text.Json;
using ServerManager.Contracts;
using ServerManager.Core;

namespace ServerManager.Infrastructure.Games.Palworld;

public sealed class PalworldUpdateService(
    ISteamCmdService steamCmdService,
    PalworldServerProvider provider,
    IProcessSupervisor processSupervisor,
    IGameServerStore gameServerStore)
{
    public async Task<UpdateCheckResult> CheckAsync(
        GameServerDefinition server,
        CancellationToken cancellationToken = default)
    {
        EnsurePalworld(server);
        var metadata = await ReadMetadataAsync(server.RootPath, cancellationToken);
        var latest = await steamCmdService.GetLatestBuildIdAsync(
            metadata.AppId,
            cancellationToken);
        return new UpdateCheckResult(
            metadata.BuildId,
            latest,
            latest is not null && GameVersionComparer.Compare(latest, metadata.BuildId) > 0,
            DateTimeOffset.UtcNow);
    }

    public async Task<OperationResult> UpdateAsync(
        GameServerDefinition server,
        CancellationToken cancellationToken = default)
    {
        EnsurePalworld(server);
        var metadata = await ReadMetadataAsync(server.RootPath, cancellationToken);
        var wasRunning = await processSupervisor.GetSnapshotAsync(server.Id, cancellationToken)
            is { State: ServerState.Running };
        if (wasRunning)
        {
            var stopped = await processSupervisor.StopAsync(server.Id, false, cancellationToken);
            if (!stopped.Success)
            {
                return stopped;
            }
        }

        await provider.CleanupAfterStopAsync(server, cancellationToken);
        await gameServerStore.SetStateAsync(server.Id, ServerState.Updating, cancellationToken);
        try
        {
            await CreateSaveSnapshotAsync(server.RootPath, cancellationToken);
            var update = await steamCmdService.InstallOrUpdateAsync(
                server.RootPath,
                metadata.AppId,
                cancellationToken);
            var updatedMetadata = metadata with
            {
                BuildId = update.BuildId,
                InstalledAtUtc = DateTimeOffset.UtcNow
            };
            await WriteMetadataAsync(server.RootPath, updatedMetadata, cancellationToken);
            var updatedServer = server with { InstalledVersion = update.BuildId };
            await gameServerStore.UpsertAsync(
                updatedServer,
                wasRunning ? ServerState.Starting : ServerState.Stopped,
                cancellationToken);

            if (wasRunning)
            {
                await provider.PrepareForStartAsync(updatedServer, cancellationToken);
                await processSupervisor.StartAsync(
                    updatedServer,
                    provider.CreateLaunchSpec(updatedServer),
                    cancellationToken);
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                if (await processSupervisor.GetSnapshotAsync(server.Id, cancellationToken)
                    is not { State: ServerState.Running })
                {
                    await gameServerStore.SetStateAsync(
                        server.Id,
                        ServerState.Error,
                        cancellationToken);
                    return OperationResult.Fail(
                        "PalworldRestartVerificationFailed",
                        "Palworld was updated but did not remain running. It has been left stopped; restore the save snapshot if needed.");
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
            await gameServerStore.SetStateAsync(server.Id, ServerState.Error, cancellationToken);
            return OperationResult.Fail(
                "PalworldUpdateFailed",
                $"{exception.Message} The server remains stopped; the pre-update save/config snapshot was retained.");
        }
    }

    private static async Task CreateSaveSnapshotAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var saved = Path.Combine(root, "Pal", "Saved");
        if (!Directory.Exists(saved))
        {
            return;
        }

        var backupRoot = Path.Combine(root, "backups");
        Directory.CreateDirectory(backupRoot);
        var archivePath = Path.Combine(
            backupRoot,
            $"Palworld-pre-update-{DateTimeOffset.UtcNow:yyyy-MM-dd_HH-mm-ss}-{Guid.NewGuid():N}.zip");
        await using var output = File.Create(archivePath);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create, true);
        foreach (var file in Directory.EnumerateFiles(saved, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(file);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"Palworld backup rejected reparse point: {file}");
            }

            var relative = Path.GetRelativePath(root, file);
            var entry = archive.CreateEntry(relative, CompressionLevel.Optimal);
            await using var source = File.OpenRead(file);
            await using var destination = entry.Open();
            await source.CopyToAsync(destination, cancellationToken);
        }
    }

    private static async Task<PalworldServerMetadata> ReadMetadataAsync(
        string root,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(Path.Combine(root, ".1salem", "metadata.json"));
        return await JsonSerializer.DeserializeAsync<PalworldServerMetadata>(
            stream,
            new JsonSerializerOptions(JsonSerializerDefaults.Web),
            cancellationToken)
            ?? throw new InvalidDataException("Managed Palworld metadata is invalid.");
    }

    private static async Task WriteMetadataAsync(
        string root,
        PalworldServerMetadata metadata,
        CancellationToken cancellationToken)
    {
        await using var stream = File.Create(Path.Combine(root, ".1salem", "metadata.json"));
        await JsonSerializer.SerializeAsync(
            stream,
            metadata,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true },
            cancellationToken);
    }

    private static void EnsurePalworld(GameServerDefinition server)
    {
        if (server.Game != GameType.Palworld)
        {
            throw new ArgumentException("The update target is not Palworld.", nameof(server));
        }
    }
}

