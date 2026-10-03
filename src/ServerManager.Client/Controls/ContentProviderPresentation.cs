using ServerManager.Contracts;

namespace ServerManager.Client.Controls;

/// <summary>Never infer a site's health from another site's response or from zero search hits.</summary>
public static class ContentProviderPresentation
{
    public static IReadOnlyList<ContentProviderId> FailedProviders(ContentSearchResult result) =>
        result.ProviderStatuses is { } statuses
            ? statuses.Where(status => !status.Succeeded).Select(status => status.Provider).Distinct().ToArray()
            : result.ProviderErrors.Select(error => error.Split(':')[0])
                .Select(name => Enum.TryParse<ContentProviderId>(name, out var provider) ? (ContentProviderId?)provider : null)
                .Where(provider => provider.HasValue).Select(provider => provider!.Value).Distinct().ToArray();

    public static bool BothUnavailable(ContentSearchResult result) =>
        result.Projects.Count == 0 &&
        FailedProviders(result).Contains(ContentProviderId.Hangar) &&
        FailedProviders(result).Contains(ContentProviderId.Modrinth) &&
        !(result.ProviderStatuses?.Any(status => status.Succeeded) ?? false);
}
