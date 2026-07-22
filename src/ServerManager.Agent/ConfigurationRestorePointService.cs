using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ServerManager.Contracts;
using ServerManager.Core;

namespace ServerManager.Agent;

public sealed class ConfigurationRestorePointService(
    IGameServerStore serverStore,
    ISecretStore secretStore,
    IAuditLogStore auditLogStore,
    IProcessSupervisor processSupervisor,
    GameServerOrchestrator orchestrator)
{
    public const int DefaultRetentionCount = 10;
    private const string HistoryFolder = "configuration-restore-points";
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<ConfigurationRestorePointItem> CreateAsync(
        GameServerDefinition server,
        string reason,
        IEnumerable<string> relativePaths,
        IReadOnlyDictionary<string, string>? summarizedChanges = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentNullException.ThrowIfNull(relativePaths);

        var files = new Dictionary<string, ProtectedConfigurationFile>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var relativePath in relativePaths
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var normalized = NormalizeRelativePath(relativePath);
            var target = SafePathPolicy.ResolveWithinRoot(
                server.RootPath,
                normalized);
            var exists = File.Exists(target);
            var bytes = exists
                ? await File.ReadAllBytesAsync(target, cancellationToken)
                : [];
            files[normalized] = new ProtectedConfigurationFile(
                exists,
                ComputeSha256(bytes),
                secretStore.Protect(Convert.ToBase64String(bytes)));
        }

        if (files.Count == 0)
        {
            throw new ArgumentException(
                "At least one configuration file must be included.",
                nameof(relativePaths));
        }

        var now = DateTimeOffset.UtcNow;
        var id = $"{now:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}";
        var manifest = new RestorePointManifest(
            id,
            server.Id,
            server.Game,
            now,
            ProductVersion,
            server.InstalledVersion,
            SanitizeReason(reason),
            BuildSafeSummary(summarizedChanges),
            files,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            server,
            KnownWorking: await processSupervisor.GetSnapshotAsync(
                server.Id,
                cancellationToken) is not null,
            Label: null);
        await WriteManifestAsync(server.RootPath, manifest, cancellationToken);
        EnforceRetention(server.RootPath);
        await auditLogStore.WriteAsync(
            "LocalAdministrator",
            "ConfigurationRestorePointCreated",
            $"{server.Id}:{id}",
            true,
            $"Game={server.Game}; Reason={manifest.Reason}; Files={files.Count}; KnownWorking={manifest.KnownWorking}",
            cancellationToken);
        return ToItem(server.RootPath, manifest);
    }

    public async Task CompleteAsync(
        Guid serverId,
        string restorePointId,
        bool markKnownWorking,
        CancellationToken cancellationToken = default)
    {
        var server = await GetServerAsync(serverId, cancellationToken);
        var manifest = await ReadManifestAsync(
            server.RootPath,
            restorePointId,
            cancellationToken);
        var hashes = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var relativePath in manifest.Files.Keys)
        {
            var target = SafePathPolicy.ResolveWithinRoot(
                server.RootPath,
                relativePath);
            hashes[relativePath] = File.Exists(target)
                ? ComputeSha256(
                    await File.ReadAllBytesAsync(target, cancellationToken))
                : ComputeSha256([]);
        }

        await WriteManifestAsync(
            server.RootPath,
            manifest with
            {
                AppliedFileHashes = hashes,
                KnownWorking = manifest.KnownWorking || markKnownWorking
            },
            cancellationToken);
    }

    public async Task<IReadOnlyList<ConfigurationRestorePointItem>> ListAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
    {
        var server = await GetServerAsync(serverId, cancellationToken);
        var root = ResolveHistoryRoot(server.RootPath);
        if (!Directory.Exists(root))
        {
            return [];
        }

        var items = new List<ConfigurationRestorePointItem>();
        foreach (var path in Directory.EnumerateFiles(
                     root,
                     "*.json",
                     SearchOption.TopDirectoryOnly)
                     .OrderByDescending(File.GetLastWriteTimeUtc))
        {
            try
            {
                var manifest = await ReadManifestPathAsync(path, cancellationToken);
                if (manifest.ServerId == server.Id &&
                    manifest.Game == server.Game)
                {
                    items.Add(ToItem(server.RootPath, manifest));
                }
            }
            catch (Exception exception) when (
                exception is IOException or JsonException or
                InvalidDataException)
            {
                await auditLogStore.WriteAsync(
                    "Agent",
                    "ConfigurationRestorePointRead",
                    path,
                    false,
                    exception.Message,
                    cancellationToken);
            }
        }

        return items;
    }

    public async Task<ConfigurationRestorePointActionResponse> RestoreAsync(
        Guid serverId,
        ConfigurationRestorePointRestoreRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!request.ConfirmRestart)
        {
            return Failure(
                "RestartConfirmationRequired",
                "Confirm the restart before restoring configuration.");
        }

        var server = await GetServerAsync(serverId, cancellationToken);
        RestorePointManifest selected;
        try
        {
            selected = await ReadManifestAsync(
                server.RootPath,
                request.RestorePointId,
                cancellationToken);
        }
        catch (FileNotFoundException)
        {
            return Failure(
                "RestorePointNotFound",
                "The selected configuration restore point was not found.");
        }

        var safety = await CreateAsync(
            server,
            "before-restore",
            selected.Files.Keys,
            new Dictionary<string, string>
            {
                ["Restore point"] = selected.Id
            },
            cancellationToken);
        var wasRunning = await processSupervisor.GetSnapshotAsync(
            server.Id,
            cancellationToken) is not null;
        try
        {
            await RestoreFilesAsync(
                server.RootPath,
                selected,
                cancellationToken);
            await serverStore.UpsertAsync(
                selected.ServerBefore with { State = server.State },
                server.State,
                cancellationToken);
            var restarted = false;
            if (wasRunning)
            {
                _ = await orchestrator.RestartAsync(
                    server.Id,
                    cancellationToken);
                restarted = true;
            }

            await WriteManifestAsync(
                server.RootPath,
                selected with
                {
                    KnownWorking = selected.KnownWorking || restarted
                },
                cancellationToken);
            EnforceRetention(server.RootPath);
            await auditLogStore.WriteAsync(
                "LocalAdministrator",
                "ConfigurationRestorePointRestored",
                $"{server.Id}:{selected.Id}",
                true,
                $"Game={server.Game}; Restarted={restarted}; Verified=True",
                cancellationToken);
            return new ConfigurationRestorePointActionResponse(
                true,
                restarted
                    ? "Configuration restored, server restarted, and startup verified."
                    : "Configuration restored and file hashes verified. The server was already stopped.",
                restarted,
                true);
        }
        catch (Exception exception)
        {
            var rolledBack = false;
            try
            {
                var rollback = await ReadManifestAsync(
                    server.RootPath,
                    safety.Id,
                    CancellationToken.None);
                await RestoreFilesAsync(
                    server.RootPath,
                    rollback,
                    CancellationToken.None);
                await serverStore.UpsertAsync(
                    server,
                    server.State,
                    CancellationToken.None);
                if (wasRunning)
                {
                    _ = await orchestrator.RestartAsync(
                        server.Id,
                        CancellationToken.None);
                }

                rolledBack = true;
            }
            catch
            {
                // The exact primary failure and rollback state are returned below.
            }

            await auditLogStore.WriteAsync(
                "LocalAdministrator",
                "ConfigurationRestorePointRestored",
                $"{server.Id}:{selected.Id}",
                false,
                $"Error={exception.Message}; RolledBack={rolledBack}",
                CancellationToken.None);
            return Failure(
                "RestoreFailed",
                rolledBack
                    ? $"Restore failed and the pre-restore configuration was recovered: {exception.Message}"
                    : $"Restore and rollback failed: {exception.Message}",
                rolledBack);
        }
    }

    public async Task<ConfigurationRestorePointActionResponse> SetLabelAsync(
        Guid serverId,
        string restorePointId,
        ConfigurationRestorePointLabelRequest request,
        CancellationToken cancellationToken = default)
    {
        var server = await GetServerAsync(serverId, cancellationToken);
        var manifest = await ReadManifestAsync(
            server.RootPath,
            restorePointId,
            cancellationToken);
        var label = string.IsNullOrWhiteSpace(request.Label)
            ? null
            : request.Label.Trim();
        if (label?.Length > 80)
        {
            return Failure(
                "LabelTooLong",
                "Labels can contain at most 80 characters.");
        }

        await WriteManifestAsync(
            server.RootPath,
            manifest with { Label = label },
            cancellationToken);
        return new ConfigurationRestorePointActionResponse(
            true,
            label is null ? "Label removed." : "Label saved.");
    }

    public async Task<ConfigurationRestorePointActionResponse> DeleteAsync(
        Guid serverId,
        string restorePointId,
        CancellationToken cancellationToken = default)
    {
        var server = await GetServerAsync(serverId, cancellationToken);
        var manifest = await ReadManifestAsync(
            server.RootPath,
            restorePointId,
            cancellationToken);
        File.Delete(ResolveManifestPath(server.RootPath, manifest.Id));
        await auditLogStore.WriteAsync(
            "LocalAdministrator",
            "ConfigurationRestorePointDeleted",
            $"{server.Id}:{manifest.Id}",
            true,
            $"Game={server.Game}",
            cancellationToken);
        return new ConfigurationRestorePointActionResponse(
            true,
            "Configuration restore point deleted.");
    }

    private async Task<GameServerDefinition> GetServerAsync(
        Guid serverId,
        CancellationToken cancellationToken) =>
        await serverStore.GetAsync(serverId, cancellationToken)
        ?? throw new KeyNotFoundException(
            $"Server {serverId} is not registered.");

    private async Task RestoreFilesAsync(
        string serverRoot,
        RestorePointManifest manifest,
        CancellationToken cancellationToken)
    {
        foreach (var (relativePath, file) in manifest.Files)
        {
            var target = SafePathPolicy.ResolveWithinRoot(
                serverRoot,
                relativePath);
            if (!file.Existed)
            {
                if (File.Exists(target))
                {
                    File.Delete(target);
                }

                continue;
            }

            var bytes = Convert.FromBase64String(
                secretStore.Unprotect(file.ProtectedContent));
            if (!ComputeSha256(bytes).Equals(
                    file.OriginalSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Protected restore material for '{relativePath}' failed hash verification.");
            }

            await WriteBytesAtomicAsync(target, bytes, cancellationToken);
            var written = await File.ReadAllBytesAsync(target, cancellationToken);
            if (!ComputeSha256(written).Equals(
                    file.OriginalSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException(
                    $"Restored file '{relativePath}' failed hash verification.");
            }
        }
    }

    private static async Task WriteBytesAtomicAsync(
        string target,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(target)
            ?? throw new InvalidOperationException(
                "The configuration directory could not be resolved.");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(
            directory,
            $".{Path.GetFileName(target)}.1salem-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
            if (File.Exists(target) && OperatingSystem.IsWindows())
            {
                File.Replace(temporary, target, null, true);
            }
            else
            {
                File.Move(temporary, target, true);
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static string BuildSafeSummary(
        IReadOnlyDictionary<string, string>? changes)
    {
        if (changes is null || changes.Count == 0)
        {
            return "Configuration files captured before a managed change.";
        }

        return string.Join(
            Environment.NewLine,
            changes
                .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                .Select(item =>
                    $"{item.Key}: {(IsSecretName(item.Key) ? "[protected]" : Limit(item.Value, 160))}"));
    }

    private static bool IsSecretName(string name) =>
        name.Contains("password", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("token", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("authorization", StringComparison.OrdinalIgnoreCase);

    private static string Limit(string value, int maximumLength)
    {
        var safe = value
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        return safe.Length <= maximumLength
            ? safe
            : safe[..maximumLength] + "…";
    }

    private static string SanitizeReason(string value)
    {
        var safe = string.Concat(value.Select(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_'
                ? character
                : '-')).Trim('-');
        return string.IsNullOrEmpty(safe) ? "configuration-change" : safe;
    }

    private static string NormalizeRelativePath(string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new ArgumentException(
                "Configuration paths must be relative to the registered server root.",
                nameof(relativePath));
        }

        return relativePath
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar);
    }

    private static string ComputeSha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));

    private static string ResolveHistoryRoot(string serverRoot) =>
        SafePathPolicy.ResolveWithinRoot(
            serverRoot,
            Path.Combine("backups", HistoryFolder));

    private static string ResolveManifestPath(
        string serverRoot,
        string restorePointId)
    {
        if (string.IsNullOrWhiteSpace(restorePointId) ||
            restorePointId.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
        {
            throw new ArgumentException(
                "The configuration restore point ID is invalid.",
                nameof(restorePointId));
        }

        return Path.Combine(
            ResolveHistoryRoot(serverRoot),
            restorePointId + ".json");
    }

    private static async Task WriteManifestAsync(
        string serverRoot,
        RestorePointManifest manifest,
        CancellationToken cancellationToken)
    {
        var path = ResolveManifestPath(serverRoot, manifest.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporary,
                JsonSerializer.Serialize(manifest, SerializerOptions),
                new UTF8Encoding(false),
                cancellationToken);
            _ = await ReadManifestPathAsync(temporary, cancellationToken);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static async Task<RestorePointManifest> ReadManifestAsync(
        string serverRoot,
        string restorePointId,
        CancellationToken cancellationToken) =>
        await ReadManifestPathAsync(
            ResolveManifestPath(serverRoot, restorePointId),
            cancellationToken);

    private static async Task<RestorePointManifest> ReadManifestPathAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var manifest = await JsonSerializer.DeserializeAsync<RestorePointManifest>(
            stream,
            SerializerOptions,
            cancellationToken);
        return manifest is null ||
               string.IsNullOrWhiteSpace(manifest.Id) ||
               manifest.Files.Count == 0
            ? throw new InvalidDataException(
                "The configuration restore point manifest is invalid.")
            : manifest;
    }

    private static void EnforceRetention(string serverRoot)
    {
        var root = ResolveHistoryRoot(serverRoot);
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(
                     root,
                     "*.json",
                     SearchOption.TopDirectoryOnly)
                     .OrderByDescending(File.GetLastWriteTimeUtc)
                     .Skip(DefaultRetentionCount))
        {
            File.Delete(path);
        }
    }

    private static ConfigurationRestorePointItem ToItem(
        string serverRoot,
        RestorePointManifest manifest)
    {
        var path = ResolveManifestPath(serverRoot, manifest.Id);
        return new ConfigurationRestorePointItem(
            manifest.Id,
            manifest.ServerId,
            manifest.Game,
            manifest.CreatedAtUtc,
            manifest.ApplicationVersion,
            manifest.GameVersion,
            manifest.Reason,
            manifest.Summary,
            manifest.Files.ToDictionary(
                item => item.Key,
                item => item.Value.OriginalSha256,
                StringComparer.OrdinalIgnoreCase),
            manifest.AppliedFileHashes,
            manifest.KnownWorking,
            manifest.Label,
            File.Exists(path) ? new FileInfo(path).Length : 0);
    }

    private static ConfigurationRestorePointActionResponse Failure(
        string errorCode,
        string message,
        bool rolledBack = false) =>
        new(
            false,
            message,
            RolledBack: rolledBack,
            ErrorCode: errorCode);

    private static string ProductVersion { get; } =
        typeof(ConfigurationRestorePointService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            .Split('+', 2)[0]
        ?? typeof(ConfigurationRestorePointService).Assembly
            .GetName()
            .Version
            ?.ToString(3)
        ?? "Unknown";

    public sealed record ProtectedConfigurationFile(
        bool Existed,
        string OriginalSha256,
        string ProtectedContent);

    public sealed record RestorePointManifest(
        string Id,
        Guid ServerId,
        GameType Game,
        DateTimeOffset CreatedAtUtc,
        string ApplicationVersion,
        string? GameVersion,
        string Reason,
        string Summary,
        IReadOnlyDictionary<string, ProtectedConfigurationFile> Files,
        IReadOnlyDictionary<string, string> AppliedFileHashes,
        GameServerDefinition ServerBefore,
        bool KnownWorking,
        string? Label);
}
