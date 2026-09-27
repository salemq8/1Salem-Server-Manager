namespace ServerManager.Client.Controls;

/// <summary>Rejects delayed replies from a previously selected server, including A → B → A.</summary>
public sealed class ConnectServerRequestTracker
{
    private long _generation;
    public Guid ServerId { get; private set; }

    public bool Select(Guid serverId)
    {
        if (ServerId == serverId)
        {
            return false;
        }

        ServerId = serverId;
        _generation++;
        return true;
    }

    public ConnectServerRequest Capture(Guid serverId)
    {
        Select(serverId);
        return new(ServerId, _generation);
    }

    public bool IsCurrent(ConnectServerRequest request) =>
        request.ServerId == ServerId && request.Generation == _generation;
}

public readonly record struct ConnectServerRequest(Guid ServerId, long Generation);

/// <summary>Unsaved local nicknames survive status refreshes and failed saves.</summary>
public sealed class ConnectNicknameDrafts
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public string Get(string membershipId, string savedValue) =>
        _values.GetValueOrDefault(membershipId, savedValue);

    public void Set(string membershipId, string value) => _values[membershipId] = value;

    public void Saved(string membershipId, string submittedValue)
    {
        // The owner may have continued typing while the save was in flight.
        if (_values.TryGetValue(membershipId, out var current) && current == submittedValue)
        {
            _values.Remove(membershipId);
        }
    }

    public void Retain(IEnumerable<string> membershipIds)
    {
        var live = membershipIds.ToHashSet(StringComparer.Ordinal);
        foreach (var id in _values.Keys.Where(id => !live.Contains(id)).ToArray())
        {
            _values.Remove(id);
        }
    }
}
