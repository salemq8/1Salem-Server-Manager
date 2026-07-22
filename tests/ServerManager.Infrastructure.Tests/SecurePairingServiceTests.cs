using System.Security.Cryptography;
using System.Text;
using ServerManager.Core;
using ServerManager.Infrastructure.Security;

namespace ServerManager.Infrastructure.Tests;

public sealed class SecurePairingServiceTests
{
    [Fact]
    public async Task Complete_UsesCodeOnce_AndStoresOnlyCredentialHash()
    {
        var store = new InMemoryClientStore();
        var service = CreateService(store, new AdjustableTimeProvider());
        var challenge = await service.CreateChallengeAsync();

        var result = await service.CompleteAsync(
            challenge.Code,
            "Living Room PC",
            "192.168.1.10");

        var saved = Assert.Single(await store.ListAsync());
        Assert.NotEqual(result.ProtectedCredential, saved.CredentialHash);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(result.ProtectedCredential))),
            saved.CredentialHash);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => service.CompleteAsync(
                challenge.Code,
                "Second PC",
                "192.168.1.11"));
    }

    [Fact]
    public async Task Complete_RejectsExpiredCode()
    {
        var time = new AdjustableTimeProvider();
        var service = CreateService(new InMemoryClientStore(), time);
        var challenge = await service.CreateChallengeAsync();
        time.Advance(TimeSpan.FromMinutes(5).Add(TimeSpan.FromSeconds(1)));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => service.CompleteAsync(
                challenge.Code,
                "Expired PC",
                "192.168.1.12"));
    }

    [Fact]
    public async Task RevokedCredential_CannotAuthenticate()
    {
        var service = CreateService(
            new InMemoryClientStore(),
            new AdjustableTimeProvider());
        var challenge = await service.CreateChallengeAsync();
        var result = await service.CompleteAsync(
            challenge.Code,
            "Office PC",
            "192.168.1.13");

        Assert.NotNull(await service.ValidateCredentialAsync(result.ProtectedCredential));
        await service.RevokeAsync(result.ClientId);

        Assert.Null(await service.ValidateCredentialAsync(result.ProtectedCredential));
    }

    [Fact]
    public async Task Complete_RateLimitsRepeatedAddress()
    {
        var service = CreateService(
            new InMemoryClientStore(),
            new AdjustableTimeProvider());
        for (var index = 0; index < 5; index++)
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(
                () => service.CompleteAsync(
                    "000000",
                    "Unknown PC",
                    "192.168.1.14"));
        }

        await Assert.ThrowsAsync<PairingRateLimitException>(
            () => service.CompleteAsync(
                "000000",
                "Unknown PC",
                "192.168.1.14"));
    }

    private static SecurePairingService CreateService(
        IClientStore store,
        TimeProvider timeProvider) =>
        new(
            store,
            new InMemoryAuditLogStore(),
            new AgentCertificateIdentity(null!, "ABC123"),
            timeProvider);

    private sealed class AdjustableTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 7, 17, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }

    private sealed class InMemoryClientStore : IClientStore
    {
        private readonly Dictionary<Guid, PairedClientRecord> _clients = [];

        public Task UpsertAsync(
            PairedClientRecord client,
            CancellationToken cancellationToken = default)
        {
            _clients[client.Id] = client;
            return Task.CompletedTask;
        }

        public Task<PairedClientRecord?> FindByCredentialHashAsync(
            string credentialHash,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                _clients.Values.FirstOrDefault(
                    client => client.CredentialHash == credentialHash));

        public Task<IReadOnlyList<PairedClientRecord>> ListAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PairedClientRecord>>([.. _clients.Values]);

        public Task RenameAsync(
            Guid clientId,
            string name,
            CancellationToken cancellationToken = default)
        {
            _clients[clientId] = _clients[clientId] with { Name = name };
            return Task.CompletedTask;
        }

        public Task RevokeAsync(
            Guid clientId,
            DateTimeOffset revokedAtUtc,
            CancellationToken cancellationToken = default)
        {
            _clients[clientId] = _clients[clientId] with { RevokedAtUtc = revokedAtUtc };
            return Task.CompletedTask;
        }

        public Task MarkConnectedAsync(
            Guid clientId,
            DateTimeOffset connectedAtUtc,
            CancellationToken cancellationToken = default)
        {
            _clients[clientId] = _clients[clientId] with
            {
                LastConnectedAtUtc = connectedAtUtc
            };
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryAuditLogStore : IAuditLogStore
    {
        public Task WriteAsync(
            string actor,
            string action,
            string target,
            bool succeeded,
            string? detail = null,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
