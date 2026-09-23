using System.IO.Compression;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using ServerManager.Contracts;
using ServerManager.Core;
using ServerManager.Core.Content;
using ServerManager.Infrastructure.Content;

namespace ServerManager.Infrastructure.Tests;

/// <summary>
/// Building a server from a modpack. The rules that matter: it only ever creates a new
/// server, every listed file is checked against the pack's own hash, and a failure leaves no
/// half-built folder behind.
/// </summary>
public sealed class ModpackServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"1salem-modpack-{Guid.NewGuid():N}");

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
    public async Task APackBuildsANewServerWithItsLoaderAndFiles()
    {
        var harness = new Harness(_root);
        var destination = Path.Combine(_root, "new-server");

        var result = await harness.Service.InstallAsync(
            harness.Profile,
            new ModpackInstallRequest(
                ContentProviderId.Modrinth,
                "test-pack",
                "test-pack-1.0.0",
                "My pack server",
                destination,
                25599,
                EulaAccepted: true));

        Assert.True(result.Success, result.Message);
        Assert.Equal("1.21.8", result.MinecraftVersion);
        Assert.Equal("fabric", result.Loader);

        // The loader's launcher, the pack's own file, and the server files the rest of the
        // app expects.
        Assert.True(File.Exists(Path.Combine(destination, "server.jar")));
        Assert.True(File.Exists(Path.Combine(destination, "mods", "thing.jar")));
        Assert.True(File.Exists(Path.Combine(destination, "eula.txt")));
        Assert.Contains("eula=true", await File.ReadAllTextAsync(Path.Combine(destination, "eula.txt")));
        Assert.Contains(
            "server-port=25599",
            await File.ReadAllTextAsync(Path.Combine(destination, "server.properties")));

        // An override from the pack is applied, and the new server is registered.
        Assert.True(File.Exists(Path.Combine(destination, "config", "from-overrides.txt")));
        var registered = Assert.Single(await harness.Servers.ListAsync());
        Assert.Equal("My pack server", registered.Name);
        Assert.Equal(destination, registered.RootPath);
    }

    [Fact]
    public async Task TheNewServersPackIsRecordedAgainstThatServer()
    {
        var harness = new Harness(_root);
        var destination = Path.Combine(_root, "recorded-server");

        var result = await harness.Service.InstallAsync(
            harness.Profile,
            new ModpackInstallRequest(
                ContentProviderId.Modrinth,
                "test-pack",
                "test-pack-1.0.0",
                "Recorded",
                destination,
                25599,
                EulaAccepted: true));

        Assert.True(result.Success, result.Message);
        var registered = Assert.Single(await harness.Servers.ListAsync());

        // One record, for the pack itself — not one per mod inside it — against the server
        // this install created, so a second Minecraft server keeps its own.
        var record = Assert.Single(harness.Store.All);
        Assert.Equal(registered.Id, record.ServerId);
        Assert.Equal(result.ServerId, record.ServerId);
        Assert.Equal(ContentKind.Modpack, record.Kind);
        Assert.Equal(ContentProviderId.Modrinth, record.Provider);
        Assert.Equal("test-pack", record.ProjectId);
        Assert.Equal("test-pack-1.0.0", record.VersionId);
        Assert.Equal("Test pack", record.ProjectName);
        Assert.Equal("1.0.0", record.InstalledVersion);
        Assert.Equal("1.21.8", record.MinecraftVersionAtInstall);
        Assert.Equal("fabric", record.Loader);
        Assert.Equal("0.16.9", record.LoaderVersion);
        Assert.True(record.ManagedByManager);
        Assert.NotNull(record.InstalledAtUtc);

        // The pack file it names is really there, and its hash is the file's own.
        var kept = Path.Combine(destination, record.RelativePath!);
        Assert.True(File.Exists(kept));
        Assert.Equal(
            (await FileDigests.ComputeAsync(kept, true)).Sha256,
            record.LocalSha256);
    }

    [Fact]
    public async Task APortAnotherServerAlreadyUsesIsRefusedBeforeAnythingIsBuilt()
    {
        var harness = new Harness(_root);
        await harness.Servers.UpsertAsync(
            new GameServerDefinition(
                Guid.NewGuid(),
                GameType.Minecraft,
                "Salem's survival",
                Path.Combine(_root, "existing-server"),
                25599,
                "1.21.8",
                DateTimeOffset.UtcNow),
            ServerState.Stopped);
        var destination = Path.Combine(_root, "clashing-server");

        var result = await harness.Service.InstallAsync(
            harness.Profile,
            new ModpackInstallRequest(
                ContentProviderId.Modrinth,
                "test-pack",
                "test-pack-1.0.0",
                "Clashing",
                destination,
                25599,
                EulaAccepted: true));

        Assert.False(result.Success);
        Assert.Equal("PortInUse", result.ErrorCode);
        Assert.Contains("Salem's survival", result.Message);

        // Nothing was downloaded, built or registered for the refused port.
        Assert.False(Directory.Exists(destination));
        Assert.Single(await harness.Servers.ListAsync());
        Assert.Empty(harness.Store.All);
    }

    [Fact]
    public async Task AFileThatFailsItsChecksumLeavesNoHalfBuiltServer()
    {
        var harness = new Harness(_root, corruptPackFile: true);
        var destination = Path.Combine(_root, "failed-server");

        var result = await harness.Service.InstallAsync(
            harness.Profile,
            new ModpackInstallRequest(
                ContentProviderId.Modrinth,
                "test-pack",
                "test-pack-1.0.0",
                "Doomed",
                destination,
                25599,
                EulaAccepted: true));

        Assert.False(result.Success);
        Assert.Equal("FileUnavailable", result.ErrorCode);

        // The folder this call created is gone, and nothing was registered.
        Assert.False(Directory.Exists(destination));
        Assert.Empty(await harness.Servers.ListAsync());
    }

    [Fact]
    public async Task APackForALoaderWeCannotSetUpIsRefusedBeforeAnythingIsCreated()
    {
        var harness = new Harness(_root, loader: "neoforge");
        var destination = Path.Combine(_root, "neoforge-server");

        var plan = await harness.Service.PlanAsync(
            harness.Profile,
            ContentProviderId.Modrinth,
            "test-pack");
        Assert.True(plan.Blocked);
        Assert.Equal("LoaderNotSupported:neoforge", plan.BlockedReason);

        var result = await harness.Service.InstallAsync(
            harness.Profile,
            new ModpackInstallRequest(
                ContentProviderId.Modrinth,
                "test-pack",
                "test-pack-1.0.0",
                "Nope",
                destination,
                25599,
                EulaAccepted: true));

        Assert.False(result.Success);
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public async Task AnExistingFolderWithFilesIsNeverOverwritten()
    {
        var harness = new Harness(_root);
        var destination = Path.Combine(_root, "existing");
        Directory.CreateDirectory(destination);
        var existing = Path.Combine(destination, "world.dat");
        await File.WriteAllTextAsync(existing, "someone's server");

        var result = await harness.Service.InstallAsync(
            harness.Profile,
            new ModpackInstallRequest(
                ContentProviderId.Modrinth,
                "test-pack",
                "test-pack-1.0.0",
                "Nope",
                destination,
                25599,
                EulaAccepted: true));

        Assert.False(result.Success);
        Assert.Equal("DestinationNotEmpty", result.ErrorCode);
        Assert.Equal("someone's server", await File.ReadAllTextAsync(existing));
    }

    [Fact]
    public async Task TheLicenceHasToBeAcceptedFirst()
    {
        var harness = new Harness(_root);

        var result = await harness.Service.InstallAsync(
            harness.Profile,
            new ModpackInstallRequest(
                ContentProviderId.Modrinth,
                "test-pack",
                "test-pack-1.0.0",
                "No eula",
                Path.Combine(_root, "no-eula"),
                25599));

        Assert.False(result.Success);
        Assert.Equal("EulaRequired", result.ErrorCode);
    }

    [Fact]
    public async Task ThePlanSaysWhatWouldBeBuiltBeforeAnythingIsDownloaded()
    {
        var harness = new Harness(_root);

        var plan = await harness.Service.PlanAsync(
            harness.Profile,
            ContentProviderId.Modrinth,
            "test-pack");

        Assert.False(plan.Blocked);
        Assert.Equal("1.21.8", plan.MinecraftVersion);
        Assert.Equal("fabric", plan.Loader);
        Assert.Equal(1, plan.ServerFileCount);
        Assert.True(plan.HasServerOverrides);
    }

    // ---- helpers ---------------------------------------------------------------------------

    private sealed class Harness
    {
        public Harness(string root, bool corruptPackFile = false, string loader = "fabric-loader")
        {
            var sourceRoot = Path.Combine(root, "source");
            Directory.CreateDirectory(sourceRoot);

            // The file the pack lists, and the hash the index will claim for it.
            var modBytes = Encoding.UTF8.GetBytes("pretend mod jar");
            var modHash = Convert.ToHexString(System.Security.Cryptography.SHA512.HashData(modBytes))
                .ToLowerInvariant();
            var servedBytes = corruptPackFile
                ? Encoding.UTF8.GetBytes("something else entirely")
                : modBytes;

            var packPath = WritePack(Path.Combine(sourceRoot, "test-pack-1.0.0.mrpack"), modHash, loader);
            Provider = new FakeModpackProvider(packPath);
            Servers = new InMemoryServerStore();
            Store = new ModpackContentStore();
            Service = new ModpackService(
                new ContentCatalogService([Provider], NullLogger<ContentCatalogService>.Instance),
                Servers,
                Store,
                new FakeJavaLocator(),
                new NoAudit(),
                new HttpClient(new PackHostHandler(servedBytes)),
                NullLogger<ModpackService>.Instance);
            Profile = new ServerContentProfile(
                Guid.NewGuid(),
                GameType.Minecraft,
                ServerPlatform.Vanilla,
                "1.21.8",
                null,
                Path.Combine(root, "browsing-from"),
                Path.Combine(root, "browsing-from", "plugins"),
                SupportsPlugins: false,
                IsRunning: false);
        }

        public FakeModpackProvider Provider { get; }

        public InMemoryServerStore Servers { get; }

        public ModpackContentStore Store { get; } = null!;

        public ModpackService Service { get; }

        public ServerContentProfile Profile { get; }

        private static string WritePack(string path, string modSha512, string loader)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
            using (var index = new StreamWriter(archive.CreateEntry("modrinth.index.json").Open()))
            {
                index.Write($$"""
                    {
                      "formatVersion": 1,
                      "game": "minecraft",
                      "versionId": "1.0.0",
                      "name": "Test pack",
                      "files": [{
                        "path": "mods/thing.jar",
                        "hashes": { "sha1": "unused", "sha512": "{{modSha512}}" },
                        "env": { "client": "required", "server": "required" },
                        "downloads": ["https://cdn.modrinth.com/data/x/versions/y/thing.jar"],
                        "fileSize": 15
                      }],
                      "dependencies": { "minecraft": "1.21.8", "{{loader}}": "0.16.9" }
                    }
                    """);
            }

            using var overrides = new StreamWriter(
                archive.CreateEntry("server-overrides/config/from-overrides.txt").Open());
            overrides.Write("pack config");
            return path;
        }
    }

    private sealed class FakeModpackProvider(string packPath) : IContentProvider
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
            Task.FromResult<ContentProject?>(new ContentProject(
                Id, projectId, projectId, "Test pack", null, null, null, null, null, [], [],
                Kind: ContentKind.Modpack));

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
                    $"{projectId}-1.0.0",
                    "1.0.0",
                    ContentReleaseChannel.Release,
                    DateTimeOffset.UtcNow,
                    ["fabric"],
                    ["1.21.8"],
                    new ContentFile(
                        "test-pack.mrpack",
                        new Uri("https://cdn.modrinth.com/data/x/versions/y/test-pack.mrpack"),
                        new FileInfo(packPath).Length),
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
            CancellationToken cancellationToken = default)
        {
            File.Copy(packPath, stagingFilePath, true);
            return Task.CompletedTask;
        }
    }

    /// <summary>Stands in for Fabric's metadata site and the pack's file hosts.</summary>
    private sealed class PackHostHandler(byte[] packFileBytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("/versions/loader", StringComparison.Ordinal) &&
                url.EndsWith("/server/jar", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Encoding.UTF8.GetBytes("fabric server launcher"))
                });
            }

            if (url.EndsWith("/versions/loader", StringComparison.Ordinal))
            {
                return Json("""[{"version":"0.16.9","stable":true}]""");
            }

            if (url.EndsWith("/versions/installer", StringComparison.Ordinal))
            {
                return Json("""[{"version":"1.1.2","stable":true}]""");
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(packFileBytes)
            });
        }

        private static Task<HttpResponseMessage> Json(string body) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
    }

    private sealed class FakeJavaLocator : IJavaRuntimeLocator
    {
        public Task<JavaRuntimeInfo?> FindAsync(
            int minimumMajorVersion,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<JavaRuntimeInfo?>(new JavaRuntimeInfo(@"C:\java\bin\java.exe", 21, "21.0.1"));
    }

    private sealed class InMemoryServerStore : IGameServerStore
    {
        private readonly List<GameServerDefinition> _servers = [];

        public Task<IReadOnlyList<GameServerDefinition>> ListAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GameServerDefinition>>(_servers.ToArray());

        public Task<GameServerDefinition?> GetAsync(
            Guid serverId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_servers.FirstOrDefault(server => server.Id == serverId));

        public Task UpsertAsync(
            GameServerDefinition server,
            ServerState state,
            CancellationToken cancellationToken = default)
        {
            _servers.RemoveAll(existing => existing.Id == server.Id);
            _servers.Add(server);
            return Task.CompletedTask;
        }

        public Task SetStateAsync(
            Guid serverId,
            ServerState state,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SetStateWithErrorAsync(
            Guid serverId,
            ServerState state,
            string? lastError,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class ModpackContentStore : IInstalledContentStore
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

        public IReadOnlyList<InstalledContent> All => _records.Values.ToArray();
    }

    private sealed class NoAudit : IAuditLogStore
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
