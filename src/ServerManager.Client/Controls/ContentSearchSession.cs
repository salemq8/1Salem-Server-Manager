using ServerManager.Contracts;
using ServerManager.Core.Content;

namespace ServerManager.Client.Controls;

/// <summary>What the Discover filters say, read once per search.</summary>
public sealed record ContentSearchInputs(
    Guid ServerId,
    ContentKind Kind,
    string? Text,
    ContentSortOrder Sort,
    string? Provider,
    bool CompatibleOnly,
    string? Platform,
    int Limit = 30);

/// <summary>
/// The rules behind Discover's searching, kept out of the WPF control so they can be tested:
/// one search per real change of filters, the newest search always wins, and the dashboard's
/// regular refreshes never restart anything unless a different server was selected.
/// </summary>
public sealed class ContentSearchSession
{
    private string? _contextKey;
    private string? _current;
    private int _generation;

    /// <summary>The query string for the Agent's search endpoint.</summary>
    public static string BuildQuery(ContentSearchInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var query = new List<string>
        {
            $"sort={inputs.Sort}",
            $"kind={inputs.Kind}",
            $"compatibleOnly={inputs.CompatibleOnly.ToString().ToLowerInvariant()}",
            $"limit={inputs.Limit}"
        };
        if (!string.IsNullOrWhiteSpace(inputs.Text))
        {
            query.Add($"query={Uri.EscapeDataString(inputs.Text.Trim())}");
        }

        if (inputs.Provider is { Length: > 0 } provider)
        {
            query.Add($"provider={provider}");
        }

        if (ContentPlatformFilter.Options(inputs.Kind).Count > 0)
        {
            query.Add($"platform={ContentPlatformFilter.Normalize(inputs.Kind, inputs.Platform)}");
        }

        return string.Join('&', query);
    }

    /// <summary>
    /// True only when the selected server, or what it runs, is different from last time. The
    /// dashboard feed reports every few seconds; those reports alone must not reload anything.
    /// </summary>
    public bool ContextChanged(string contextKey)
    {
        if (string.Equals(contextKey, _contextKey, StringComparison.Ordinal))
        {
            return false;
        }

        _contextKey = contextKey;
        _current = null;
        return true;
    }

    /// <summary>
    /// Starts a search unless the very same one is already current. Returns the generation
    /// that its reply must still hold to be shown.
    /// </summary>
    public bool TryBegin(string request, bool force, out int generation)
    {
        if (!force && string.Equals(request, _current, StringComparison.Ordinal))
        {
            generation = _generation;
            return false;
        }

        _current = request;
        generation = ++_generation;
        return true;
    }

    /// <summary>Whether a reply for <paramref name="generation"/> is still the newest one.</summary>
    public bool IsCurrent(int generation) => generation == _generation;

    /// <summary>After a failure the same search may be asked again.</summary>
    public void Forget(int generation)
    {
        if (IsCurrent(generation))
        {
            _current = null;
        }
    }

    /// <summary>Makes any reply still on its way stale, for example when the type changes.</summary>
    public void Invalidate()
    {
        _current = null;
        _generation++;
    }
}

/// <summary>
/// Runs an action once typing pauses. Each call replaces the previous pending one; only the
/// last call within <see cref="Delay"/> runs. Awaiting on the UI thread keeps the action there.
/// </summary>
public sealed class Debouncer(TimeSpan delay)
{
    private readonly object _gate = new();
    private CancellationTokenSource? _pending;

    public TimeSpan Delay { get; } = delay;

    public void Cancel()
    {
        lock (_gate)
        {
            _pending?.Cancel();
            _pending = null;
        }
    }

    public async Task RunAsync(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        CancellationTokenSource mine;
        lock (_gate)
        {
            _pending?.Cancel();
            mine = new CancellationTokenSource();
            _pending = mine;
        }

        try
        {
            await Task.Delay(Delay, mine.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        lock (_gate)
        {
            if (!ReferenceEquals(_pending, mine))
            {
                return;
            }

            _pending = null;
        }

        await action();
    }
}
