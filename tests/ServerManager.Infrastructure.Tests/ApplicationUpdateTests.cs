using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ServerManager.Contracts;
using ServerManager.Core;
using ServerManager.Infrastructure.Persistence;
using ServerManager.Infrastructure.Updates;

namespace ServerManager.Infrastructure.Tests;

public sealed class ApplicationUpdateTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"1salem-update-tests-{Guid.NewGuid():N}");

    [Fact]
    public void ManifestParsing_ValidatesAllRequiredFields()
    {
        var manifest = ApplicationUpdateManifestReader.Read(
            CreateManifestJson(
                "1.3.0",
                123,
                new string('A', 64)),
            ApplicationUpdateChannel.Stable);

        Assert.Equal("1.3.0", manifest.Version);
        Assert.True(manifest.RequiresServiceRestart);
        Assert.False(manifest.RequiresFullSetup);
    }

    [Fact]
    public void ManifestParsing_RejectsNonHttpsSources()
    {
        var json = CreateManifestJson(
            "1.3.0",
            123,
            new string('A', 64),
            "http://updates.test");

        Assert.Throws<InvalidDataException>(
            () => ApplicationUpdateManifestReader.Read(
                json,
                ApplicationUpdateChannel.Stable));
    }

    [Fact]
    public async Task Sha256Verification_AcceptsExactPackageAndRejectsCorruption()
    {
        var path = Path.Combine(CreateDirectory("hash"), "package.zip");
        await File.WriteAllBytesAsync(path, Encoding.UTF8.GetBytes("verified bytes"));
        var bytes = await File.ReadAllBytesAsync(path);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));

        await UpdatePackageSecurity.VerifyFileAsync(path, bytes.Length, hash);
        await Assert.ThrowsAsync<InvalidDataException>(
            () => UpdatePackageSecurity.VerifyFileAsync(
                path,
                bytes.Length,
                new string('0', 64)));
    }

    [Fact]
    public void ZipSafety_RejectsTraversal()
    {
        var package = Path.Combine(CreateDirectory("traversal"), "unsafe.zip");
        using (var archive = ZipFile.Open(package, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "Client/1Salem.ServerManager.exe", "client");
            WriteEntry(archive, "Agent/1Salem.ServerManager.Agent.exe", "agent");
            WriteEntry(archive, "../outside.txt", "escape");
        }

        Assert.Throws<InvalidDataException>(
            () => UpdatePackageSecurity.ValidateArchive(package));
    }

    [Fact]
    public void ZipSafety_RejectsScripts()
    {
        var package = Path.Combine(CreateDirectory("script"), "unsafe.zip");
        using (var archive = ZipFile.Open(package, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "Client/1Salem.ServerManager.exe", "client");
            WriteEntry(archive, "Agent/1Salem.ServerManager.Agent.exe", "agent");
            WriteEntry(archive, "Client/install.ps1", "danger");
        }

        Assert.Throws<InvalidDataException>(
            () => UpdatePackageSecurity.ValidateArchive(package));
    }

    [Fact]
    public void ZipSafety_RejectsCaseCollisions()
    {
        var package = Path.Combine(CreateDirectory("case-collision"), "unsafe.zip");
        using (var archive = ZipFile.Open(package, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "Client/1Salem.ServerManager.exe", "client");
            WriteEntry(archive, "client/1salem.servermanager.exe", "collision");
            WriteEntry(archive, "Agent/1Salem.ServerManager.Agent.exe", "agent");
        }

        Assert.Throws<InvalidDataException>(
            () => UpdatePackageSecurity.ValidateArchive(package));
    }

    [Theory]
    [InlineData("Client/ProgramData/server-manager.db")]
    [InlineData("Agent/SaveGames/Level.sav")]
    [InlineData("Client/playit.toml")]
    [InlineData("Client/backups/private.zip")]
    public void ZipSafety_RejectsServerDataAndSecretLikeFiles(string unsafePath)
    {
        var package = Path.Combine(CreateDirectory(Guid.NewGuid().ToString("N")), "unsafe.zip");
        using (var archive = ZipFile.Open(package, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "Client/1Salem.ServerManager.exe", "client");
            WriteEntry(archive, "Agent/1Salem.ServerManager.Agent.exe", "agent");
            WriteEntry(archive, unsafePath, "private");
        }

        Assert.Throws<InvalidDataException>(
            () => UpdatePackageSecurity.ValidateArchive(package));
    }

    [Fact]
    public async Task UpdateStaging_DownloadsAndVerifiesPackage()
    {
        var package = CreatePackageBytes();
        var hash = Convert.ToHexString(SHA256.HashData(package));
        var handler = new UpdateHttpHandler(
            CreateManifestJson("9.9.9", package.Length, hash),
            package);
        var coordinator = CreateCoordinator(handler, "staging");
        await coordinator.InitializeAsync();

        var check = await coordinator.CheckAsync();
        var downloaded = await coordinator.DownloadAsync(
            new ApplicationUpdateActionRequest());

        Assert.True(check.IsUpdateAvailable);
        Assert.Equal(ApplicationUpdateStage.ReadyToInstall, downloaded.Stage);
        Assert.NotNull(downloaded.StagedPackagePath);
        Assert.True(File.Exists(downloaded.StagedPackagePath));
    }

    [Fact]
    public async Task OfflineUpdateCheck_FailsWithoutChangingApplicationFiles()
    {
        var coordinator = CreateCoordinator(
            new ThrowingHttpHandler(),
            "offline");
        await coordinator.InitializeAsync();

        var result = await coordinator.CheckAsync();

        Assert.Equal(ApplicationUpdateStage.Failed, result.Stage);
        Assert.Contains("offline", result.LastError!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CorruptDownloadedPackage_IsRejected()
    {
        var package = CreatePackageBytes();
        var handler = new UpdateHttpHandler(
            CreateManifestJson(
                "9.9.9",
                package.Length,
                new string('0', 64)),
            package);
        var coordinator = CreateCoordinator(handler, "corrupt");
        await coordinator.InitializeAsync();
        await coordinator.CheckAsync();

        var result = await coordinator.DownloadAsync(
            new ApplicationUpdateActionRequest());

        Assert.Equal(ApplicationUpdateStage.Failed, result.Stage);
        Assert.Null(result.StagedPackagePath);
    }

    [Fact]
    public async Task Applier_RestartsAgentAndPreservesProtectedData()
    {
        var installRoot = CreateInstall("success", "old");
        var dataRoot = CreateDirectory("protected-data");
        var sentinel = Path.Combine(dataRoot, "game-save.sav");
        await File.WriteAllTextAsync(sentinel, "DO NOT CHANGE");
        var package = CreatePackageFile("success-package");
        var operations = new List<string>();
        var applier = new UpdatePackageApplier(
            serviceCommand: (operation, service, allowStopped, token) =>
            {
                operations.Add(operation);
                return Task.CompletedTask;
            });

        var result = await applier.ApplyAsync(new UpdateApplyOptions(
            package,
            installRoot,
            dataRoot,
            "1.3.0",
            true,
            "test-agent",
            SkipHealthCheck: true,
            SkipVersionVerification: true));

        Assert.True(result.Success);
        Assert.Equal(["stop", "start"], operations);
        Assert.Equal("new-client", await File.ReadAllTextAsync(
            Path.Combine(installRoot, "Client", "1Salem.ServerManager.exe")));
        Assert.Equal("DO NOT CHANGE", await File.ReadAllTextAsync(sentinel));
    }

    [Fact]
    public async Task Applier_FailureRollsBackApplicationAndPreservesProgramData()
    {
        var installRoot = CreateInstall("rollback", "old");
        var dataRoot = CreateDirectory("rollback-data");
        var sentinel = Path.Combine(dataRoot, "server-manager.db");
        await File.WriteAllTextAsync(sentinel, "DATABASE");
        var package = CreatePackageFile("rollback-package");
        var applier = new UpdatePackageApplier(
            _ => throw new IOException("simulated post-swap failure"),
            (_, _, _, _) => Task.CompletedTask);

        var result = await applier.ApplyAsync(new UpdateApplyOptions(
            package,
            installRoot,
            dataRoot,
            "1.3.0",
            true,
            "test-agent",
            SkipHealthCheck: true,
            SkipVersionVerification: true));

        Assert.False(result.Success);
        Assert.True(result.RolledBack);
        Assert.Equal("old-client", await File.ReadAllTextAsync(
            Path.Combine(installRoot, "Client", "1Salem.ServerManager.exe")));
        Assert.Equal("old-agent", await File.ReadAllTextAsync(
            Path.Combine(installRoot, "Agent", "1Salem.ServerManager.Agent.exe")));
        Assert.Equal("DATABASE", await File.ReadAllTextAsync(sentinel));
    }

    [Fact]
    public async Task HealthFailure_StopsRestartedAgentBeforeRollbackAndStartsItAgain()
    {
        var installRoot = CreateInstall("health-rollback", "old");
        var dataRoot = CreateDirectory("health-rollback-data");
        var package = CreatePackageFile("health-rollback-package");
        var operations = new List<string>();
        var applier = new UpdatePackageApplier(
            serviceCommand: (operation, _, _, _) =>
            {
                operations.Add(operation);
                return Task.CompletedTask;
            },
            healthVerifier: (_, _, _) =>
                throw new InvalidOperationException("simulated unhealthy Agent"));

        var result = await applier.ApplyAsync(new UpdateApplyOptions(
            package,
            installRoot,
            dataRoot,
            "1.3.0",
            true,
            "test-agent",
            SkipVersionVerification: true));

        Assert.False(result.Success);
        Assert.True(result.RolledBack);
        Assert.Equal(["stop", "start", "stop", "start"], operations);
        Assert.Equal("old-agent", await File.ReadAllTextAsync(
            Path.Combine(installRoot, "Agent", "1Salem.ServerManager.Agent.exe")));
    }

    private ApplicationUpdateCoordinator CreateCoordinator(
        HttpMessageHandler handler,
        string name)
    {
        var dataRoot = CreateDirectory(name);
        var settings = new InMemorySettingsStore();
        return new ApplicationUpdateCoordinator(
            new HttpClient(handler),
            settings,
            new EmptyServerStore(),
            new SqliteStorageOptions(dataRoot),
            new ApplicationUpdateSourceOptions(
                _ => new Uri("https://updates.test/version.json")),
            NullLogger<ApplicationUpdateCoordinator>.Instance);
    }

    private string CreateInstall(string name, string marker)
    {
        var root = CreateDirectory($"install-{name}");
        Directory.CreateDirectory(Path.Combine(root, "Client"));
        Directory.CreateDirectory(Path.Combine(root, "Agent"));
        File.WriteAllText(
            Path.Combine(root, "Client", "1Salem.ServerManager.exe"),
            $"{marker}-client");
        File.WriteAllText(
            Path.Combine(root, "Agent", "1Salem.ServerManager.Agent.exe"),
            $"{marker}-agent");
        return root;
    }

    private string CreatePackageFile(string name)
    {
        var directory = CreateDirectory(name);
        var path = Path.Combine(directory, "update.zip");
        File.WriteAllBytes(path, CreatePackageBytes());
        return path;
    }

    private static byte[] CreatePackageBytes()
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "Client/1Salem.ServerManager.exe", "new-client");
            WriteEntry(archive, "Client/readme.txt", "client payload");
            WriteEntry(archive, "Agent/1Salem.ServerManager.Agent.exe", "new-agent");
            WriteEntry(archive, "Agent/appsettings.json", "{}");
        }

        return memory.ToArray();
    }

    private static void WriteEntry(
        ZipArchive archive,
        string path,
        string content)
    {
        var entry = archive.CreateEntry(path);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(content);
    }

    private static string CreateManifestJson(
        string version,
        long size,
        string sha256,
        string root = "https://updates.test")
    {
        var manifest = new ApplicationUpdateManifest(
            version,
            "1.1.0",
            "Stable",
            $"{root}/package.zip",
            size,
            sha256,
            $"{root}/notes.md",
            DateTimeOffset.UtcNow,
            true,
            true,
            false,
            BuildRevision: 1);
        return JsonSerializer.Serialize(
            manifest,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private string CreateDirectory(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class UpdateHttpHandler(string manifest, byte[] package)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            HttpContent content = request.RequestUri!.AbsolutePath switch
            {
                "/version.json" => new StringContent(
                    manifest,
                    Encoding.UTF8,
                    "application/json"),
                "/notes.md" => new StringContent(
                    "# Release notes",
                    Encoding.UTF8,
                    "text/markdown"),
                "/package.zip" => new ByteArrayContent(package),
                _ => throw new InvalidOperationException("Unexpected test URL.")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content
            });
        }
    }

    private sealed class ThrowingHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new HttpRequestException("offline test endpoint");
    }

    private sealed class InMemorySettingsStore : ISettingsStore
    {
        private readonly ConcurrentDictionary<string, object?> _values =
            new(StringComparer.Ordinal);

        public Task<T?> GetAsync<T>(
            string key,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                _values.TryGetValue(key, out var value) ? (T?)value : default);

        public Task SetAsync<T>(
            string key,
            T value,
            CancellationToken cancellationToken = default)
        {
            _values[key] = value;
            return Task.CompletedTask;
        }
    }

    private sealed class EmptyServerStore : IGameServerStore
    {
        public Task<IReadOnlyList<GameServerDefinition>> ListAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GameServerDefinition>>([]);

        public Task<GameServerDefinition?> GetAsync(
            Guid serverId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<GameServerDefinition?>(null);

        public Task UpsertAsync(
            GameServerDefinition server,
            ServerState state,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SetStateAsync(
            Guid serverId,
            ServerState state,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
