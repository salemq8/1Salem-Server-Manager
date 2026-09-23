using System.IO.Compression;
using Microsoft.Extensions.Logging.Abstractions;
using ServerManager.Contracts;
using ServerManager.Core;
using ServerManager.Core.Content;
using ServerManager.Infrastructure.Concurrency;
using ServerManager.Infrastructure.Content;

namespace ServerManager.Infrastructure.Tests;

/// <summary>
/// Data packs and resource packs end to end against a real folder, with the provider faked.
/// The things that matter: a pack lands in the world the server actually runs, a resource
/// pack is not sent to anybody until asked, and nothing else in the world is touched.
/// </summary>
public sealed class ContentPacksTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"1salem-packs-{Guid.NewGuid():N}");

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

    // ---- finding the world -------------------------------------------------------------

    [Fact]
    public void TheWorldComesFromTheServersOwnSettings()
    {
        var server = NewServer(levelName: "survival");

        var world = ServerWorldLocator.Locate(server);

        Assert.True(world.Found);
        Assert.Equal("survival", world.LevelName);
        Assert.EndsWith(
            Path.Combine("survival", "datapacks"),
            world.DataPackDirectory!,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AServerWithNoReadableWorldSaysSoInsteadOfGuessing()
    {
        var noProperties = Path.Combine(_root, "empty");
        Directory.CreateDirectory(noProperties);
        Assert.Equal("NoServerProperties", ServerWorldLocator.Locate(noProperties).Reason);

        var noLevel = Path.Combine(_root, "nolevel");
        Directory.CreateDirectory(noLevel);
        File.WriteAllText(Path.Combine(noLevel, "server.properties"), "server-port=25565\n");

        // Minecraft would default to "world", but assuming that could write into the wrong one.
        Assert.Equal("NoLevelName", ServerWorldLocator.Locate(noLevel).Reason);
    }

    [Fact]
    public void AWorldNameThatIsAPathIsRefused()
    {
        var server = NewServer(levelName: "../escape");

        var world = ServerWorldLocator.Locate(server);

        Assert.False(world.Found);
        Assert.Equal("UnsafeLevelName", world.Reason);
    }

    // ---- data packs ----------------------------------------------------------------------

    [Fact]
    public async Task ADataPackLandsInTheWorldAndNotInPlugins()
    {
        var harness = new Harness(_root, this);

        var result = await harness.Packs.InstallAsync(
            harness.Profile,
            new ContentInstallRequest(
                harness.Profile.ServerId,
                ContentProviderId.Modrinth,
                "vanilla-tweaks",
                Kind: ContentKind.DataPack));

        Assert.True(result.Success);
        var installed = Assert.Single(result.Installed!);
        var expected = Path.Combine(harness.ServerRoot, "survival", "datapacks", installed.FileName);
        Assert.True(File.Exists(expected));
        Assert.False(Directory.Exists(Path.Combine(harness.ServerRoot, "plugins")) &&
                     File.Exists(Path.Combine(harness.ServerRoot, "plugins", installed.FileName)));
        Assert.Equal(ContentKind.DataPack, installed.Kind);
    }

    [Fact]
    public async Task ARunningServerIsToldToReloadRatherThanRestart()
    {
        var harness = new Harness(_root, this, running: true);

        var result = await harness.Packs.InstallAsync(
            harness.Profile,
            new ContentInstallRequest(
                harness.Profile.ServerId,
                ContentProviderId.Modrinth,
                "vanilla-tweaks",
                Kind: ContentKind.DataPack));

        Assert.True(result.Success);
        Assert.Equal(InstalledContentState.ReloadRequired, result.Installed![0].State);
    }

    [Fact]
    public async Task AnArchiveThatIsNotAPackIsRefused()
    {
        var harness = new Harness(_root, this);
        harness.Provider.PublishBroken("vanilla-tweaks", omitMetadata: true, traversal: false);

        var result = await harness.Packs.InstallAsync(
            harness.Profile,
            new ContentInstallRequest(
                harness.Profile.ServerId,
                ContentProviderId.Modrinth,
                "vanilla-tweaks",
                Kind: ContentKind.DataPack));

        Assert.False(result.Success);
        Assert.Equal("InvalidArchive", result.ErrorCode);
        Assert.Empty(Directory.GetFiles(
            Path.Combine(harness.ServerRoot, "survival", "datapacks"),
            "*.zip"));
    }

    [Fact]
    public async Task AnArchiveThatWritesOutsideItselfIsRefused()
    {
        var harness = new Harness(_root, this);
        harness.Provider.PublishBroken("vanilla-tweaks", omitMetadata: false, traversal: true);

        var result = await harness.Packs.InstallAsync(
            harness.Profile,
            new ContentInstallRequest(
                harness.Profile.ServerId,
                ContentProviderId.Modrinth,
                "vanilla-tweaks",
                Kind: ContentKind.DataPack));

        Assert.False(result.Success);
        Assert.Equal("InvalidArchive", result.ErrorCode);
    }

    [Fact]
    public async Task AWrongChecksumStopsThePackAndLeavesNothingStaged()
    {
        var harness = new Harness(_root, this);
        harness.Provider.CorruptDownload = true;

        var result = await harness.Packs.InstallAsync(
            harness.Profile,
            new ContentInstallRequest(
                harness.Profile.ServerId,
                ContentProviderId.Modrinth,
                "vanilla-tweaks",
                Kind: ContentKind.DataPack));

        Assert.False(result.Success);
        Assert.Equal("HashMismatch", result.ErrorCode);
        Assert.Empty(await harness.Store.ListAsync(harness.Profile.ServerId));

        // Staging never outlives a failed attempt.
        var staging = ContentPathPolicy.ResolveStagingDirectory(harness.ServerRoot);
        Assert.True(!Directory.Exists(staging) || Directory.GetFiles(staging).Length == 0);
    }

    [Fact]
    public async Task ADataPackCanBeUpdatedAndPutBack()
    {
        var harness = new Harness(_root, this);
        await harness.Packs.InstallAsync(
            harness.Profile,
            new ContentInstallRequest(
                harness.Profile.ServerId,
                ContentProviderId.Modrinth,
                "vanilla-tweaks",
                Kind: ContentKind.DataPack));
        harness.Provider.PublishNewVersion("vanilla-tweaks", "2.0.0", "VanillaTweaks-2.zip");

        var installedFile = (await harness.Store.ListAsync(harness.Profile.ServerId))[0].FileName;
        var update = await harness.Packs.UpdateAsync(harness.Profile, installedFile);
        Assert.True(update.Success);
        var dataPacks = Path.Combine(harness.ServerRoot, "survival", "datapacks");
        Assert.True(File.Exists(Path.Combine(dataPacks, "VanillaTweaks-2.zip")));
        Assert.False(File.Exists(Path.Combine(dataPacks, installedFile)));

        var rollback = await harness.Packs.RollbackAsync(harness.Profile, "VanillaTweaks-2.zip");
        Assert.True(rollback.Success);
        Assert.False(File.Exists(Path.Combine(dataPacks, "VanillaTweaks-2.zip")));
        Assert.Single(Directory.GetFiles(dataPacks, "*.zip"));
    }

    [Fact]
    public async Task UninstallingAPackLeavesTheRestOfTheWorldAlone()
    {
        var harness = new Harness(_root, this);
        await harness.Packs.InstallAsync(
            harness.Profile,
            new ContentInstallRequest(
                harness.Profile.ServerId,
                ContentProviderId.Modrinth,
                "vanilla-tweaks",
                Kind: ContentKind.DataPack));
        var installed = (await harness.Store.ListAsync(harness.Profile.ServerId))[0];

        // Somebody else's pack, and the world's own data.
        var dataPacks = Path.Combine(harness.ServerRoot, "survival", "datapacks");
        var foreign = Path.Combine(dataPacks, "SomeoneElses.zip");
        WritePack(foreign);
        var levelData = Path.Combine(harness.ServerRoot, "survival", "level.dat");
        await File.WriteAllTextAsync(levelData, "world data");

        var result = await harness.Packs.UninstallAsync(harness.Profile, installed.FileName);

        Assert.True(result.Success);
        Assert.False(File.Exists(Path.Combine(dataPacks, installed.FileName)));
        Assert.True(File.Exists(foreign));
        Assert.True(File.Exists(levelData));
        Assert.Equal("world data", await File.ReadAllTextAsync(levelData));
    }

    // ---- resource packs ---------------------------------------------------------------------

    [Fact]
    public async Task AResourcePackIsKeptButNotSentToAnyoneUntilAsked()
    {
        var harness = new Harness(_root, this);

        var result = await harness.Packs.InstallAsync(
            harness.Profile,
            new ContentInstallRequest(
                harness.Profile.ServerId,
                ContentProviderId.Modrinth,
                "faithful",
                Kind: ContentKind.ResourcePack));

        Assert.True(result.Success);
        var record = Assert.Single(result.Installed!);
        Assert.Equal(InstalledContentState.NotDistributed, record.State);

        // Nothing has been written into the server's settings.
        var (url, sha1, _) = ServerWorldLocator.ReadResourcePackSettings(harness.ServerRoot);
        Assert.Null(url);
        Assert.Null(sha1);
    }

    [Fact]
    public async Task SendingAResourcePackPointsClientsAtTheProvidersOwnAddress()
    {
        var harness = new Harness(_root, this);
        var install = await harness.Packs.InstallAsync(
            harness.Profile,
            new ContentInstallRequest(
                harness.Profile.ServerId,
                ContentProviderId.Modrinth,
                "faithful",
                Kind: ContentKind.ResourcePack));
        var record = install.Installed![0];

        var result = await harness.Packs.DistributeAsync(
            harness.Profile,
            new ResourcePackDistributionRequest(record.FileName, Require: true));

        Assert.True(result.Success);
        var (url, sha1, require) = ServerWorldLocator.ReadResourcePackSettings(harness.ServerRoot);
        Assert.StartsWith("https://cdn.modrinth.com/", url!, StringComparison.Ordinal);
        Assert.Equal(record.ProviderSha1, sha1);
        Assert.True(require);

        // Other settings in the file are untouched.
        var properties = await File.ReadAllTextAsync(
            Path.Combine(harness.ServerRoot, "server.properties"));
        Assert.Contains("level-name=survival", properties, StringComparison.Ordinal);
        Assert.Contains("motd=", properties, StringComparison.Ordinal);

        var withdraw = await harness.Packs.StopDistributingAsync(harness.Profile);
        Assert.True(withdraw.Success);
        Assert.Null(ServerWorldLocator.ReadResourcePackSettings(harness.ServerRoot).Url);
    }

    [Fact]
    public async Task APackWithNoProviderAddressCannotBeSentToPlayers()
    {
        var harness = new Harness(_root, this);
        var install = await harness.Packs.InstallAsync(
            harness.Profile,
            new ContentInstallRequest(
                harness.Profile.ServerId,
                ContentProviderId.Modrinth,
                "faithful",
                Kind: ContentKind.ResourcePack));
        var record = install.Installed![0];

        // As if it had been added by hand: a local file with nowhere for clients to get it.
        await harness.Store.UpsertAsync(record with { DownloadUrl = null });

        var result = await harness.Packs.DistributeAsync(
            harness.Profile,
            new ResourcePackDistributionRequest(record.FileName));

        Assert.False(result.Success);
        Assert.Equal("NeedsReachableUrl", result.ErrorCode);
        Assert.Null(ServerWorldLocator.ReadResourcePackSettings(harness.ServerRoot).Url);
    }

    [Fact]
    public async Task RemovingASentPackAlsoStopsSendingIt()
    {
        var harness = new Harness(_root, this);
        var install = await harness.Packs.InstallAsync(
            harness.Profile,
            new ContentInstallRequest(
                harness.Profile.ServerId,
                ContentProviderId.Modrinth,
                "faithful",
                Kind: ContentKind.ResourcePack));
        var record = install.Installed![0];
        await harness.Packs.DistributeAsync(
            harness.Profile,
            new ResourcePackDistributionRequest(record.FileName));

        await harness.Packs.UninstallAsync(harness.Profile, record.FileName);

        // Clients would otherwise be sent to a file that is no longer there.
        Assert.Null(ServerWorldLocator.ReadResourcePackSettings(harness.ServerRoot).Url);
    }

    // ---- everything together ------------------------------------------------------------------

    [Fact]
    public async Task InstalledShowsEachTypeSeparatelyWithItsOwnState()
    {
        var harness = new Harness(_root, this, running: true);
        await harness.Packs.InstallAsync(
            harness.Profile,
            new ContentInstallRequest(
                harness.Profile.ServerId,
                ContentProviderId.Modrinth,
                "vanilla-tweaks",
                Kind: ContentKind.DataPack));
        await harness.Packs.InstallAsync(
            harness.Profile,
            new ContentInstallRequest(
                harness.Profile.ServerId,
                ContentProviderId.Modrinth,
                "faithful",
                Kind: ContentKind.ResourcePack));

        var listed = await harness.Installed.ListAsync(harness.Profile);

        var dataPack = Assert.Single(listed, item => item.Kind == ContentKind.DataPack);
        var resourcePack = Assert.Single(listed, item => item.Kind == ContentKind.ResourcePack);
        Assert.Equal(InstalledContentState.ReloadRequired, dataPack.State);
        Assert.Equal(InstalledContentState.NotDistributed, resourcePack.State);

        // A pack somebody dropped in by hand is reported as theirs, not ours.
        WritePack(Path.Combine(harness.ServerRoot, "survival", "datapacks", "TheirPack.zip"));
        var afterManual = await harness.Installed.ListAsync(harness.Profile);
        var manual = Assert.Single(afterManual, item => item.FileName == "TheirPack.zip");
        Assert.Equal(InstalledContentState.InstalledManually, manual.State);
        Assert.Equal(ContentKind.DataPack, manual.Kind);
        Assert.False(manual.ManagedByManager);
    }

    [Fact]
    public async Task ContentInstalledForAnotherMinecraftVersionIsFlagged()
    {
        var harness = new Harness(_root, this);
        var install = await harness.Packs.InstallAsync(
            harness.Profile,
            new ContentInstallRequest(
                harness.Profile.ServerId,
                ContentProviderId.Modrinth,
                "vanilla-tweaks",
                Kind: ContentKind.DataPack));

        // The server later moves to a different Minecraft version.
        var moved = harness.Profile with { MinecraftVersion = "1.21.11" };
        var listed = await harness.Installed.ListAsync(moved);

        Assert.Equal(
            InstalledContentState.IncompatibleWithServer,
            Assert.Single(listed, item => item.FileName == install.Installed![0].FileName).State);
    }

    // ---- helpers ---------------------------------------------------------------------------

    private string NewServer(string levelName)
    {
        var root = Path.Combine(_root, $"server-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.WriteAllText(
            Path.Combine(root, "server.properties"),
            $"#Minecraft server properties\nserver-port=25565\nlevel-name={levelName}\nmotd=Test\n");
        return root;
    }

    private static string WritePack(string path, bool withMetadata = true, bool traversal = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        if (withMetadata)
        {
            using var writer = new StreamWriter(archive.CreateEntry("pack.mcmeta").Open());
            writer.Write("""{"pack":{"pack_format":48,"description":"Test pack"}}""");
        }

        if (traversal)
        {
            archive.CreateEntry("../escaped.json");
        }

        using var data = new StreamWriter(archive.CreateEntry("data/test/thing.json").Open());
        data.Write("{}");
        return path;
    }

    private sealed class Harness
    {
        public Harness(string root, ContentPacksTests owner, bool running = false)
        {
            ServerRoot = owner.NewServer("survival");
            Profile = new ServerContentProfile(
                Guid.NewGuid(),
                GameType.Minecraft,
                ServerPlatform.Paper,
                "1.21.8",
                null,
                ServerRoot,
                Path.Combine(ServerRoot, "plugins"),
                SupportsPlugins: true,
                IsRunning: running);
            Provider = new FakePackProvider(Path.Combine(root, $"source-{Guid.NewGuid():N}"));
            Store = new InMemoryPackStore();
            var catalog = new ContentCatalogService([Provider], NullLogger<ContentCatalogService>.Instance);
            Packs = new PackInstallService(
                catalog,
                Store,
                new ServerOperationCoordinator(),
                new NoAuditLog(),
                NullLogger<PackInstallService>.Instance,
                TimeSpan.FromMilliseconds(200));
            Installed = new InstalledContentService(
                catalog,
                Store,
                NullLogger<InstalledContentService>.Instance);
        }

        public string ServerRoot { get; }

        public ServerContentProfile Profile { get; }

        public FakePackProvider Provider { get; }

        public InMemoryPackStore Store { get; }

        public PackInstallService Packs { get; }

        public InstalledContentService Installed { get; }
    }

    private sealed class FakePackProvider : IContentProvider
    {
        private readonly string _sourceRoot;
        private readonly Dictionary<string, List<ContentVersion>> _versions = new(StringComparer.Ordinal);

        public FakePackProvider(string sourceRoot)
        {
            _sourceRoot = sourceRoot;
            Directory.CreateDirectory(sourceRoot);
            Publish("vanilla-tweaks", "1.0.0", "VanillaTweaks.zip", ContentKind.DataPack);
            Publish("faithful", "1.0.0", "Faithful.zip", ContentKind.ResourcePack);
        }

        public ContentProviderId Id => ContentProviderId.Modrinth;


        public bool CorruptDownload { get; set; }

        public bool CanServe(ServerContentProfile profile) => true;

        public bool CanServe(ServerContentProfile profile, ContentKind kind) => true;

        public void PublishNewVersion(string projectId, string versionNumber, string fileName) =>
            Publish(projectId, versionNumber, fileName, ContentKind.DataPack);

        /// <summary>
        /// Republishes a project as a deliberately broken pack, with hashes that match the
        /// broken file, so a test reaches archive validation rather than stopping at the hash.
        /// </summary>
        public void PublishBroken(string projectId, bool omitMetadata, bool traversal)
        {
            var existing = _versions[projectId][^1];
            var path = Path.Combine(_sourceRoot, $"{projectId}-{existing.VersionNumber}.zip");
            File.Delete(path);
            WritePack(path, !omitMetadata, traversal);
            var digests = FileDigests.ComputeAsync(path, true).GetAwaiter().GetResult();
            _versions[projectId][^1] = existing with
            {
                File = existing.File! with
                {
                    SizeBytes = new FileInfo(path).Length,
                    Sha512 = digests.Sha512,
                    Sha1 = digests.Sha1
                }
            };
        }

        private void Publish(string projectId, string versionNumber, string fileName, ContentKind kind)
        {
            var path = WritePack(Path.Combine(_sourceRoot, $"{projectId}-{versionNumber}.zip"));
            var digests = FileDigests.ComputeAsync(path, true).GetAwaiter().GetResult();
            var version = new ContentVersion(
                Id,
                projectId,
                $"{projectId}-{versionNumber}",
                versionNumber,
                ContentReleaseChannel.Release,
                DateTimeOffset.UtcNow.AddMinutes(_versions.Count),
                [kind == ContentKind.DataPack ? "datapack" : "minecraft"],
                ["1.21.8"],
                new ContentFile(
                    fileName,
                    new Uri($"https://cdn.modrinth.com/data/{projectId}/{fileName}"),
                    new FileInfo(path).Length,
                    digests.Sha512,
                    null,
                    digests.Sha1),
                []);
            if (!_versions.TryGetValue(projectId, out var versions))
            {
                versions = [];
                _versions[projectId] = versions;
            }

            versions.Add(version);
        }

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
            Task.FromResult<ContentProject?>(new ContentProject(
                Id,
                projectId,
                projectId,
                projectId,
                null,
                null,
                null,
                null,
                null,
                [],
                ["1.21.8"],
                Kind: kind));

        public Task<IReadOnlyList<ContentVersion>> GetVersionsAsync(
            string projectId,
            ServerContentProfile profile,
            ContentKind kind = ContentKind.Plugin,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ContentVersion>>(_versions[projectId].ToArray());

        public async Task<ContentVersion?> ResolveCompatibleVersionAsync(
            string projectId,
            ServerContentProfile profile,
            bool allowPrerelease = false,
            ContentKind kind = ContentKind.Plugin,
            CancellationToken cancellationToken = default) =>
            PluginCompatibilityPolicy.SelectBest(
                await GetVersionsAsync(projectId, profile, kind, cancellationToken),
                profile,
                allowPrerelease,
                kind);

        public Task<ContentIdentification?> IdentifyAsync(
            ContentFileDigests digests,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ContentIdentification?>(null);

        public Task DownloadAsync(
            ContentVersion version,
            string stagingFilePath,
            IProgress<ContentInstallProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            File.Copy(
                Path.Combine(_sourceRoot, $"{version.ProjectId}-{version.VersionNumber}.zip"),
                stagingFilePath,
                true);
            if (CorruptDownload)
            {
                // Same name, different bytes: what hash verification exists for.
                using var archive = ZipFile.Open(stagingFilePath, ZipArchiveMode.Update);
                archive.CreateEntry($"tampered-{Guid.NewGuid():N}.json");
            }

            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryPackStore : IInstalledContentStore
    {
        private readonly Dictionary<(Guid, string), InstalledContent> _records = [];

        public Task<IReadOnlyList<InstalledContent>> ListAsync(
            Guid serverId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<InstalledContent>>(
                _records.Where(pair => pair.Key.Item1 == serverId)
                    .Select(pair => pair.Value)
                    .ToArray());

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

    private sealed class NoAuditLog : IAuditLogStore
    {
        public Task WriteAsync(
            string actor,
            string action,
            string target,
            bool succeeded,
            string? detail = null,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
