using Microsoft.Extensions.Logging;
using ServerManager.Contracts;
using ServerManager.Core.Content;

namespace ServerManager.Infrastructure.Content;

/// <summary>
/// Searches every provider that can serve this server and merges the answers. One provider
/// being down never blanks the page: its failure is reported alongside the results that did
/// arrive.
/// </summary>
public sealed class ContentCatalogService(
    IEnumerable<IContentProvider> providers,
    ILogger<ContentCatalogService> logger)
{
    private readonly IReadOnlyList<IContentProvider> _providers = providers.ToArray();

    public IContentProvider? Find(ContentProviderId id) =>
        _providers.FirstOrDefault(provider => provider.Id == id);

    public async Task<ContentSearchResult> SearchAsync(
        ContentSearchRequest request,
        ServerContentProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(profile);

        // Plugins need a plugin-capable server; data and resource packs work on any
        // Minecraft server, including Vanilla, which is why this asks the type rather than
        // the plugin flag.
        if (!ContentTypePolicy.IsSupportedBy(request.Kind, profile))
        {
            return new ContentSearchResult([], request.Offset, request.Limit, 0, []);
        }

        var selected = _providers
            .Where(provider => request.Provider is null || provider.Id == request.Provider)
            .Where(provider => ContentTypePolicy.IsServedBy(request.Kind, provider.Id))
            .Where(provider => provider.CanServe(profile, request.Kind))
            .ToArray();

        var tasks = selected.Select(async provider =>
        {
            try
            {
                return (provider.Id, Result: await provider.SearchAsync(request, profile, cancellationToken), Error: (string?)null);
            }
            catch (ContentProviderException exception)
            {
                logger.LogWarning(
                    exception,
                    "Content search failed for {Provider}: {Code}.",
                    provider.Id,
                    exception.ErrorCode);
                return (provider.Id, Result: Empty(request), Error: exception.ErrorCode);
            }
        });

        var outcomes = await Task.WhenAll(tasks);
        var errors = outcomes
            .Where(outcome => outcome.Error is not null)
            .Select(outcome => $"{outcome.Id}:{outcome.Error}")
            .ToArray();

        // Interleave the providers so neither one owns the top of the list. The same plugin
        // published on both sites stays as two entries, each labelled with its source,
        // because only a provider's own ids prove identity and nothing here can prove that
        // two ids are the same project.
        var perProvider = outcomes.Select(outcome => outcome.Result.Projects).ToArray();
        var merged = new List<ContentProject>();
        for (var index = 0; merged.Count < request.Limit; index++)
        {
            var added = false;
            foreach (var list in perProvider)
            {
                if (index < list.Count)
                {
                    merged.Add(list[index]);
                    added = true;
                    if (merged.Count == request.Limit)
                    {
                        break;
                    }
                }
            }

            if (!added)
            {
                break;
            }
        }

        return new ContentSearchResult(
            merged,
            request.Offset,
            request.Limit,
            outcomes.Sum(outcome => outcome.Result.TotalHits),
            errors);
    }

    public async Task<(ContentProject? Project, ContentVersion? Latest, IReadOnlyList<ContentVersion> Versions)>
        GetProjectAsync(
            ContentProviderId providerId,
            string projectId,
            ServerContentProfile profile,
            ContentKind kind = ContentKind.Plugin,
            CancellationToken cancellationToken = default)
    {
        var provider = Find(providerId);
        if (provider is null || !provider.CanServe(profile, kind))
        {
            return (null, null, []);
        }

        var project = await provider.GetProjectAsync(projectId, profile, kind, cancellationToken);
        var versions = await provider.GetVersionsAsync(projectId, profile, kind, cancellationToken);
        var latest = PluginCompatibilityPolicy.SelectBest(versions, profile, false, kind);
        return (project, latest, versions);
    }

    private static ContentSearchResult Empty(ContentSearchRequest request) =>
        new([], request.Offset, request.Limit, 0, []);
}


