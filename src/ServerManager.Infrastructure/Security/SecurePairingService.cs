using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ServerManager.Core;

namespace ServerManager.Infrastructure.Security;

public sealed class PairingRateLimitException(string message) : InvalidOperationException(message);

public sealed class SecurePairingService(
    IClientStore clientStore,
    IAuditLogStore auditLogStore,
    AgentCertificateIdentity certificateIdentity,
    TimeProvider timeProvider) : IPairingService
{
    private const int MaximumAttemptsPerWindow = 5;
    private static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RateLimitWindow = TimeSpan.FromMinutes(1);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _challenges = new();
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _attempts = new();

    public Task<PairingChallenge> CreateChallengeAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CleanupExpiredChallenges();
        string code;
        do
        {
            code = RandomNumberGenerator.GetInt32(0, 1_000_000)
                .ToString("D6", CultureInfo.InvariantCulture);
        }
        while (!_challenges.TryAdd(code, GetUtcNow().Add(ChallengeLifetime)));

        return Task.FromResult(new PairingChallenge(code, _challenges[code]));
    }

    public async Task<PairingResult> CompleteAsync(
        string code,
        string clientName,
        string clientAddress,
        CancellationToken cancellationToken = default)
    {
        ValidateClientName(clientName);
        ValidateAttempt(clientAddress);
        cancellationToken.ThrowIfCancellationRequested();

        var now = GetUtcNow();
        if (string.IsNullOrWhiteSpace(code) ||
            code.Length != 6 ||
            !_challenges.TryRemove(code, out var expiresAt) ||
            expiresAt <= now)
        {
            await auditLogStore.WriteAsync(
                clientAddress,
                "PairingFailed",
                clientName,
                false,
                "Invalid or expired pairing code.",
                cancellationToken);
            throw new UnauthorizedAccessException("The pairing code is invalid or expired.");
        }

        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        string token;
        try
        {
            token = Convert.ToBase64String(tokenBytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }
        finally
        {
            CryptographicOperations.ZeroMemory(tokenBytes);
        }

        var record = new PairedClientRecord(
            Guid.NewGuid(),
            clientName.Trim(),
            certificateIdentity.Sha256Fingerprint,
            HashCredential(token),
            now,
            null,
            null);
        await clientStore.UpsertAsync(record, cancellationToken);
        await auditLogStore.WriteAsync(
            clientAddress,
            "ClientPaired",
            record.Id.ToString(),
            true,
            record.Name,
            cancellationToken);
        return new PairingResult(
            record.Id,
            record.Name,
            token,
            certificateIdentity.Sha256Fingerprint);
    }

    public async Task RevokeAsync(
        Guid clientId,
        CancellationToken cancellationToken = default)
    {
        await clientStore.RevokeAsync(clientId, GetUtcNow(), cancellationToken);
        await auditLogStore.WriteAsync(
            "Agent",
            "ClientRevoked",
            clientId.ToString(),
            true,
            cancellationToken: cancellationToken);
    }

    public async Task RenameAsync(
        Guid clientId,
        string name,
        CancellationToken cancellationToken = default)
    {
        ValidateClientName(name);
        await clientStore.RenameAsync(clientId, name.Trim(), cancellationToken);
        await auditLogStore.WriteAsync(
            "Agent",
            "ClientRenamed",
            clientId.ToString(),
            true,
            name.Trim(),
            cancellationToken);
    }

    public Task<IReadOnlyList<PairedClientRecord>> ListClientsAsync(
        CancellationToken cancellationToken = default) =>
        clientStore.ListAsync(cancellationToken);

    public async Task<PairedClientRecord?> ValidateCredentialAsync(
        string credential,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(credential) || credential.Length > 200)
        {
            return null;
        }

        var client = await clientStore.FindByCredentialHashAsync(
            HashCredential(credential),
            cancellationToken);
        if (client is null || client.RevokedAtUtc is not null)
        {
            return null;
        }

        await clientStore.MarkConnectedAsync(client.Id, GetUtcNow(), cancellationToken);
        return client;
    }

    private static string HashCredential(string credential) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(credential)));

    private void ValidateAttempt(string clientAddress)
    {
        clientAddress = string.IsNullOrWhiteSpace(clientAddress)
            ? "unknown"
            : clientAddress;
        var now = GetUtcNow();
        var attempts = _attempts.GetOrAdd(clientAddress, _ => new Queue<DateTimeOffset>());
        lock (attempts)
        {
            while (attempts.TryPeek(out var first) && now - first >= RateLimitWindow)
            {
                attempts.Dequeue();
            }

            if (attempts.Count >= MaximumAttemptsPerWindow)
            {
                throw new PairingRateLimitException(
                    "Too many pairing attempts. Wait one minute and try again.");
            }

            attempts.Enqueue(now);
        }
    }

    private void CleanupExpiredChallenges()
    {
        var now = GetUtcNow();
        foreach (var challenge in _challenges.Where(item => item.Value <= now))
        {
            _challenges.TryRemove(challenge.Key, out _);
        }
    }

    private DateTimeOffset GetUtcNow() => timeProvider.GetUtcNow();

    private static void ValidateClientName(string clientName)
    {
        if (string.IsNullOrWhiteSpace(clientName) || clientName.Trim().Length > 100)
        {
            throw new ArgumentException(
                "Client names must contain 1 to 100 characters.",
                nameof(clientName));
        }
    }
}
