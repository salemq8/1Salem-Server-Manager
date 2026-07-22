using Microsoft.Data.Sqlite;
using ServerManager.Core;

namespace ServerManager.Infrastructure.Persistence;

public sealed class SqliteClientStore(SqliteConnectionFactory connectionFactory) : IClientStore
{
    public async Task UpsertAsync(
        PairedClientRecord client,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Clients (
                Id, Name, CertificateFingerprint, ProtectedCredential,
                CreatedAtUtc, LastConnectedAtUtc, RevokedAtUtc)
            VALUES (
                $id, $name, $fingerprint, $credentialHash,
                $createdAtUtc, $lastConnectedAtUtc, $revokedAtUtc)
            ON CONFLICT (Id) DO UPDATE SET
                Name = excluded.Name,
                CertificateFingerprint = excluded.CertificateFingerprint,
                ProtectedCredential = excluded.ProtectedCredential,
                LastConnectedAtUtc = excluded.LastConnectedAtUtc,
                RevokedAtUtc = excluded.RevokedAtUtc;
            """;
        Bind(command, client);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<PairedClientRecord?> FindByCredentialHashAsync(
        string credentialHash,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialHash);
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Name, CertificateFingerprint, ProtectedCredential,
                   CreatedAtUtc, LastConnectedAtUtc, RevokedAtUtc
            FROM Clients
            WHERE ProtectedCredential = $credentialHash
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$credentialHash", credentialHash);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<IReadOnlyList<PairedClientRecord>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var clients = new List<PairedClientRecord>();
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Name, CertificateFingerprint, ProtectedCredential,
                   CreatedAtUtc, LastConnectedAtUtc, RevokedAtUtc
            FROM Clients
            ORDER BY Name COLLATE NOCASE;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            clients.Add(Read(reader));
        }

        return clients;
    }

    public async Task RenameAsync(
        Guid clientId,
        string name,
        CancellationToken cancellationToken = default)
    {
        ValidateName(name);
        await ExecuteUpdateAsync(
            "UPDATE Clients SET Name = $value WHERE Id = $id;",
            clientId,
            "$value",
            name.Trim(),
            cancellationToken);
    }

    public Task RevokeAsync(
        Guid clientId,
        DateTimeOffset revokedAtUtc,
        CancellationToken cancellationToken = default) =>
        ExecuteUpdateAsync(
            "UPDATE Clients SET RevokedAtUtc = $value WHERE Id = $id;",
            clientId,
            "$value",
            revokedAtUtc.ToString("O"),
            cancellationToken);

    public Task MarkConnectedAsync(
        Guid clientId,
        DateTimeOffset connectedAtUtc,
        CancellationToken cancellationToken = default) =>
        ExecuteUpdateAsync(
            "UPDATE Clients SET LastConnectedAtUtc = $value WHERE Id = $id;",
            clientId,
            "$value",
            connectedAtUtc.ToString("O"),
            cancellationToken);

    private async Task ExecuteUpdateAsync(
        string sql,
        Guid clientId,
        string valueParameter,
        object value,
        CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", clientId.ToString());
        command.Parameters.AddWithValue(valueParameter, value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void Bind(SqliteCommand command, PairedClientRecord client)
    {
        command.Parameters.AddWithValue("$id", client.Id.ToString());
        command.Parameters.AddWithValue("$name", client.Name);
        command.Parameters.AddWithValue("$fingerprint", client.CertificateFingerprint);
        command.Parameters.AddWithValue("$credentialHash", client.CredentialHash);
        command.Parameters.AddWithValue("$createdAtUtc", client.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue(
            "$lastConnectedAtUtc",
            client.LastConnectedAtUtc?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(
            "$revokedAtUtc",
            client.RevokedAtUtc?.ToString("O") ?? (object)DBNull.Value);
    }

    private static PairedClientRecord Read(SqliteDataReader reader) =>
        new(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            DateTimeOffset.Parse(reader.GetString(4)),
            reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5)),
            reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6)));

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 100)
        {
            throw new ArgumentException(
                "Client names must contain 1 to 100 characters.",
                nameof(name));
        }
    }
}
