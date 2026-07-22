using System.Text.Json;
using ServerManager.Contracts;
using ServerManager.Core;
using ServerManager.Infrastructure.Games.Palworld;

namespace ServerManager.Infrastructure.Games.Minecraft;

public sealed class SafeFileImportService(
    MinecraftServerProvider minecraftProvider,
    PalworldServerProvider palworldProvider,
    ISecretStore secretStore,
    IGameServerStore gameServerStore) : IFileImportService
{
    public async Task<ImportPlan> PlanAsync(
        ImportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var source = Path.GetFullPath(request.SourcePath);
        var destination = Path.GetFullPath(request.DestinationPath);
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException($"The import source does not exist: {source}");
        }

        var provider = GetProvider(request.Game);
        var detection = await provider.DetectInstallationAsync(source, cancellationToken);
        if (!detection.IsInstalled)
        {
            throw new InvalidDataException(
                "The selected folder does not contain a detectable vanilla Minecraft server.");
        }

        var files = EnumerateSafeFiles(source).ToArray();
        var warnings = request.Mode == ImportMode.ManageInPlace
            ? SafePathPolicy.GetRiskWarnings(source).ToList()
            : [];
        if (SafePathPolicy.IsWithinRoot(destination, source) ||
            SafePathPolicy.IsWithinRoot(source, destination))
        {
            throw new InvalidDataException("Source and destination must not contain one another.");
        }

        return new ImportPlan(
            request with { SourcePath = source, DestinationPath = destination },
            files.Sum(file => file.Length),
            files.Length,
            detection.DetectedFiles,
            warnings);
    }

    public async Task<OperationResult> ExecuteAsync(
        ImportPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var request = plan.Request;
        if (request.Mode == ImportMode.Move)
        {
            var expected = $"MOVE {Path.GetFileName(request.SourcePath.TrimEnd(Path.DirectorySeparatorChar))}";
            if (!string.Equals(request.ConfirmationText, expected, StringComparison.Ordinal))
            {
                return OperationResult.Fail(
                    "MoveConfirmationRequired",
                    $"Type exactly: {expected}");
            }
        }

        if (request.Mode == ImportMode.ManageInPlace)
        {
            return await RegisterAsync(request, request.SourcePath, cancellationToken);
        }

        if (Directory.Exists(request.DestinationPath) || File.Exists(request.DestinationPath))
        {
            return OperationResult.Fail(
                "DestinationExists",
                "The import destination must not already exist.");
        }

        var parent = Directory.GetParent(request.DestinationPath);
        if (parent is null)
        {
            return OperationResult.Fail("InvalidDestination", "The destination parent folder is invalid.");
        }

        Directory.CreateDirectory(parent.FullName);
        var staging = Path.Combine(
            parent.FullName,
            $".1salem-import-staging-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(staging);
            var files = EnumerateSafeFiles(request.SourcePath).ToArray();
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(request.SourcePath, file.FullName);
                var destinationFile = SafePathPolicy.ResolveWithinRoot(staging, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
                File.Copy(file.FullName, destinationFile, false);
            }

            var copied = EnumerateSafeFiles(staging).ToArray();
            if (copied.Length != files.Length ||
                copied.Sum(file => file.Length) != files.Sum(file => file.Length))
            {
                return OperationResult.Fail(
                    "ImportVerificationFailed",
                    "Copied file count or size did not match the source.");
            }

            var manifest = new
            {
                game = request.Game.ToString(),
                sourcePath = request.SourcePath,
                destinationPath = request.DestinationPath,
                mode = request.Mode.ToString(),
                fileCount = files.Length,
                totalBytes = files.Sum(file => file.Length),
                createdAtUtc = DateTimeOffset.UtcNow
            };
            Directory.CreateDirectory(Path.Combine(staging, ".1salem"));
            await File.WriteAllTextAsync(
                Path.Combine(staging, ".1salem", "import-manifest.json"),
                JsonSerializer.Serialize(
                    manifest,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }),
                cancellationToken);

            Directory.Move(staging, request.DestinationPath);
            var registration = await RegisterAsync(request, request.DestinationPath, cancellationToken);
            if (!registration.Success)
            {
                return registration;
            }

            if (request.Mode == ImportMode.Move)
            {
                Directory.Delete(request.SourcePath, true);
            }

            return OperationResult.Ok();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return OperationResult.Fail("ImportFailed", exception.Message);
        }
        finally
        {
            if (Directory.Exists(staging) &&
                SafePathPolicy.IsWithinRoot(staging, parent.FullName) &&
                Path.GetFileName(staging).StartsWith(
                    ".1salem-import-staging-",
                    StringComparison.Ordinal))
            {
                Directory.Delete(staging, true);
            }
        }
    }

    private async Task<OperationResult> RegisterAsync(
        ImportRequest request,
        string root,
        CancellationToken cancellationToken)
    {
        var detection = await GetProvider(request.Game).DetectInstallationAsync(root, cancellationToken);
        if (!detection.IsInstalled)
        {
            return OperationResult.Fail(
                "ImportDetectionFailed",
                $"{request.Game} files were not detected after import verification.");
        }

        var port = request.Game == GameType.Minecraft
            ? await ReadMinecraftPortAsync(root, cancellationToken)
            : 8211;
        if (request.Game == GameType.Palworld)
        {
            await EnsurePalworldMetadataAsync(root, detection.Version, cancellationToken);
        }

        var server = new GameServerDefinition(
            Guid.NewGuid(),
            request.Game,
            Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar)),
            Path.GetFullPath(root),
            port,
            detection.Version,
            DateTimeOffset.UtcNow);
        await gameServerStore.UpsertAsync(server, ServerState.Stopped, cancellationToken);
        return OperationResult.Ok();
    }

    private IGameServerProvider GetProvider(GameType game) =>
        game switch
        {
            GameType.Minecraft => minecraftProvider,
            GameType.Palworld => palworldProvider,
            _ => throw new ArgumentOutOfRangeException(nameof(game))
        };

    private static async Task<int> ReadMinecraftPortAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var propertiesPath = Path.Combine(root, "server.properties");
        var properties = MinecraftPropertiesSerializer.Parse(
            await File.ReadAllTextAsync(propertiesPath, cancellationToken));
        return properties.TryGetValue("server-port", out var value) &&
               int.TryParse(value, out var parsedPort)
            ? parsedPort
            : 25565;
    }

    private async Task EnsurePalworldMetadataAsync(
        string root,
        string? buildId,
        CancellationToken cancellationToken)
    {
        var metadataPath = Path.Combine(root, ".1salem", "metadata.json");
        if (File.Exists(metadataPath))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(metadataPath)!);
        var name = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar));
        var metadata = new PalworldServerMetadata(
            "PalworldVanilla",
            2_394_010,
            buildId,
            secretStore.Protect(string.Empty),
            secretStore.Protect(string.Empty),
            new PalworldServerSettingsTemplate(
                name,
                string.Empty,
                32,
                8211,
                false,
                false,
                25575),
            DateTimeOffset.UtcNow);
        await File.WriteAllTextAsync(
            metadataPath,
            JsonSerializer.Serialize(
                metadata,
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }),
            cancellationToken);
    }

    private static IEnumerable<FileInfo> EnumerateSafeFiles(string root)
    {
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        while (pending.TryPop(out var directory))
        {
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"Reparse points are not allowed during import: {directory.FullName}");
            }

            foreach (var file in directory.EnumerateFiles())
            {
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException(
                        $"Reparse-point files are not allowed during import: {file.FullName}");
                }

                yield return file;
            }

            foreach (var child in directory.EnumerateDirectories())
            {
                pending.Push(child);
            }
        }
    }
}
