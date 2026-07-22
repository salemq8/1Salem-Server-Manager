namespace ServerManager.Core;

public static class NetworkAdapterSelection
{
    public static NetworkAdapterSnapshot? Select(
        IReadOnlyList<NetworkAdapterSnapshot> adapters,
        string? preferredAdapterId)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        return adapters.FirstOrDefault(adapter =>
                   !string.IsNullOrWhiteSpace(preferredAdapterId) &&
                   adapter.Id.Equals(
                       preferredAdapterId,
                       StringComparison.OrdinalIgnoreCase)) ??
               adapters.FirstOrDefault(adapter => adapter.HasDefaultGateway) ??
               adapters.FirstOrDefault();
    }
}
