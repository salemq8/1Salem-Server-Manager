using System.IO.Compression;
using Microsoft.Extensions.Logging.Abstractions;
using ServerManager.Contracts;
using ServerManager.Core.Content;
using ServerManager.Infrastructure.Content;

namespace ServerManager.Infrastructure.Tests;

/// <summary>
/// A modpack is the managed unit for the server it built: it appears under Installed, and a
/// newer release is classified before anything is offered. A pack that would change the
/// Minecraft version or the loader is a server migration, not an update.
/// </summary>
public sealed class ModpackLifecycleTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"1salem-packlife-{Guid.NewGuid():N}");

    private readonly Guid _serverId = Guid.NewGuid();

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task TheServersModpackIsListedAsOneItem()
    {
        var harness = await BuildAsync(newestMinecraft: "1.21.8", newestLoader: "fabric", newest: "1.0.0");

        var listed = await harness.Installed.ListAsync(harness.Profile);

        var pack = Assert.Single(listed, item => item.Kind == ContentKind.Modpack);
        Assert.Equal("Fabulously Optimized", pack.ProjectName);
        Assert.Equal("1.0.0", pack.InstalledVersion);
        Assert.Equal("1.21.8", pack.MinecraftVersionAtInstall);
        Assert.Equal("fabric", pack.Loader);
        Assert.Equal("0.16.9", pack.LoaderVersion);
        Assert.Equal(InstalledContentState.UpToDate, pack.State);

        // The mods inside the pack are not separate marketplace items.
        Assert.Single(listed);
    }

    [Fact]
    public async Task ANewerPackForTheSameEnvironmentIsAnUpdate()
    {
        var harness = await BuildAsync(newestMinecraft: "1.21.8", newestLoader: "fabric", newest: "2.0.0");

        var listed = await harness.Installed.ListAsync(harness.Profile, checkForUpdates: true);

        var pack = Assert.Single(listed, item => item.Kind == ContentKind.Modpack);
        Assert.Equal(InstalledContentState.UpdateAvailable, pack.State);
        Assert.Equal("2.0.0", pack.AvailableVersionNumber);
    }

    [Fact]
    public async Task ANewerPackOnADifferentMinecraftVersionNeedsAMigration()
    {
        var harness = await BuildAsync(newestMinecraft: "1.21.11", newestLoader: "fabric", newest: "3.0.0");

        var listed = await harness.Installed.ListAsync(harness.Profile, checkForUpdates: true);

        var pack = Assert.Single(listed, item => item.Kind == ContentKind.Modpack);
        Assert.Equal(InstalledContentState.RequiresServerMigration, pack.State);
        Assert.Equal("3.0.0", pack.AvailableVersionNumber);
    }

    [Fact]
    public async Task ANewerPackOnADifferentLoaderNeedsAMigrationToo()
    {
        var harness = await BuildAsync(newestMinecraft: "1.21.8", newestLoader: "neoforge", newest: "4.0.0");

        var listed = await harness.Installed.ListAsync(harness.Profile, checkForUpdates: true);

        Assert.Equal(
            InstalledContentState.RequiresServerMigration,
            Assert.Single(listed, item => item.Kind == ContentKind.Modpack).State);
    }

    [Fact]
    public async Task TheSamePackVersionIsNotAnUpdate()
    {
        var harness = await BuildAsync(
            newestMinecraft: "1.21.8",
            newestLoader: "fabric",
            newest: "1.0.0",
            newestIsInstalled: true);

        var listed = await harness.Installed.ListAsync(harness.Profile, checkForUpdates: true);

        Assert.Equal(
            InstalledContentState.UpToDate,
            Assert.Single(listed, item => item.Kind == ContentKind.Modpack).State);
    }

    // ---- helpers ---------------------------------------------------------------------------

    private async Task<Harness> BuildAsync(
        string newestMinecraft,
        string newestLoader,
        string newest,
        bool newestIsInstalled = false)
    {
        var serverRoot = Path.Combine(_root, $"server-{Guid.NewGuid():N}");
        var packDirectory = ContentPathPolicy.ResolveModpackDirectory(serverRoot);
        Directory.CreateDirectory(packDirectory);
        var packPath = Path.Combine(packDirectory, "Fabulously.mrpack");
        using (var archive = ZipFile.Open(packPath, ZipArchiveMode.Create))
        {
            using var entry = new StreamWriter(archive.CreateEntry("modrinth.index.json").Open());
            entry.Write("""{"formatVersion":1,"game":"minecraft","versionId":"1.0.0","name":"x"}""");
        }

        var digests = await FileDigests.ComputeAsync(packPath, true);
        var store = new InMemoryLifecycleStore();
        await store.UpsertAsync(new InstalledContent(
            _serverId,
            "Fabulously.mrpack",
            InstalledContentState.UpToDate,
            ContentKind.Modpack,
            ContentProviderId.Modrinth,
            "1KVo5zza",
            newestIsInstalled ? "version-newest" : "version-installed",
            "Fabulously Optimized",
            "1.0.0",
            "1.21.8",
            ServerPlatform.Unknown,
            LocalSha256: digests.Sha256,
            InstalledAtUtc: DateTimeOffset.UtcNow,
            ManagedByManager: true,
            Loader: "fabric",
            LoaderVersion: "0.16.9"));

        var provider = new PackVersionProvider(newestMinecraft, newestLoader, newest);
        var catalog = new ContentCatalogService([provider], NullLogger<ContentCatalogService>.Instance);
        return new Harness(
            new ServerContentProfile(
                _serverId,
                GameType.Minecraft,
                ServerPlatform.Unknown,
                "1.21.8",
                null,
                serverRoot,
                Path.Combine(serverRoot, "plugins"),
                SupportsPlugins: false,
                IsRunning: false),
            store,
            new InstalledContentService(catalog, store, NullLogger<InstalledContentService>.Instance));
    }

    private sealed record Harness(
        ServerContentProfile Profile,
        InMemoryLifecycleStore Store,
        InstalledContentService Installed);

    private sealed class PackVersionProvider(string minecraft, string loader, string newestVersion)
        : IContentProvider
    {
        public ContentProviderId Id => ContentProviderId.Modrinth;

        public bool CanServe(ServerContentProfile profile) => true;

        public bool CanServe(ServerContentProfile profile, ContentKind kind) => true;

        public Task<ContentSearchResult> SearchAsync(
            ContentSearchRequest request,
            ServerContentProfile profile,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ContentSearchResult([], 0, request.Limit, 0, []));

        public Task<ContentProject?> GetProjectAsync(
            string projectId,
            ServerContentProfile profile,
            ContentKind kind = ContentKind.Plugin,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ContentProject?>(null);

        public Task<IReadOnlyList<ContentVersion>> GetVersionsAsync(
            string projectId,
            ServerContentProfile profile,
            ContentKind kind = ContentKind.Plugin,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ContentVersion>>(
            [
                new ContentVersion(
                    Id,
                    projectId,
                    "version-newest",
                    newestVersion,
                    ContentReleaseChannel.Release,
                    DateTimeOffset.UtcNow,
                    [loader],
                    [minecraft],
                    new ContentFile("pack.mrpack", new Uri("https://cdn.modrinth.com/p.mrpack"), 10),
                    [])
            ]);

        public Task<ContentVersion?> ResolveCompatibleVersionAsync(
            string projectId,
            ServerContentProfile profile,
            bool allowPrerelease = false,
            ContentKind kind = ContentKind.Plugin,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ContentVersion?>(null);

        public Task<ContentIdentification?> IdentifyAsync(
            ContentFileDigests digests,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ContentIdentification?>(null);

        public Task DownloadAsync(
            ContentVersion version,
            string stagingFilePath,
            IProgress<ContentInstallProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class InMemoryLifecycleStore : IInstalledContentStore
    {
        private readonly Dictionary<(Guid, string), InstalledContent> _records = [];

        public Task<IReadOnlyList<InstalledContent>> ListAsync(
            Guid serverId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<InstalledContent>>(
                _records.Where(pair => pair.Key.Item1 == serverId).Select(pair => pair.Value).ToArray());

        public Task<InstalledContent?> GetAsync(
            Guid serverId,
            string fileName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_records.TryGetValue((serverId, fileName), out var record) ? record : null);

        public Task UpsertAsync(InstalledContent record, CancellationToken cancellationToken = default)
        {
            _records[(record.ServerId, record.FileName)] = record;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(
            Guid serverId,
            string fileName,
            CancellationToken cancellationToken = default)
        {
            _records.Remove((serverId, fileName));
            return Task.CompletedTask;
        }
    }
}
