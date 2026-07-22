using System.Runtime.CompilerServices;
using System.Threading.Channels;
using ServerManager.Core;

namespace ServerManager.Infrastructure.Processes;

internal sealed class ProcessLogBuffer(int maximumEntries = 2_000)
{
    private readonly object _sync = new();
    private readonly Queue<LogEntry> _entries = new(maximumEntries);
    private readonly List<Channel<LogEntry>> _subscribers = [];

    public void Publish(LogEntry entry)
    {
        lock (_sync)
        {
            while (_entries.Count >= maximumEntries)
            {
                _entries.Dequeue();
            }

            _entries.Enqueue(entry);
            foreach (var subscriber in _subscribers)
            {
                subscriber.Writer.TryWrite(entry);
            }
        }
    }

    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (_sync)
        {
            return _entries.ToArray();
        }
    }

    public async IAsyncEnumerable<LogEntry> SubscribeAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = Channel.CreateBounded<LogEntry>(
            new BoundedChannelOptions(512)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest
            });
        LogEntry[] snapshot;

        lock (_sync)
        {
            snapshot = _entries.ToArray();
            _subscribers.Add(channel);
        }

        try
        {
            foreach (var entry in snapshot)
            {
                yield return entry;
            }

            await foreach (var entry in channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return entry;
            }
        }
        finally
        {
            lock (_sync)
            {
                _subscribers.Remove(channel);
                channel.Writer.TryComplete();
            }
        }
    }
}

