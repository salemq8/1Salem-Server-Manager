using ServerManager.Core;
using ServerManager.Infrastructure.Concurrency;

namespace ServerManager.Infrastructure.Tests;

public sealed class ServerOperationCoordinatorTests
{
    [Fact]
    public async Task AcquireAsync_SameServer_SerializesConcurrentOperations()
    {
        var coordinator = new ServerOperationCoordinator();
        var serverId = Guid.NewGuid();
        var order = new List<string>();
        var firstEntered = new TaskCompletionSource();
        var releaseFirst = new TaskCompletionSource();

        var first = Task.Run(async () =>
        {
            await using var handle = await coordinator.AcquireAsync(serverId, "Start");
            lock (order)
            {
                order.Add("first-enter");
            }

            firstEntered.SetResult();
            await releaseFirst.Task;
            lock (order)
            {
                order.Add("first-exit");
            }
        });

        await firstEntered.Task;
        var second = Task.Run(async () =>
        {
            await using var handle = await coordinator.AcquireAsync(serverId, "Stop");
            lock (order)
            {
                order.Add("second-enter");
            }
        });

        // The second caller must not be able to enter while the first still holds the lock.
        await Task.Delay(100);
        Assert.DoesNotContain("second-enter", order);

        releaseFirst.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(["first-enter", "first-exit", "second-enter"], order);
    }

    [Fact]
    public async Task AcquireAsync_DifferentServers_DoNotBlockEachOther()
    {
        var coordinator = new ServerOperationCoordinator();
        var blockFirst = new TaskCompletionSource();

        var first = Task.Run(async () =>
        {
            await using var handle = await coordinator.AcquireAsync(Guid.NewGuid(), "Start");
            await blockFirst.Task;
        });

        // A completely unrelated server must be able to acquire immediately even while the
        // first server's lock is held indefinitely.
        var second = coordinator.AcquireAsync(
            Guid.NewGuid(),
            "Start",
            TimeSpan.FromMilliseconds(200));
        var handle = await second.WaitAsync(TimeSpan.FromSeconds(5));
        await handle.DisposeAsync();

        blockFirst.SetResult();
        await first;
    }

    [Fact]
    public async Task AcquireAsync_TimesOut_ThrowsServerBusyExceptionNamingTheHeldOperation()
    {
        var coordinator = new ServerOperationCoordinator();
        var serverId = Guid.NewGuid();
        var release = new TaskCompletionSource();
        var holder = Task.Run(async () =>
        {
            await using var handle = await coordinator.AcquireAsync(serverId, "Restore");
            await release.Task;
        });
        await Task.Delay(50);

        var exception = await Assert.ThrowsAsync<ServerBusyException>(() =>
            coordinator.AcquireAsync(serverId, "Start", TimeSpan.FromMilliseconds(100)));

        Assert.Equal(serverId, exception.ServerId);
        Assert.Contains("Restore", exception.Message, StringComparison.Ordinal);

        release.SetResult();
        await holder;
    }

    [Fact]
    public async Task AcquireAsync_ReleasesTheLockEvenWhenTheOperationThrows()
    {
        var coordinator = new ServerOperationCoordinator();
        var serverId = Guid.NewGuid();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var handle = await coordinator.AcquireAsync(serverId, "Start");
            throw new InvalidOperationException("simulated failure");
        });

        // The lock must not be left held by the failed operation.
        await using var next = await coordinator.AcquireAsync(
            serverId,
            "Stop",
            TimeSpan.FromMilliseconds(200));
    }

    [Fact]
    public async Task AcquireAsync_HonorsCancellationWhileWaiting()
    {
        var coordinator = new ServerOperationCoordinator();
        var serverId = Guid.NewGuid();
        var release = new TaskCompletionSource();
        var holder = Task.Run(async () =>
        {
            await using var handle = await coordinator.AcquireAsync(serverId, "Start");
            await release.Task;
        });
        await Task.Delay(50);

        using var cts = new CancellationTokenSource();
        var waiter = coordinator.AcquireAsync(
            serverId,
            "Stop",
            TimeSpan.FromSeconds(30),
            cts.Token);
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAsync<OperationCanceledException>(() => waiter);

        release.SetResult();
        await holder;
    }
}
