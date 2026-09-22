using System.IO.Compression;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using ServerManager.Contracts;
using ServerManager.Core;
using ServerManager.Core.Content;
using ServerManager.Infrastructure.Concurrency;
using ServerManager.Infrastructure.Content;

namespace ServerManager.Infrastructure.Tests;

public sealed class ContentHubTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"1salem-content-{Guid.NewGuid():N}");

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

    // ---- provider normalization ---------------------------------------------------------

    [Fact]
    public async Task Modrinth_NormalizesSearchHitsIntoTheSharedModel()
    {
        const string search = """
            {
              "hits": [{
                "project_id": "Vebnzrzj",
                "slug": "luckperms",
                "title": "LuckPerms",
                "description": "Permissions",
                "author": "lucko",
                "categories": ["paper", "spigot", "bukkit", "fabric", "utility"],
                "versions": ["1.21.8", "1.21.7"],
                "downloads": 12345,
                "icon_url": "https://cdn.modrinth.com/icon.png",
                "license": "MIT"
              }],
              "offset": 0, "limit": 20, "total_hits": 1
            }
            """;
        var provider = new ModrinthContentProvider(Client(search));

        var result = await provider.SearchAsync(new ContentSearchRequest("luck"), Profile());

        var project = Assert.Single(result.Projects);
        Assert.Equal(ContentProviderId.Modrinth, project.Provider);
        Assert.Equal("LuckPerms", project.Name);
        Assert.Equal("lucko", project.Author);
        Assert.Equal(12345, project.Downloads);
        Assert.Equal("MIT", project.License);
        Assert.Equal("https://modrinth.com/plugin/luckperms", project.ProjectUrl?.ToString());

        // Only the loaders this Paper server can actually run are surfaced.
        Assert.Contains("paper", project.Platforms);
        Assert.DoesNotContain("fabric", project.Platforms);
        Assert.True(project.IsCompatible);
    }

    [Fact]
    public async Task Modrinth_NormalizesVersionHashesAndDependencies()
    {
        const string versions = """
            [{
              "id": "b0mk8uS6",
              "project_id": "Vebnzrzj",
              "version_number": "5.5.71",
              "version_type": "release",
              "date_published": "2026-09-01T00:00:00Z",
              "loaders": ["paper", "bukkit"],
              "game_versions": ["1.21.8"],
              "files": [{
                "url": "https://cdn.modrinth.com/data/x/versions/y/LuckPerms.jar",
                "filename": "LuckPerms.jar",
                "size": 1501521,
                "primary": true,
                "hashes": { "sha1": "aa", "sha512": "bb" }
              }],
              "dependencies": [
                { "project_id": "dep1", "dependency_type": "required" },
                { "project_id": "dep2", "dependency_type": "optional" }
              ]
            }]
            """;
        var provider = new ModrinthContentProvider(Client(versions));

        var result = await provider.GetVersionsAsync("Vebnzrzj", Profile());

        var version = Assert.Single(result);
        Assert.Equal(ContentReleaseChannel.Release, version.Channel);
        Assert.Equal("bb", version.File?.Sha512);
        Assert.Equal("aa", version.File?.Sha1);
        Assert.Equal(1501521, version.File?.SizeBytes);
        Assert.Equal(ContentDependencyKind.Required, version.Dependencies[0].Kind);
        Assert.Equal(ContentDependencyKind.Optional, version.Dependencies[1].Kind);
    }

    [Fact]
    public async Task Hangar_NormalizesSha256AndPlatformVersions()
    {
        const string versions = """
            {
              "pagination": { "count": 1, "limit": 25, "offset": 0 },
              "result": [{
                "name": "5.1.0",
                "createdAt": "2026-05-17T08:06:45Z",
                "channel": { "name": "Release" },
                "platformDependencies": { "PAPER": ["1.21.8", "1.21.9"] },
                "downloads": {
                  "PAPER": {
                    "fileInfo": {
                      "name": "Maintenance-Paper-5.1.0.jar",
                      "sizeBytes": 233744,
                      "sha256Hash": "ab08939d"
                    },
                    "externalUrl": null,
                    "downloadUrl": "https://hangarcdn.papermc.io/plugins/kennytv/Maintenance/5.1.0/PAPER/M.jar"
                  }
                },
                "pluginDependencies": {}
              }]
            }
            """;
        var provider = new HangarContentProvider(Client(versions));

        var result = await provider.GetVersionsAsync("Maintenance", Profile());

        var version = Assert.Single(result);
        Assert.Equal("ab08939d", version.File?.Sha256);
        Assert.Contains("1.21.8", version.GameVersions);
        Assert.True(PluginCompatibilityPolicy.IsCompatible(version, Profile()));
    }

    [Fact]
    public async Task Hangar_ExternallyHostedReleaseIsNotInstallable()
    {
        const string versions = """
            {
              "result": [{
                "name": "9.9.9",
                "channel": { "name": "Release" },
                "platformDependencies": { "PAPER": ["1.21.8"] },
                "downloads": {
                  "PAPER": {
                    "fileInfo": null,
                    "downloadUrl": null,
                    "externalUrl": "https://github.com/example/releases/plugin.jar"
                  }
                }
              }]
            }
            """;
        var provider = new HangarContentProvider(Client(versions));

        var version = Assert.Single(await provider.GetVersionsAsync("Example", Profile()));

        // We do not download a file the provider neither hosts nor hashes.
        Assert.Null(version.File);
        Assert.NotNull(version.ExternalDownloadUrl);
        Assert.False(PluginCompatibilityPolicy.IsCompatible(version, Profile()));
        await Assert.ThrowsAsync<ContentProviderException>(
            () => provider.DownloadAsync(version, Path.Combine(_root, "x.jar")));
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, ContentProviderException.RateLimitedCode)]
    [InlineData(HttpStatusCode.Gone, ContentProviderException.ApiRetiredCode)]
    [InlineData(HttpStatusCode.NotFound, ContentProviderException.NotFoundCode)]
    [InlineData(HttpStatusCode.ServiceUnavailable, ContentProviderException.UnavailableCode)]
    [InlineData(HttpStatusCode.InternalServerError, ContentProviderException.UnavailableCode)]
    public async Task ProviderFailures_BecomeCodesTheUiCanPhrase(
        HttpStatusCode status,
        string expectedCode)
    {
        var provider = new ModrinthContentProvider(
            new HttpClient(new StatusHandler(status))
            {
                BaseAddress = new Uri(ModrinthContentProvider.BaseAddress)
            });

        var exception = await Assert.ThrowsAsync<ContentProviderException>(
            () => provider.SearchAsync(new ContentSearchRequest("x"), Profile()));

        Assert.Equal(expectedCode, exception.ErrorCode);
    }

    [Fact]
    public async Task NetworkFailure_IsReportedAsOffline()
    {
        var provider = new ModrinthContentProvider(
            new HttpClient(new ThrowingHandler())
            {
                BaseAddress = new Uri(ModrinthContentProvider.BaseAddress)
            });

        var exception = await Assert.ThrowsAsync<ContentProviderException>(
            () => provider.SearchAsync(new ContentSearchRequest("x"), Profile()));

        Assert.Equal(ContentProviderException.OfflineCode, exception.ErrorCode);
    }

    // ---- JAR validation ------------------------------------------------------------------

    [Fact]
    public void JarValidator_AcceptsAPluginAndRejectsImpostors()
    {
        Directory.CreateDirectory(_root);
        var good = WriteJar(Path.Combine(_root, "good.jar"));
        Assert.True(PluginJarValidator.Validate(good).IsValid);

        var html = Path.Combine(_root, "error.jar");
        File.WriteAllText(html, "<html><body>404 not found</body></html>");
        Assert.False(PluginJarValidator.Validate(html).IsValid);

        var empty = Path.Combine(_root, "empty.jar");
        File.WriteAllBytes(empty, []);
        Assert.False(PluginJarValidator.Validate(empty).IsValid);

        // A JAR with classes but no plugin descriptor is not a server plugin.
        var library = Path.Combine(_root, "library.jar");
        using (var archive = ZipFile.Open(library, ZipArchiveMode.Create))
        {
            archive.CreateEntry("com/example/Thing.class");
        }

        Assert.False(PluginJarValidator.Validate(library).IsValid);
    }

    [Fact]
    public void JarValidator_RejectsTraversalEntries()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "evil.jar");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            archive.CreateEntry("plugin.yml");
            archive.CreateEntry("com/example/Plugin.class");
            archive.CreateEntry("../../escape.class");
        }

        var validation = PluginJarValidator.Validate(path);

        Assert.False(validation.IsValid);
        Assert.Contains("unsafe", validation.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // ---- install pipeline ----------------------------------------------------------------

    [Fact]
    public async Task Install_PlacesTheJarAndRecordsWhatItInstalled()
    {
        var harness = new Harness(_root);
        var result = await harness.Installer.InstallAsync(
            harness.Profile,
            new ContentInstallRequest(harness.Profile.ServerId, ContentProviderId.Modrinth, "main"));

        Assert.True(result.Success);
        Assert.True(File.Exists(Path.Combine(harness.Profile.PluginsDirectory, "main.jar")));

        var recorded = Assert.Single(await harness.Store.ListAsync(harness.Profile.ServerId));
        Assert.Equal("main.jar", recorded.FileName);
        Assert.Equal("1.0.0", recorded.InstalledVersion);
        Assert.Equal(ContentProviderId.Modrinth, recorded.Provider);
        Assert.False(string.IsNullOrWhiteSpace(recorded.LocalSha256));
        Assert.Equal(harness.Profile.MinecraftVersion, recorded.MinecraftVersionAtInstall);
    }

    [Fact]
    public async Task Install_OnARunningServerAsksForARestartAndNeverReloads()
    {
        var harness = new Harness(_root, running: true);

        var result = await harness.Installer.InstallAsync(
            harness.Profile,
            new ContentInstallRequest(harness.Profile.ServerId, ContentProviderId.Modrinth, "main"));

        Assert.True(result.Success);
        Assert.True(result.RestartRequired);
        Assert.Equal(InstalledContentState.RestartRequired, result.Installed![0].State);
    }

    [Fact]
    public async Task Install_RefusesWhenTheProviderHashDoesNotMatch()
    {
        var harness = new Harness(_root);
        harness.Provider.CorruptDownload = true;

        var result = await harness.Installer.InstallAsync(
            harness.Profile,
            new ContentInstallRequest(harness.Profile.ServerId, ContentProviderId.Modrinth, "main"));

        Assert.False(result.Success);
        Assert.Equal("HashMismatch", result.ErrorCode);
        Assert.False(File.Exists(Path.Combine(harness.Profile.PluginsDirectory, "main.jar")));
        Assert.Empty(await harness.Store.ListAsync(harness.Profile.ServerId));
    }

    [Fact]
    public async Task Install_BringsRequiredDependenciesAndShowsThemFirst()
    {
        var harness = new Harness(_root);
        harness.Provider.AddDependency("main", "lib", ContentDependencyKind.Required);

        var plan = await harness.Installer.PlanAsync(
            harness.Profile,
            new ContentInstallRequest(harness.Profile.ServerId, ContentProviderId.Modrinth, "main"));
        Assert.False(plan.Blocked);
        Assert.Equal(2, plan.Items.Count);
        Assert.Contains(plan.Items, item => item is { ProjectId: "lib", IsDependency: true });

        var result = await harness.Installer.InstallAsync(
            harness.Profile,
            new ContentInstallRequest(harness.Profile.ServerId, ContentProviderId.Modrinth, "main"));

        Assert.True(result.Success);
        Assert.True(File.Exists(Path.Combine(harness.Profile.PluginsDirectory, "lib.jar")));
    }

    [Fact]
    public async Task Install_StopsEntirelyWhenARequiredDependencyDoesNotFit()
    {
        var harness = new Harness(_root);
        harness.Provider.AddDependency("main", "lib", ContentDependencyKind.Required);
        harness.Provider.MakeIncompatible("lib");

        var result = await harness.Installer.InstallAsync(
            harness.Profile,
            new ContentInstallRequest(harness.Profile.ServerId, ContentProviderId.Modrinth, "main"));

        Assert.False(result.Success);
        Assert.StartsWith("DependencyIncompatible", result.ErrorCode);

        // Nothing at all is installed: no half-done install presented as success.
        Assert.False(File.Exists(Path.Combine(harness.Profile.PluginsDirectory, "main.jar")));
        Assert.False(File.Exists(Path.Combine(harness.Profile.PluginsDirectory, "lib.jar")));
    }

    [Fact]
    public async Task Install_DoesNotInstallOptionalDependencies()
    {
        var harness = new Harness(_root);
        harness.Provider.AddDependency("main", "extra", ContentDependencyKind.Optional);

        var plan = await harness.Installer.PlanAsync(
            harness.Profile,
            new ContentInstallRequest(harness.Profile.ServerId, ContentProviderId.Modrinth, "main"));

        Assert.Single(plan.Items);
        Assert.Contains(plan.Warnings, warning => warning.StartsWith("Optional:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Update_KeepsTheOldJarAndRollbackPutsItBack()
    {
        var harness = new Harness(_root);
        await harness.Installer.InstallAsync(
            harness.Profile,
            new ContentInstallRequest(harness.Profile.ServerId, ContentProviderId.Modrinth, "main"));
        harness.Provider.PublishNewVersion("main", "2.0.0", "main-2.jar");

        var update = await harness.Installer.UpdateAsync(harness.Profile, "main.jar");

        Assert.True(update.Success);
        Assert.True(File.Exists(Path.Combine(harness.Profile.PluginsDirectory, "main-2.jar")));
        Assert.False(File.Exists(Path.Combine(harness.Profile.PluginsDirectory, "main.jar")));
        var updated = Assert.Single(await harness.Store.ListAsync(harness.Profile.ServerId));
        Assert.Equal("2.0.0", updated.InstalledVersion);
        Assert.NotNull(updated.PreviousFileName);

        var rollback = await harness.Installer.RollbackAsync(harness.Profile, "main-2.jar");

        Assert.True(rollback.Success);
        Assert.False(File.Exists(Path.Combine(harness.Profile.PluginsDirectory, "main-2.jar")));
        Assert.Single(Directory.GetFiles(harness.Profile.PluginsDirectory, "*.jar"));
    }

    [Fact]
    public async Task Update_IsRefusedWhenTheNewerReleaseDoesNotFitThisServer()
    {
        var harness = new Harness(_root);
        await harness.Installer.InstallAsync(
            harness.Profile,
            new ContentInstallRequest(harness.Profile.ServerId, ContentProviderId.Modrinth, "main"));

        // A newer release that dropped this server's Minecraft version is not an update.
        harness.Provider.PublishNewVersion("main", "3.0.0", "main-3.jar", gameVersion: "1.21.11");

        var update = await harness.Installer.UpdateAsync(harness.Profile, "main.jar");

        Assert.False(update.Success);
        Assert.Equal("AlreadyCurrent", update.ErrorCode);
        Assert.False(File.Exists(Path.Combine(harness.Profile.PluginsDirectory, "main-3.jar")));
    }

    [Fact]
    public async Task Uninstall_RemovesTheJarAndKeepsTheConfigurationFolder()
    {
        var harness = new Harness(_root);
        await harness.Installer.InstallAsync(
            harness.Profile,
            new ContentInstallRequest(harness.Profile.ServerId, ContentProviderId.Modrinth, "main"));

        // The plugin's own data folder, as a real plugin would create on first run.
        var dataFolder = Path.Combine(harness.Profile.PluginsDirectory, "main");
        Directory.CreateDirectory(dataFolder);
        var config = Path.Combine(dataFolder, "config.yml");
        await File.WriteAllTextAsync(config, "players: keep me");

        var result = await harness.Installer.UninstallAsync(harness.Profile, "main.jar");

        Assert.True(result.Success);
        Assert.False(File.Exists(Path.Combine(harness.Profile.PluginsDirectory, "main.jar")));
        Assert.True(File.Exists(config));
        Assert.Equal("players: keep me", await File.ReadAllTextAsync(config));
        Assert.Empty(await harness.Store.ListAsync(harness.Profile.ServerId));
    }

    [Fact]
    public async Task InstalledList_SeparatesManualFilesAndNoticesChanges()
    {
        var harness = new Harness(_root);
        await harness.Installer.InstallAsync(
            harness.Profile,
            new ContentInstallRequest(harness.Profile.ServerId, ContentProviderId.Modrinth, "main"));
        WriteJar(
            Path.Combine(harness.Profile.PluginsDirectory, "SomethingElse.jar"),
            "name: SomethingElse");

        var listed = await harness.Installed.ListAsync(harness.Profile);

        var manual = Assert.Single(listed, item => item.FileName == "SomethingElse.jar");
        Assert.Equal(InstalledContentState.InstalledManually, manual.State);
        Assert.Null(manual.ProjectId);
        Assert.False(manual.ManagedByManager);

        var managed = Assert.Single(listed, item => item.FileName == "main.jar");
        Assert.Equal(InstalledContentState.UpToDate, managed.State);

        // Replacing a managed file by hand is reported rather than hidden.
        await File.WriteAllBytesAsync(
            Path.Combine(harness.Profile.PluginsDirectory, "main.jar"),
            await File.ReadAllBytesAsync(
                Path.Combine(harness.Profile.PluginsDirectory, "SomethingElse.jar")));
        var afterEdit = await harness.Installed.ListAsync(harness.Profile);
        Assert.Equal(
            InstalledContentState.ModifiedLocally,
            Assert.Single(afterEdit, item => item.FileName == "main.jar").State);

        // A managed file that disappears is reported missing, not silently forgotten.
        File.Delete(Path.Combine(harness.Profile.PluginsDirectory, "main.jar"));
        var afterDelete = await harness.Installed.ListAsync(harness.Profile);
        Assert.Equal(
            InstalledContentState.MissingFile,
            Assert.Single(afterDelete, item => item.FileName == "main.jar").State);
    }

    [Fact]
    public async Task Install_IsRefusedWhileAnotherOperationHoldsTheServer()
    {
        var harness = new Harness(_root);
        await using var _ = await harness.Coordinator.AcquireAsync(
            harness.Profile.ServerId,
            "Backup",
            TimeSpan.FromMilliseconds(50));

        var result = await harness.Installer.InstallAsync(
            harness.Profile,
            new ContentInstallRequest(harness.Profile.ServerId, ContentProviderId.Modrinth, "main"));

        Assert.False(result.Success);
        Assert.Equal("ServerBusy", result.ErrorCode);
    }

    // ---- helpers -------------------------------------------------------------------------

    private static ServerContentProfile Profile(
        ServerPlatform platform = ServerPlatform.Paper,
        string version = "1.21.8") =>
        new(
            Guid.NewGuid(),
            GameType.Minecraft,
            platform,
            version,
            null,
            @"C:\servers\mc",
            @"C:\servers\mc\plugins",
            SupportsPlugins: true,
            IsRunning: false);

    private static HttpClient Client(string json) =>
        new(new StaticJsonHandler(json))
        {
            BaseAddress = new Uri(ModrinthContentProvider.BaseAddress)
        };

    private static string WriteJar(string path, string marker = "name: Test")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var descriptor = new StreamWriter(archive.CreateEntry("plugin.yml").Open()))
        {
            descriptor.Write(marker);
        }

        using var code = new StreamWriter(archive.CreateEntry("com/example/Plugin.class").Open());
        code.Write("not really bytecode, never executed");
        return path;
    }

    private sealed class StaticJsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
    }

    private sealed class StatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status));
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new HttpRequestException("no network");
    }

    /// <summary>A real server root and real files, with the provider faked.</summary>
    private sealed class Harness
    {
        public Harness(string root, bool running = false)
        {
            var serverRoot = Path.Combine(root, "server");
            Directory.CreateDirectory(Path.Combine(serverRoot, "plugins"));
            Profile = new ServerContentProfile(
                Guid.NewGuid(),
                GameType.Minecraft,
                ServerPlatform.Paper,
                "1.21.8",
                null,
                serverRoot,
                Path.Combine(serverRoot, "plugins"),
                SupportsPlugins: true,
                IsRunning: running);
            Provider = new FakeProvider(Path.Combine(root, "source"));
            Store = new InMemoryStore();
            Coordinator = new ServerOperationCoordinator();
            var catalog = new ContentCatalogService([Provider], NullLogger<ContentCatalogService>.Instance);
            Installer = new PluginInstallService(
                catalog,
                Store,
                Coordinator,
                new NullAuditLog(),
                NullLogger<PluginInstallService>.Instance,
                TimeSpan.FromMilliseconds(200));
            Installed = new InstalledContentService(
                catalog,
                Store,
                NullLogger<InstalledContentService>.Instance);
        }

        public ServerContentProfile Profile { get; }

        public FakeProvider Provider { get; }

        public InMemoryStore Store { get; }

        public ServerOperationCoordinator Coordinator { get; }

        public PluginInstallService Installer { get; }

        public InstalledContentService Installed { get; }
    }

    private sealed class FakeProvider : IContentProvider
    {
        private readonly string _sourceRoot;
        private readonly Dictionary<string, List<ContentVersion>> _versions = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<ContentDependency>> _dependencies = new(StringComparer.Ordinal);
        private readonly HashSet<string> _incompatible = new(StringComparer.Ordinal);

        public FakeProvider(string sourceRoot)
        {
            _sourceRoot = sourceRoot;
            Directory.CreateDirectory(sourceRoot);
            Publish("main", "1.0.0", "main.jar");
            Publish("lib", "1.0.0", "lib.jar");
            Publish("extra", "1.0.0", "extra.jar");
        }

        public ContentProviderId Id => ContentProviderId.Modrinth;

        public bool CorruptDownload { get; set; }

        public bool CanServe(ServerContentProfile profile) => profile.SupportsPlugins;

        public void AddDependency(string projectId, string dependsOn, ContentDependencyKind kind)
        {
            if (!_dependencies.TryGetValue(projectId, out var list))
            {
                list = [];
                _dependencies[projectId] = list;
            }

            list.Add(new ContentDependency(kind, Id, dependsOn, null, dependsOn));
            var versions = _versions[projectId];
            for (var index = 0; index < versions.Count; index++)
            {
                versions[index] = versions[index] with { Dependencies = list.ToArray() };
            }
        }

        public void MakeIncompatible(string projectId) => _incompatible.Add(projectId);

        public void PublishNewVersion(
            string projectId,
            string versionNumber,
            string fileName,
            string gameVersion = "1.21.8") =>
            Publish(projectId, versionNumber, fileName, gameVersion);

        private void Publish(
            string projectId,
            string versionNumber,
            string fileName,
            string gameVersion = "1.21.8")
        {
            var path = WriteJar(Path.Combine(_sourceRoot, $"{projectId}-{versionNumber}.jar"));
            var digest = FileDigests.ComputeAsync(path, true).GetAwaiter().GetResult();
            var version = new ContentVersion(
                Id,
                projectId,
                $"{projectId}-{versionNumber}",
                versionNumber,
                ContentReleaseChannel.Release,
                DateTimeOffset.UtcNow.AddMinutes(_versions.Count),
                ["paper"],
                [gameVersion],
                new ContentFile(
                    fileName,
                    new Uri($"https://cdn.modrinth.com/{projectId}/{fileName}"),
                    new FileInfo(path).Length,
                    digest.Sha512,
                    null,
                    digest.Sha1),
                _dependencies.TryGetValue(projectId, out var list) ? list.ToArray() : []);
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
                ["paper"],
                ["1.21.8"]));

        public Task<IReadOnlyList<ContentVersion>> GetVersionsAsync(
            string projectId,
            ServerContentProfile profile,
            CancellationToken cancellationToken = default)
        {
            if (_incompatible.Contains(projectId))
            {
                // Present, but for another Minecraft version entirely.
                return Task.FromResult<IReadOnlyList<ContentVersion>>(
                    _versions[projectId]
                        .Select(version => version with { GameVersions = new[] { "1.16.5" } })
                        .ToArray());
            }

            return Task.FromResult<IReadOnlyList<ContentVersion>>(_versions[projectId].ToArray());
        }

        public async Task<ContentVersion?> ResolveCompatibleVersionAsync(
            string projectId,
            ServerContentProfile profile,
            bool allowPrerelease = false,
            CancellationToken cancellationToken = default) =>
            PluginCompatibilityPolicy.SelectBest(
                await GetVersionsAsync(projectId, profile, cancellationToken),
                profile,
                allowPrerelease);

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
            var source = Path.Combine(
                _sourceRoot,
                $"{version.ProjectId}-{version.VersionNumber}.jar");
            File.Copy(source, stagingFilePath, true);
            if (CorruptDownload)
            {
                // Same name, different bytes: exactly what hash verification exists for.
                using var archive = ZipFile.Open(stagingFilePath, ZipArchiveMode.Update);
                archive.CreateEntry($"tampered-{Guid.NewGuid():N}.class");
            }

            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryStore : IInstalledContentStore
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

    private sealed class NullAuditLog : IAuditLogStore
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
