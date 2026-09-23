using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ServerManager.Contracts;
using ServerManager.Core;
using ServerManager.Core.Content;
using ServerManager.Infrastructure.Games.Minecraft;

namespace ServerManager.Infrastructure.Content;

/// <summary>
/// Modpacks, which are not plugins and are never applied over an existing server. A pack
/// decides the Minecraft version, the mod loader and the whole mod set, so it is only ever
/// used to build a new server, and a failed build leaves nothing behind.
/// </summary>
public sealed class ModpackService(
    ContentCatalogService catalog,
    IGameServerStore servers,
    IInstalledContentStore store,
    IJavaRuntimeLocator javaLocator,
    IAuditLogStore auditLog,
    HttpClient client,
    ILogger<ModpackService> logger)
{
    /// <summary>Fabric publishes a ready server launcher; the other loaders do not.</summary>
    private const string FabricMetaBase = "https://meta.fabricmc.net/v2/versions";

    private const string FabricMetaHost = "meta.fabricmc.net";

    /// <summary>
    /// Reads the pack and reports what it would build, before anything is created. The
    /// .mrpack itself is small: it is an index plus overrides, not the mods.
    /// </summary>
    public async Task<ModpackPlan> PlanAsync(
        ServerContentProfile profile,
        ContentProviderId providerId,
        string projectId,
        string? versionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var provider = catalog.Find(providerId);
        if (provider is null || !ContentTypePolicy.IsServedBy(ContentKind.Modpack, providerId))
        {
            return Blocked(providerId, projectId, "ProviderUnavailable");
        }

        ContentProject? project;
        ContentVersion? version;
        try
        {
            project = await provider.GetProjectAsync(projectId, profile, ContentKind.Modpack, cancellationToken);
            var versions = await provider.GetVersionsAsync(
                projectId,
                profile,
                ContentKind.Modpack,
                cancellationToken);
            version = versionId is { Length: > 0 }
                ? versions.FirstOrDefault(candidate =>
                    string.Equals(candidate.VersionId, versionId, StringComparison.Ordinal))
                : versions
                    .Where(candidate => candidate.File is not null)
                    .OrderBy(candidate => candidate.Channel)
                    .ThenByDescending(candidate => candidate.PublishedAtUtc ?? DateTimeOffset.MinValue)
                    .FirstOrDefault();
        }
        catch (ContentProviderException exception)
        {
            return Blocked(providerId, projectId, exception.ErrorCode);
        }

        if (version?.File is null)
        {
            return Blocked(providerId, projectId, "NoDownloadableFile", project?.Name);
        }

        var staging = Path.Combine(
            Path.GetTempPath(),
            "1salem-modpack",
            $"{Guid.NewGuid():N}.mrpack");
        Directory.CreateDirectory(Path.GetDirectoryName(staging)!);
        try
        {
            await provider.DownloadAsync(version, staging, null, cancellationToken);
            var (index, failure) = await ReadIndexAsync(staging, cancellationToken);
            if (index is null)
            {
                return Blocked(providerId, projectId, failure ?? "MalformedIndex", project?.Name);
            }

            var validation = ModpackIndexPolicy.Validate(index);
            if (!validation.IsValid)
            {
                return new ModpackPlan(
                    providerId,
                    projectId,
                    project?.Name ?? index.Name,
                    version.VersionId,
                    version.VersionNumber,
                    index.MinecraftVersion,
                    index.Loader,
                    index.LoaderVersion,
                    0,
                    0,
                    false,
                    validation.Warnings,
                    true,
                    validation.ErrorCode);
            }

            var serverFiles = index.Files.Where(file => file.IsRequiredOnServer).ToArray();
            var hasServerOverrides = HasEntriesUnder(staging, "server-overrides/") ||
                                     HasEntriesUnder(staging, "overrides/");

            return new ModpackPlan(
                providerId,
                projectId,
                project?.Name ?? index.Name,
                version.VersionId,
                version.VersionNumber,
                index.MinecraftVersion,
                index.Loader,
                index.LoaderVersion,
                serverFiles.Length,
                serverFiles.Sum(file => file.FileSize),
                hasServerOverrides,
                validation.Warnings);
        }
        catch (ContentProviderException exception)
        {
            return Blocked(providerId, projectId, exception.ErrorCode, project?.Name);
        }
        finally
        {
            TryDelete(staging);
        }
    }

    /// <summary>
    /// Builds a new server from a pack. Everything is written under a directory this call
    /// creates; if any step fails, that directory is removed and nothing is registered.
    /// </summary>
    public async Task<ModpackInstallResult> InstallAsync(
        ServerContentProfile profile,
        ModpackInstallRequest request,
        IProgress<ContentInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(request);

        var destination = Path.GetFullPath(request.DestinationPath);
        if (Directory.Exists(destination) &&
            Directory.EnumerateFileSystemEntries(destination).Any())
        {
            return new ModpackInstallResult(
                false,
                "DestinationNotEmpty",
                "Choose a folder that does not already exist or is empty.");
        }

        if (!request.EulaAccepted)
        {
            return new ModpackInstallResult(
                false,
                "EulaRequired",
                "Minecraft's EULA has to be accepted before a server can run.");
        }

        // Several Minecraft servers can be managed side by side, but not on the same port.
        var existing = await servers.ListAsync(cancellationToken);
        if (ServerPortAllocationPolicy.FindConflict(existing, request.Port) is { } conflict)
        {
            var suggestion = ServerPortAllocationPolicy.SuggestPort(existing, request.Port);
            return new ModpackInstallResult(
                false,
                "PortInUse",
                $"Port {conflict.Port} is already set up for '{conflict.ServerName}'." +
                (suggestion is { } free ? $" Try port {free}." : string.Empty));
        }

        progress?.Report(new ContentInstallProgress(
            ContentInstallStage.CheckingCompatibility,
            "ResolvingPack"));
        var plan = await PlanAsync(
            profile,
            request.Provider,
            request.ProjectId,
            request.VersionId,
            cancellationToken);
        if (plan.Blocked)
        {
            return new ModpackInstallResult(false, plan.BlockedReason, "The pack cannot be installed.");
        }

        var provider = catalog.Find(request.Provider)!;
        var staging = Path.Combine(Path.GetTempPath(), "1salem-modpack", $"{Guid.NewGuid():N}.mrpack");
        Directory.CreateDirectory(Path.GetDirectoryName(staging)!);
        var created = false;

        try
        {
            var versions = await provider.GetVersionsAsync(
                request.ProjectId,
                profile,
                ContentKind.Modpack,
                cancellationToken);
            var version = versions.FirstOrDefault(candidate =>
                string.Equals(candidate.VersionId, request.VersionId, StringComparison.Ordinal));
            if (version?.File is null)
            {
                return new ModpackInstallResult(false, "VersionUnavailable", "That pack version is gone.");
            }

            await provider.DownloadAsync(version, staging, progress, cancellationToken);
            var (index, failure) = await ReadIndexAsync(staging, cancellationToken);
            var validation = ModpackIndexPolicy.Validate(index);
            if (index is null || !validation.IsValid)
            {
                return new ModpackInstallResult(
                    false,
                    failure ?? validation.ErrorCode,
                    validation.Reason ?? "The pack index could not be read.");
            }

            Directory.CreateDirectory(destination);
            created = true;

            // The loader's own server launcher. Fabric publishes one; the check above has
            // already refused the loaders that need their own installer program.
            progress?.Report(new ContentInstallProgress(
                ContentInstallStage.Downloading,
                "DownloadingLoader"));
            var launcher = Path.Combine(destination, "server.jar");
            await DownloadFabricServerAsync(
                index.MinecraftVersion!,
                index.LoaderVersion,
                launcher,
                cancellationToken);

            // Every file the pack lists for a server, verified against the index's own hash.
            var serverFiles = index.Files.Where(file => file.IsRequiredOnServer).ToArray();
            var installed = 0;
            long transferred = 0;
            foreach (var file in serverFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                installed++;
                progress?.Report(new ContentInstallProgress(
                    ContentInstallStage.Downloading,
                    "DownloadingPackFile",
                    file.Path,
                    installed,
                    serverFiles.Length));

                var target = ArchiveSafetyPolicy.ResolveEntryDestination(destination, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var downloaded = false;
                foreach (var url in file.Downloads.Where(ModpackIndexPolicy.IsAllowedDownloadHost))
                {
                    try
                    {
                        await DownloadVerifiedAsync(url, target, file, cancellationToken);
                        downloaded = true;
                        break;
                    }
                    catch (Exception exception) when (
                        exception is HttpRequestException or InvalidDataException)
                    {
                        logger.LogDebug(exception, "A pack file source failed; trying the next one.");
                    }
                }

                if (!downloaded)
                {
                    return await FailAsync(
                        destination,
                        created,
                        "FileUnavailable",
                        $"A file the pack needs could not be downloaded or did not match its checksum: {file.Path}");
                }

                transferred += file.FileSize;
                if (transferred > ArchiveSafetyPolicy.MaximumModpackTotalBytes)
                {
                    return await FailAsync(destination, created, "PackTooLarge", "The pack exceeded its size limit.");
                }
            }

            // Overrides come after the listed files, and server-overrides win over the shared
            // ones, which is the order the format defines.
            progress?.Report(new ContentInstallProgress(ContentInstallStage.Installing, "ApplyingOverrides"));
            await SafeArchive.ExtractAsync(
                staging,
                destination,
                ContentKind.Modpack,
                "overrides/",
                cancellationToken);
            await SafeArchive.ExtractAsync(
                staging,
                destination,
                ContentKind.Modpack,
                "server-overrides/",
                cancellationToken);

            // The same server files the rest of the app expects: an accepted EULA, a
            // properties file with the chosen port, and a Java path for the launcher.
            await File.WriteAllTextAsync(
                Path.Combine(destination, "eula.txt"),
                $"# Accepted through 1Salem Server Manager on {DateTimeOffset.UtcNow:u}\neula=true\n",
                cancellationToken);
            WriteProperties(destination, request.Port);

            var java = await javaLocator.FindAsync(21, cancellationToken);
            if (java is null)
            {
                return await FailAsync(
                    destination,
                    created,
                    "JavaMissing",
                    "No suitable Java runtime was found for this pack.");
            }

            var managedJava = Path.Combine(destination, ".1salem");
            Directory.CreateDirectory(managedJava);
            await File.WriteAllTextAsync(
                Path.Combine(managedJava, "java-path.txt"),
                java.ExecutablePath,
                cancellationToken);

            progress?.Report(new ContentInstallProgress(
                ContentInstallStage.RecordingMetadata,
                "RegisteringServer"));
            var serverId = Guid.NewGuid();
            try
            {
                await servers.UpsertAsync(
                    new GameServerDefinition(
                        serverId,
                        GameType.Minecraft,
                        request.ServerName,
                        destination,
                        request.Port,
                        index.MinecraftVersion,
                        DateTimeOffset.UtcNow,
                        ServerState.Stopped,
                        JavaExecutablePath: java.ExecutablePath),
                    ServerState.Stopped,
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Registration is the last step; if it cannot happen, the downloaded files are
                // not left lying around pretending to be a server.
                logger.LogWarning(exception, "The new modpack server could not be registered.");
                return await FailAsync(
                    destination,
                    created,
                    "RegistrationFailed",
                    "The new server could not be registered, so nothing was kept.");
            }

            // The pack itself is the managed unit, so it is recorded like any other installed
            // content, against the server it created. The individual mods inside are not
            // separate marketplace items and are never listed as such.
            var packDirectory = ContentPathPolicy.ResolveModpackDirectory(destination);
            Directory.CreateDirectory(packDirectory);
            var packFileName = ContentPathPolicy.SanitizeFileName(
                version.File.FileName,
                plan.ProjectName,
                ContentTypePolicy.FileExtension(ContentKind.Modpack));
            var keptPack = ContentPathPolicy.ResolveInstallPath(packDirectory, packFileName);
            File.Copy(staging, keptPack, true);
            var packDigests = await FileDigests.ComputeAsync(keptPack, true, cancellationToken);

            await store.UpsertAsync(
                new InstalledContent(
                    serverId,
                    packFileName,
                    ContentTypePolicy.PendingState(ContentKind.Modpack, false),
                    ContentKind.Modpack,
                    request.Provider,
                    request.ProjectId,
                    version.VersionId,
                    plan.ProjectName,
                    plan.VersionNumber,
                    index.MinecraftVersion,
                    ServerPlatform.Unknown,
                    null,
                    version.File.Sha512,
                    version.File.Sha256,
                    packDigests.Sha256,
                    DateTimeOffset.UtcNow,
                    false,
                    null,
                    null,
                    null,
                    null,
                    version.File.SizeBytes,
                    true,
                    version.File.Url,
                    version.File.Sha1,
                    Path.GetRelativePath(destination, keptPack),
                    false,
                    index.Loader,
                    index.LoaderVersion),
                cancellationToken);

            // A marker so the server can say what it was built from later.
            await File.WriteAllTextAsync(
                Path.Combine(managedJava, "modpack.json"),
                JsonSerializer.Serialize(new
                {
                    provider = request.Provider.ToString(),
                    projectId = request.ProjectId,
                    versionId = request.VersionId,
                    name = plan.ProjectName,
                    packVersion = plan.VersionNumber,
                    minecraft = index.MinecraftVersion,
                    loader = index.Loader,
                    loaderVersion = index.LoaderVersion,
                    installedUtc = DateTimeOffset.UtcNow
                }, new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken);

            await auditLog.WriteAsync(
                "content",
                "ModpackInstalled",
                $"server:{serverId}",
                true,
                $"{plan.ProjectName} {plan.VersionNumber} ({index.Loader} {index.MinecraftVersion})",
                cancellationToken);

            progress?.Report(new ContentInstallProgress(ContentInstallStage.Completed, "Installed"));
            return new ModpackInstallResult(
                true,
                null,
                null,
                serverId,
                destination,
                index.MinecraftVersion,
                index.Loader,
                serverFiles.Length);
        }
        catch (OperationCanceledException)
        {
            await FailAsync(destination, created, "Cancelled", "The install was cancelled.");
            return new ModpackInstallResult(false, "Cancelled", "The install was cancelled.");
        }
        catch (ContentProviderException exception)
        {
            return await FailAsync(destination, created, exception.ErrorCode, exception.Message);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            logger.LogWarning(exception, "A modpack install failed.");
            return await FailAsync(destination, created, "InstallFailed", "The pack could not be installed.");
        }
        finally
        {
            TryDelete(staging);
        }
    }

    private async Task<ModpackInstallResult> FailAsync(
        string destination,
        bool created,
        string? code,
        string message)
    {
        // A half-built server is worse than none: the directory this call created goes away.
        if (created)
        {
            try
            {
                await Task.Run(() => Directory.Delete(destination, true));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "The incomplete modpack folder could not be removed.");
                return new ModpackInstallResult(
                    false,
                    code,
                    $"{message} The incomplete folder at {destination} could not be removed automatically.");
            }
        }

        return new ModpackInstallResult(false, code, message);
    }

    private async Task DownloadFabricServerAsync(
        string minecraftVersion,
        string? loaderVersion,
        string destination,
        CancellationToken cancellationToken)
    {
        var loader = loaderVersion;
        if (string.IsNullOrWhiteSpace(loader))
        {
            using var document = await ContentHttp.GetJsonAsync(
                client,
                ContentProviderId.Modrinth,
                $"{FabricMetaBase}/loader",
                cancellationToken);
            loader = document.RootElement.EnumerateArray()
                .FirstOrDefault(element => element.Bool("stable"))
                .String("version");
        }

        using var installers = await ContentHttp.GetJsonAsync(
            client,
            ContentProviderId.Modrinth,
            $"{FabricMetaBase}/installer",
            cancellationToken);
        var installer = installers.RootElement.EnumerateArray()
            .FirstOrDefault(element => element.Bool("stable"))
            .String("version");
        if (string.IsNullOrWhiteSpace(loader) || string.IsNullOrWhiteSpace(installer))
        {
            throw new InvalidDataException("Fabric did not publish a usable server launcher.");
        }

        var url = new Uri(
            $"{FabricMetaBase}/loader/{Uri.EscapeDataString(minecraftVersion)}/" +
            $"{Uri.EscapeDataString(loader)}/{Uri.EscapeDataString(installer)}/server/jar");
        if (!string.Equals(url.Host, FabricMetaHost, StringComparison.OrdinalIgnoreCase) ||
            url.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidDataException("The loader address is not Fabric's own site.");
        }

        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        await source.CopyToAsync(target, cancellationToken);
    }

    /// <summary>
    /// Downloads one listed pack file and checks it against the index's own hash before it is
    /// allowed to stay. A mismatch deletes the file and reports failure.
    /// </summary>
    private async Task DownloadVerifiedAsync(
        Uri url,
        string destination,
        ModpackFile file,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var declared = response.Content.Headers.ContentLength;
        if (declared is { } length && length > ArchiveSafetyPolicy.MaximumDownloadBytes(ContentKind.ResourcePack))
        {
            throw new InvalidDataException("A pack file is larger than this app will download.");
        }

        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await source.CopyToAsync(target, cancellationToken);
        }

        var digests = await FileDigests.ComputeAsync(destination, true, cancellationToken);
        var matches = FileDigests.Matches(file.Sha512, digests.Sha512!) ??
                      FileDigests.Matches(file.Sha1, digests.Sha1);
        if (matches != true)
        {
            TryDelete(destination);
            throw new InvalidDataException($"The downloaded file did not match the pack's checksum: {file.Path}");
        }
    }

    private static async Task<(ModpackIndex? Index, string? Failure)> ReadIndexAsync(
        string packPath,
        CancellationToken cancellationToken)
    {
        var inspection = SafeArchive.Inspect(packPath, ContentKind.Modpack);
        if (!inspection.IsValid)
        {
            return (null, "InvalidArchive");
        }

        var json = await SafeArchive.ReadTextEntryAsync(
            packPath,
            ModpackIndexPolicy.IndexFileName,
            cancellationToken);
        if (json is null)
        {
            return (null, "MalformedIndex");
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var files = new List<ModpackFile>();
            foreach (var element in root.Array("files"))
            {
                var path = element.String("path");
                if (path is null)
                {
                    continue;
                }

                var downloads = new List<Uri>();
                foreach (var download in element.Strings("downloads"))
                {
                    if (Uri.TryCreate(download, UriKind.Absolute, out var url))
                    {
                        downloads.Add(url);
                    }
                }

                var hashes = element.Property("hashes");
                var environment = element.Property("env");
                files.Add(new ModpackFile(
                    path,
                    downloads,
                    hashes?.String("sha512"),
                    hashes?.String("sha1"),
                    element.Int64("fileSize") ?? 0,
                    environment?.String("client") ?? "required",
                    environment?.String("server") ?? "required"));
            }

            var dependencies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (root.Property("dependencies") is { ValueKind: JsonValueKind.Object } deps)
            {
                foreach (var property in deps.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        dependencies[property.Name] = property.Value.GetString()!;
                    }
                }
            }

            return (new ModpackIndex(
                (int)(root.Int64("formatVersion") ?? 0),
                root.String("game") ?? string.Empty,
                root.String("versionId") ?? string.Empty,
                root.String("name") ?? string.Empty,
                root.String("summary"),
                files,
                dependencies), null);
        }
        catch (JsonException)
        {
            return (null, "MalformedIndex");
        }
    }

    private static bool HasEntriesUnder(string packPath, string prefix)
    {
        try
        {
            using var archive = System.IO.Compression.ZipFile.OpenRead(packPath);
            return archive.Entries.Any(entry =>
                entry.FullName.Replace('\\', '/').StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException)
        {
            return false;
        }
    }

    private static void WriteProperties(string destination, int port)
    {
        var path = Path.Combine(destination, "server.properties");
        if (File.Exists(path))
        {
            // An override already supplied one; only the port is forced, so the pack's own
            // settings survive.
            var lines = File.ReadAllLines(path).ToList();
            var replaced = false;
            for (var index = 0; index < lines.Count; index++)
            {
                if (lines[index].StartsWith("server-port=", StringComparison.OrdinalIgnoreCase))
                {
                    lines[index] = $"server-port={port}";
                    replaced = true;
                }
            }

            if (!replaced)
            {
                lines.Add($"server-port={port}");
            }

            File.WriteAllText(path, string.Join('\n', lines) + "\n", new UTF8Encoding(false));
            return;
        }

        File.WriteAllText(
            path,
            $"server-port={port}\nlevel-name=world\nmotd=1Salem modpack server\n",
            new UTF8Encoding(false));
    }

    private static ModpackPlan Blocked(
        ContentProviderId provider,
        string projectId,
        string reason,
        string? name = null) =>
        new(
            provider,
            projectId,
            name ?? projectId,
            string.Empty,
            string.Empty,
            null,
            null,
            null,
            0,
            0,
            false,
            [],
            true,
            reason);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
