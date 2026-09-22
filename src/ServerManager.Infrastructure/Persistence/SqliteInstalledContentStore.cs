using Microsoft.Data.Sqlite;
using ServerManager.Contracts;
using ServerManager.Core.Content;

namespace ServerManager.Infrastructure.Persistence;

/// <summary>
/// Stores what the Content Hub installed, one row per file per server. The state on a stored
/// row is not persisted: it is worked out fresh against the disk and the providers, so a
/// stale row can never claim a file is up to date when it is gone.
/// </summary>
public sealed class SqliteInstalledContentStore(SqliteConnectionFactory connectionFactory)
    : IInstalledContentStore
{
    private const string Columns = """
        ServerId, FileName, Kind, Provider, ProjectId, VersionId, ProjectName,
        InstalledVersion, MinecraftVersionAtInstall, PlatformAtInstall, ProjectUrl,
        ProviderSha512, ProviderSha256, LocalSha256, SizeBytes, InstalledAtUtc,
        RestartRequired, PreviousVersionId, PreviousFileName
        """;

    public async Task<IReadOnlyList<InstalledContent>> ListAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
    {
        var records = new List<InstalledContent>();
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {Columns}
            FROM InstalledContent
            WHERE ServerId = $serverId
            ORDER BY FileName;
            """;
        command.Parameters.AddWithValue("$serverId", serverId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(Read(reader));
        }

        return records;
    }

    public async Task<InstalledContent?> GetAsync(
        Guid serverId,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {Columns}
            FROM InstalledContent
            WHERE ServerId = $serverId AND FileName = $fileName
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$serverId", serverId.ToString());
        command.Parameters.AddWithValue("$fileName", fileName);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task UpsertAsync(
        InstalledContent record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO InstalledContent (
                ServerId, FileName, Kind, Provider, ProjectId, VersionId, ProjectName,
                InstalledVersion, MinecraftVersionAtInstall, PlatformAtInstall, ProjectUrl,
                ProviderSha512, ProviderSha256, LocalSha256, SizeBytes, InstalledAtUtc,
                RestartRequired, PreviousVersionId, PreviousFileName)
            VALUES (
                $serverId, $fileName, $kind, $provider, $projectId, $versionId, $projectName,
                $installedVersion, $minecraftVersion, $platform, $projectUrl,
                $sha512, $sha256, $localSha256, $sizeBytes, $installedAt,
                $restartRequired, $previousVersionId, $previousFileName)
            ON CONFLICT (ServerId, FileName) DO UPDATE SET
                Kind = excluded.Kind,
                Provider = excluded.Provider,
                ProjectId = excluded.ProjectId,
                VersionId = excluded.VersionId,
                ProjectName = excluded.ProjectName,
                InstalledVersion = excluded.InstalledVersion,
                MinecraftVersionAtInstall = excluded.MinecraftVersionAtInstall,
                PlatformAtInstall = excluded.PlatformAtInstall,
                ProjectUrl = excluded.ProjectUrl,
                ProviderSha512 = excluded.ProviderSha512,
                ProviderSha256 = excluded.ProviderSha256,
                LocalSha256 = excluded.LocalSha256,
                SizeBytes = excluded.SizeBytes,
                InstalledAtUtc = excluded.InstalledAtUtc,
                RestartRequired = excluded.RestartRequired,
                PreviousVersionId = excluded.PreviousVersionId,
                PreviousFileName = excluded.PreviousFileName;
            """;
        command.Parameters.AddWithValue("$serverId", record.ServerId.ToString());
        command.Parameters.AddWithValue("$fileName", record.FileName);
        command.Parameters.AddWithValue("$kind", (int)record.Kind);
        command.Parameters.AddWithValue(
            "$provider",
            record.Provider is { } provider ? (int)provider : DBNull.Value);
        command.Parameters.AddWithValue("$projectId", (object?)record.ProjectId ?? DBNull.Value);
        command.Parameters.AddWithValue("$versionId", (object?)record.VersionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$projectName", (object?)record.ProjectName ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$installedVersion",
            (object?)record.InstalledVersion ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$minecraftVersion",
            (object?)record.MinecraftVersionAtInstall ?? DBNull.Value);
        command.Parameters.AddWithValue("$platform", (int)record.PlatformAtInstall);
        command.Parameters.AddWithValue(
            "$projectUrl",
            (object?)record.ProjectUrl?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$sha512", (object?)record.ProviderSha512 ?? DBNull.Value);
        command.Parameters.AddWithValue("$sha256", (object?)record.ProviderSha256 ?? DBNull.Value);
        command.Parameters.AddWithValue("$localSha256", (object?)record.LocalSha256 ?? DBNull.Value);
        command.Parameters.AddWithValue("$sizeBytes", record.SizeBytes);
        command.Parameters.AddWithValue(
            "$installedAt",
            (record.InstalledAtUtc ?? DateTimeOffset.UtcNow).ToString("o"));
        command.Parameters.AddWithValue("$restartRequired", record.RestartRequired ? 1 : 0);
        command.Parameters.AddWithValue(
            "$previousVersionId",
            (object?)record.PreviousVersionId ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$previousFileName",
            (object?)record.PreviousFileName ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RemoveAsync(
        Guid serverId,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM InstalledContent
            WHERE ServerId = $serverId AND FileName = $fileName;
            """;
        command.Parameters.AddWithValue("$serverId", serverId.ToString());
        command.Parameters.AddWithValue("$fileName", fileName);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static InstalledContent Read(SqliteDataReader reader) =>
        new(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            InstalledContentState.UnknownVersion,
            (ContentKind)reader.GetInt32(2),
            reader.IsDBNull(3) ? null : (ContentProviderId)reader.GetInt32(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            (ServerPlatform)reader.GetInt32(9),
            reader.IsDBNull(10) || !Uri.TryCreate(reader.GetString(10), UriKind.Absolute, out var url)
                ? null
                : url,
            reader.IsDBNull(11) ? null : reader.GetString(11),
            reader.IsDBNull(12) ? null : reader.GetString(12),
            reader.IsDBNull(13) ? null : reader.GetString(13),
            DateTimeOffset.Parse(reader.GetString(15), System.Globalization.CultureInfo.InvariantCulture),
            reader.GetInt32(16) == 1,
            reader.IsDBNull(17) ? null : reader.GetString(17),
            reader.IsDBNull(18) ? null : reader.GetString(18),
            null,
            null,
            reader.GetInt64(14),
            ManagedByManager: true);
}
