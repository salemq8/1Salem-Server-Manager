namespace TsnetSmoke;

/// <summary>
/// Keeps a transport's log as evidence for the whole run. tsnet's backend lines (among them the
/// one-time <c>envknob: TS_DISABLE_PORTMAPPER="true"</c> line of a new node) reach only the
/// transport's in-memory ring, whose <c>diag</c> answer holds the newest 32 KiB, so a snapshot
/// taken only at the end would have lost them. This reads <c>diag</c> every second and appends
/// what is new; a snapshot that shares no line with what was kept means lines may have been missed
/// in between, which the log checks report.
/// </summary>
internal sealed class TransportLogCollector
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private readonly Func<Task<IReadOnlyList<string>>> _read;
    private readonly Func<IReadOnlyList<string>, Task> _keep;
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    private TransportLogCollector(Func<Task<IReadOnlyList<string>>> read, Func<IReadOnlyList<string>, Task> keep)
    {
        _read = read;
        _keep = keep;
        _loop = Task.Run(LoopAsync);
    }

    /// <summary>Snapshots in which the kept lines and the new ones did not overlap.</summary>
    public int PossibleGaps { get; private set; }

    public int Snapshots { get; private set; }

    /// <summary>Reads that failed, as they do before the transport serves its pipe and after it stopped.</summary>
    public int FailedReads { get; private set; }

    public override string ToString() =>
        $"{Snapshots} diag snapshots, {PossibleGaps} possible gap(s), {FailedReads} failed read(s)";

    public static TransportLogCollector Start(Func<Task<IReadOnlyList<string>>> read, Func<IReadOnlyList<string>, Task> keep) =>
        new(read, keep);

    /// <summary>
    /// Ends the polling and takes one last snapshot. A read in progress is let finish (each has
    /// its own time limit): one cut off mid-answer would leave the next read of that pipe out of
    /// step. It never throws: this is evidence only.
    /// </summary>
    public async Task StopAsync()
    {
        _stop.Cancel();
        await _loop;
        await TrySnapshotAsync();
    }

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            await TrySnapshotAsync();
            try
            {
                await Task.Delay(Interval, _stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    // A transport that is not up yet, or already gone, has nothing to add this time; the count of
    // failed reads goes into the log checks' detail.
    private async Task TrySnapshotAsync()
    {
        try
        {
            var lines = await _read();
            if (lines.Count == 0)
            {
                return;
            }

            if (_seen.Count > 0 && !_seen.Contains(lines[0]))
            {
                PossibleGaps++;
            }

            Snapshots++;
            await _keep(lines.Where(_seen.Add).ToList());
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            FailedReads++;
        }
    }
}
