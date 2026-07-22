using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
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
    public async Task ExplicitSameVersionRepair_PreservesPreviousRollbackVersion()
    {
        var (installRoot, dataRoot) = CreateInstalledLayout("1.3.2");
        var manifestPath = Path.Combine(installRoot, "current.json");
        var manifest = JsonSerializer.Deserialize<InstalledApplicationManifest>(
            await File.ReadAllTextAsync(manifestPath),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(manifest);
        await File.WriteAllTextAsync(
            manifestPath,
            JsonSerializer.Serialize(
                manifest with
                {
                    PreviousVersion = "1.3.1",
                    RollbackVersion = "1.3.1"
                },
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        var result = await new VersionedUpdateInstaller(
            managedGameProcessDetector: () => true).ApplyAsync(
            new VersionedUpdateOptions(
                CreateVersionedPackage("1.3.2"),
                installRoot,
                dataRoot,
                "1.3.2",
                "test-agent",
                AllowSameVersion: true,
                SkipBinaryVersionVerification: true,
                UpdateRegistry: false));

        Assert.True(result.Success, result.Message);
        var after = JsonSerializer.Deserialize<InstalledApplicationManifest>(
            await File.ReadAllTextAsync(manifestPath),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(after);
        Assert.Equal("1.3.1", after.PreviousVersion);
        Assert.Equal("1.3.1", after.RollbackVersion);
    }

    [Theory]
    [InlineData(@"Versions\1.3.1\Client\1Salem.ServerManager.exe", true)]
    [InlineData(@"Versions\1.3.2\Client\1Salem.ServerManager.exe", true)]
    [InlineData(@"Client\1Salem.ServerManager.exe", false)]
    [InlineData(@"Versions\1.3.2\Agent\1Salem.ServerManager.Agent.exe", false)]
    public void ShortcutMigration_OnlyRetargetsVersionedClientExecutables(
        string relativeTarget,
        bool expected)
    {
        var installRoot = Path.Combine(_root, "shortcut-install");
        var target = Path.Combine(installRoot, relativeTarget);

        Assert.Equal(
            expected,
            StableShortcutMigration.IsVersionedClientTarget(installRoot, target));
    }

    [Fact]
    public void ShortcutMigration_PreservesLinkAndRetargetsPermanentLauncher()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var installRoot = Path.Combine(_root, "real-shortcut-install");
        var stableLauncher = Path.Combine(
            installRoot,
            "Client",
            "1Salem.ServerManager.exe");
        var oldClient = Path.Combine(
            installRoot,
            "Versions",
            "1.3.1",
            "Client",
            "1Salem.ServerManager.exe");
        var shortcutRoot = Path.Combine(_root, "pinned-shortcuts");
        var shortcutPath = Path.Combine(shortcutRoot, "1Salem.lnk");
        Directory.CreateDirectory(Path.GetDirectoryName(stableLauncher)!);
        Directory.CreateDirectory(Path.GetDirectoryName(oldClient)!);
        Directory.CreateDirectory(shortcutRoot);
        File.WriteAllText(stableLauncher, "stable launcher");
        File.WriteAllText(oldClient, "old client");
        CreateShortcut(shortcutPath, oldClient);

        var result = StableShortcutMigration.RetargetInstalledShortcuts(
            installRoot,
            stableLauncher,
            [shortcutRoot]);

        Assert.True(File.Exists(shortcutPath));
        Assert.Equal(1, result.Updated);
        Assert.Empty(result.Failures);
        Assert.Equal(
            Path.GetFullPath(stableLauncher),
            Path.GetFullPath(ReadShortcutTarget(shortcutPath)),
            StringComparer.OrdinalIgnoreCase);
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

    [SupportedOSPlatform("windows")]
    private static void CreateShortcut(string shortcutPath, string targetPath)
    {
        WithShortcut(shortcutPath, (shortcut, shortcutType) =>
        {
            shortcutType.InvokeMember(
                "TargetPath",
                BindingFlags.SetProperty,
                null,
                shortcut,
                [targetPath]);
            shortcutType.InvokeMember(
                "Save",
                BindingFlags.InvokeMethod,
                null,
                shortcut,
                null);
            return string.Empty;
        });
    }

    [SupportedOSPlatform("windows")]
    private static string ReadShortcutTarget(string shortcutPath) =>
        WithShortcut(shortcutPath, (shortcut, shortcutType) =>
            shortcutType.InvokeMember(
                "TargetPath",
                BindingFlags.GetProperty,
                null,
                shortcut,
                null) as string ?? string.Empty);

    [SupportedOSPlatform("windows")]
    private static string WithShortcut(
        string shortcutPath,
        Func<object, Type, string> action)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")!;
        object? shell = null;
        object? shortcut = null;
        try
        {
            shell = Activator.CreateInstance(shellType)!;
            shortcut = shellType.InvokeMember(
                "CreateShortcut",
                BindingFlags.InvokeMethod,
                null,
                shell,
                [shortcutPath])!;
            return action(shortcut, shortcut.GetType());
        }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut))
            {
                _ = Marshal.FinalReleaseComObject(shortcut);
            }

            if (shell is not null && Marshal.IsComObject(shell))
            {
                _ = Marshal.FinalReleaseComObject(shell);
            }
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
