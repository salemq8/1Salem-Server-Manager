using Microsoft.Extensions.Logging.Abstractions;
using ServerManager.Contracts;
using ServerManager.Core.Content;
using ServerManager.Infrastructure.Content;

namespace ServerManager.Infrastructure.Tests;

public sealed class ContentProviderStatusTests
{
    [Fact]
    public async Task OneFailure_KeepsOtherProvidersResultsAndIndependentStatus()
    {
        var catalog = Catalog(new Provider(ContentProviderId.Hangar, fail: true), new Provider(ContentProviderId.Modrinth));
        var result = await catalog.SearchAsync(new(), Profile());
        Assert.Equal(ContentProviderId.Modrinth, Assert.Single(result.Projects).Provider);
        Assert.Equal("Hangar:Offline", Assert.Single(result.ProviderErrors));
        Assert.Contains(result.ProviderStatuses!, status => status.Provider == ContentProviderId.Hangar && !status.Succeeded);
        Assert.Contains(result.ProviderStatuses!, status => status.Provider == ContentProviderId.Modrinth && status.Succeeded);
    }

    [Fact]
    public async Task EmptySuccess_IsRecordedAsSuccess()
    {
        var result = await Catalog(new Provider(ContentProviderId.Modrinth, empty: true)).SearchAsync(new(), Profile());
        Assert.Empty(result.Projects);
        Assert.True(Assert.Single(result.ProviderStatuses!).Succeeded);
    }

    [Fact]
    public async Task CanceledRequest_IsNotAProviderFailure()
    {
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Catalog(new Provider(ContentProviderId.Modrinth, fail: true)).SearchAsync(new(), Profile(), canceled.Token));
    }

    [Fact]
    public async Task Vanilla_CanReadPluginDetails_ButHasNoInstallableVersion()
    {
        var profile = Profile() with { Platform = ServerPlatform.Vanilla, SupportsPlugins = false };
        var (project, latest, _) = await Catalog(new Provider(ContentProviderId.Modrinth)).GetProjectAsync(ContentProviderId.Modrinth, "id", profile);
        Assert.NotNull(project);
        Assert.Null(latest);
    }

    private static ContentCatalogService Catalog(params IContentProvider[] providers) => new(providers, NullLogger<ContentCatalogService>.Instance);
    private static ServerContentProfile Profile() => new(Guid.NewGuid(), GameType.Minecraft, ServerPlatform.Paper, "26.3", null, "C:\\fixture", "C:\\fixture\\plugins", true, false);
    private sealed class Provider(ContentProviderId id, bool fail = false, bool empty = false) : IContentProvider
    {
        public ContentProviderId Id => id;
        private ContentProject Project => new(Id, "id", "slug", "Plugin", null, null, null, null, null, ["paper"], ["26.3"]);
        public bool CanServe(ServerContentProfile profile) => profile.SupportsPlugins;
        public Task<ContentSearchResult> SearchAsync(ContentSearchRequest request, ServerContentProfile profile, CancellationToken cancellationToken = default)
        {
            if (fail) throw new ContentProviderException(Id, "Offline", "fixture unavailable");
            return Task.FromResult(new ContentSearchResult(empty ? [] : [Project], 0, 30, empty ? 0 : 1, []));
        }
        public Task<ContentProject?> GetProjectAsync(string projectId, ServerContentProfile profile, ContentKind kind = ContentKind.Plugin, CancellationToken cancellationToken = default) => Task.FromResult<ContentProject?>(Project);
        public Task<IReadOnlyList<ContentVersion>> GetVersionsAsync(string projectId, ServerContentProfile profile, ContentKind kind = ContentKind.Plugin, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ContentVersion>>([]);
        public Task<ContentVersion?> ResolveCompatibleVersionAsync(string projectId, ServerContentProfile profile, bool allowPrerelease = false, ContentKind kind = ContentKind.Plugin, CancellationToken cancellationToken = default) => Task.FromResult<ContentVersion?>(null);
        public Task<ContentIdentification?> IdentifyAsync(ContentFileDigests digests, CancellationToken cancellationToken = default) => Task.FromResult<ContentIdentification?>(null);
        public Task DownloadAsync(ContentVersion version, string stagingFilePath, IProgress<ContentInstallProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
