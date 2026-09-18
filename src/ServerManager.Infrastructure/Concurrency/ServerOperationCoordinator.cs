using System.Collections.Concurrent;
using ServerManager.Core;

namespace ServerManager.Infrastructure.Concurrency;

/// <summary>
/// Per-server mutual exclusion shared by every service that can mutate a registered server's
/// live process or files (start/stop/restart, backup/restore, and so on). One instance must be
/// registered as a singleton and injected into all of them for the guarantee to hold -- a
/// service that constructs its own instance only protects itself against itself.
/// </summary>
public sealed class ServerOperationCoordinator : IServerOperationCoordinator
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);
    private readonly ConcurrentDictionary<Guid, ServerLock> _locks = new();

    public async Task<IAsyncDisposable> AcquireAsync(
        Guid serverId,
        string operationName,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var serverLock = _locks.GetOrAdd(serverId, _ => new ServerLock());
        var acquired = await serverLock.Semaphore.WaitAsync(
            timeout ?? DefaultTimeout,
            cancellationToken);
        if (!acquired)
        {
            throw new ServerBusyException(serverId, serverLock.CurrentOperation ?? "another operation");
        }

        serverLock.CurrentOperation = operationName;
        return new Releaser(serverLock);
    }

    private sealed class ServerLock
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public string? CurrentOperation { get; set; }
    }

    private sealed class Releaser(ServerLock serverLock) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            serverLock.CurrentOperation = null;
            serverLock.Semaphore.Release();
            return ValueTask.CompletedTask;
        }
    }
}
