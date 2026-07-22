using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ServerManager.Contracts;
using ServerManager.Infrastructure.Updates;

namespace ServerManager.Infrastructure.Tests;

public sealed class VersionedUpdateInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"1salem-versioned-update-{Guid.NewGuid():N}");

    [Theory]
    [InlineData("1.2.0")]
    [InlineData("1.2.9")]
    [InlineData("1.3.0")]
    [InlineData("1.3.1")]
    public async Task StableUpdate_ActivatesClientAndUpdaterWithoutSetupOrAgentRestart(
        string installedVersion)
    {
        var (installRoot, dataRoot) = CreateInstalledLayout(installedVersion);
        var sentinel = Path.Combine(dataRoot, "game-save.sav");
        await File.WriteAllTextAsync(sentinel, "UNCHANGED SAVE");
        var package = CreateVersionedPackage("1.3.2");
        var serviceOperations = new List<string>();
        var installer = new VersionedUpdateInstaller(
            (operation, _, _, _) =>
            {
                serviceOperations.Add(operation);
                return Task.CompletedTask;
            },
            managedGameProcessDetector: () => true);

        var result = await installer.ApplyAsync(new VersionedUpdateOptions(
            package,
            installRoot,
            dataRoot,
            "1.3.2",
            "test-agent",
            ActivateAgent: false,
            SkipBinaryVersionVerification: true,
            UpdateRegistry: false));

        Assert.True(result.Success, result.Message);
        Assert.True(result.AgentUpdatePending);
        Assert.Empty(serviceOperations);
        Assert.Equal("stable-launcher-1.3.2", await File.ReadAllTextAsync(
            Path.Combine(installRoot, "Client", "1Salem.ServerManager.exe")));
        Assert.Equal("client-1.3.2", await File.ReadAllTextAsync(
            Path.Combine(
                installRoot,
                "Versions",
                "1.3.2",
                "Client",
                "1Salem.ServerManager.exe")));
        Assert.Equal("agent-old", await File.ReadAllTextAsync(
            Path.Combine(installRoot, "Agent", "1Salem.ServerManager.Agent.exe")));
        Assert.Equal("updater-1.3.2", await File.ReadAllTextAsync(
            Path.Combine(
                installRoot,
                "Client",
                "Updater",
                "1Salem.ServerManager.Updater.exe")));
        Assert.Equal("UNCHANGED SAVE", await File.ReadAllTextAsync(sentinel));
        Assert.True(VersionedUpdateInstaller.ValidateRollbackSnapshot(result.RollbackPath!));

        var manifest = JsonSerializer.Deserialize<InstalledApplicationManifest>(
            await File.ReadAllTextAsync(Path.Combine(installRoot, "current.json")),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(manifest);
        Assert.Equal("1.3.2", manifest.ActiveVersion);
        Assert.Equal(installedVersion, manifest.PreviousVersion);
        Assert.Equal(ProductIdentity.StableChannel, manifest.ReleaseChannel);
        Assert.Contains(
            Path.Combine("Versions", "1.3.2", "Client"),
            manifest.ClientExecutablePath,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NewerInstalledVersion_RejectsDowngrade()
    {
        var (installRoot, dataRoot) = CreateInstalledLayout("1.3.10");
        var installer = new VersionedUpdateInstaller(
            managedGameProcessDetector: () => true);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            installer.ApplyAsync(new VersionedUpdateOptions(
                CreateVersionedPackage("1.3.2"),
                installRoot,
                dataRoot,
                "1.3.2",
                "test-agent",
                SkipBinaryVersionVerification: true,
                UpdateRegistry: false)));

        Assert.Contains("not newer", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("client-old", await File.ReadAllTextAsync(
            Path.Combine(installRoot, "Client", "1Salem.ServerManager.exe")));
    }

    [Fact]
    public async Task SameVersion_IsRejectedUnlessExplicitlyAllowed()
    {
        var (installRoot, dataRoot) = CreateInstalledLayout("1.3.2");
        var options = new VersionedUpdateOptions(
            CreateVersionedPackage("1.3.2"),
            installRoot,
            dataRoot,
            "1.3.2",
            "test-agent",
            SkipBinaryVersionVerification: true,
            UpdateRegistry: false);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => new VersionedUpdateInstaller().ApplyAsync(options));
    }

    [Fact]
    public async Task FailedAgentActivation_RestoresClientAgentAndCurrentManifest()
    {
        var (installRoot, dataRoot) = CreateInstalledLayout("1.3.1");
        var operations = new List<string>();
        var installer = new VersionedUpdateInstaller(
            (operation, _, _, _) =>
            {
                operations.Add(operation);
                return Task.CompletedTask;
            },
            (_, _, _) => throw new InvalidOperationException("simulated health failure"),
            () => false);

        var result = await installer.ApplyAsync(new VersionedUpdateOptions(
            CreateVersionedPackage("1.3.2"),
            installRoot,
            dataRoot,
            "1.3.2",
            "test-agent",
            ActivateAgent: true,
            SkipBinaryVersionVerification: true,
            UpdateRegistry: false));

        Assert.False(result.Success);
        Assert.True(result.RolledBack);
        Assert.Equal(["stop", "start"], operations);
        Assert.Equal("client-old", await File.ReadAllTextAsync(
            Path.Combine(installRoot, "Client", "1Salem.ServerManager.exe")));
        Assert.Equal("agent-old", await File.ReadAllTextAsync(
            Path.Combine(installRoot, "Agent", "1Salem.ServerManager.Agent.exe")));
        var manifestText = await File.ReadAllTextAsync(
            Path.Combine(installRoot, "current.json"));
        Assert.Contains("1.3.1", manifestText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyOnly_ChangesNoInstalledFile()
    {
        var (installRoot, dataRoot) = CreateInstalledLayout("1.3.1");
        var result = await new VersionedUpdateInstaller().ApplyAsync(
            new VersionedUpdateOptions(
                CreateVersionedPackage("1.3.2"),
                installRoot,
                dataRoot,
                "1.3.2",
                "test-agent",
                SkipBinaryVersionVerification: true,
                UpdateRegistry: false,
                VerifyOnly: true));

        Assert.True(result.Success);
        Assert.Null(result.RollbackPath);
        Assert.Equal("client-old", await File.ReadAllTextAsync(
            Path.Combine(installRoot, "Client", "1Salem.ServerManager.exe")));
        Assert.False(Directory.Exists(Path.Combine(installRoot, "Versions", "1.3.2")));
        var rollbackParent = Path.Combine(dataRoot, "updates", "rollback");
        var workParent = Path.Combine(dataRoot, "updates", "work");
        Assert.False(Directory.Exists(rollbackParent));
        Assert.True(!Directory.Exists(workParent) || !Directory.EnumerateFileSystemEntries(workParent).Any());
    }

    [Fact]
    public async Task PreMutationFailure_ReportsThatInstalledFilesWereNotChanged()
    {
        var installRoot = Path.Combine(_root, $"blocked-install-{Guid.NewGuid():N}");
        var dataRoot = Path.Combine(_root, $"blocked-data-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataRoot);
        await File.WriteAllTextAsync(installRoot, "path intentionally occupied by a file");

        var result = await new VersionedUpdateInstaller().ApplyAsync(
            new VersionedUpdateOptions(
                CreateVersionedPackage("1.3.2"),
                installRoot,
                dataRoot,
                "1.3.2",
                "test-agent",
                SkipBinaryVersionVerification: true,
                UpdateRegistry: false));

        Assert.False(result.Success);
        Assert.False(result.RolledBack);
        Assert.Null(result.RollbackPath);
        Assert.Contains("no installed files were changed", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("path intentionally occupied by a file", await File.ReadAllTextAsync(installRoot));
    }

    private (string InstallRoot, string DataRoot) CreateInstalledLayout(string version)
    {
        var installRoot = Path.Combine(_root, $"install-{version}-{Guid.NewGuid():N}");
        var dataRoot = Path.Combine(_root, $"data-{version}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(installRoot, "Client", "Updater"));
        Directory.CreateDirectory(Path.Combine(installRoot, "Agent"));
        Directory.CreateDirectory(dataRoot);
        File.WriteAllText(
            Path.Combine(installRoot, "Client", "1Salem.ServerManager.exe"),
            "client-old");
        File.WriteAllText(
            Path.Combine(
                installRoot,
                "Client",
                "Updater",
                "1Salem.ServerManager.Updater.exe"),
            "updater-old");
        File.WriteAllText(
            Path.Combine(installRoot, "Agent", "1Salem.ServerManager.Agent.exe"),
            "agent-old");
        var manifest = new InstalledApplicationManifest(
            1,
            version,
            null,
            null,
            ProductIdentity.StableChannel,
            Path.Combine(installRoot, "Client", "1Salem.ServerManager.exe"),
            Path.Combine(installRoot, "Client", "1Salem.ServerManager.exe"),
            Path.Combine(installRoot, "Agent", "1Salem.ServerManager.Agent.exe"),
            Path.Combine(
                installRoot,
                "Client",
                "Updater",
                "1Salem.ServerManager.Updater.exe"),
            null,
            "Succeeded",
            DateTimeOffset.UtcNow);
        File.WriteAllText(
            Path.Combine(installRoot, "current.json"),
            JsonSerializer.Serialize(
                manifest,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return (installRoot, dataRoot);
    }

    private string CreateVersionedPackage(string version)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, $"update-{version}-{Guid.NewGuid():N}.zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteEntry(archive, "Client/1Salem.ServerManager.exe", $"client-{version}");
        WriteEntry(
            archive,
            "Client/Launcher/1Salem.ServerManager.Launcher.exe",
            $"stable-launcher-{version}");
        WriteEntry(
            archive,
            "Client/Updater/1Salem.ServerManager.Updater.exe",
            $"updater-{version}");
        WriteEntry(archive, "Agent/1Salem.ServerManager.Agent.exe", $"agent-{version}");
        WriteEntry(archive, "Agent/appsettings.json", "{}");
        WriteEntry(
            archive,
            "Maintenance/Uninstall 1Salem Server Manager.exe",
            $"maintenance-{version}");
        return path;
    }

    private static void WriteEntry(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(content);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
