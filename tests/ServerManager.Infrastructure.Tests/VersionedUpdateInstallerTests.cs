using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using ServerManager.Contracts;
using ServerManager.Infrastructure.Updates;
using ServerManager.Infrastructure.Windows;

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
        var serviceControl = new HealthyFakeServiceControl();
        var serviceOperations = serviceControl.Operations;
        var installer = new VersionedUpdateInstaller(
            serviceControl,
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

        Assert.Contains("older than installed", exception.Message, StringComparison.OrdinalIgnoreCase);
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
    public async Task SameProductVersion_HigherBuildActivatesWithoutPatchVersion()
    {
        var (installRoot, dataRoot) = CreateInstalledLayout("1.5");
        await SetInstalledBuildAsync(installRoot, 4);

        var result = await new VersionedUpdateInstaller(
            managedGameProcessDetector: () => true).ApplyAsync(
            new VersionedUpdateOptions(
                CreateVersionedPackage("1.5"),
                installRoot,
                dataRoot,
                "1.5",
                "test-agent",
                SkipBinaryVersionVerification: true,
                UpdateRegistry: false,
                TargetBuildRevision: 5));

        Assert.True(result.Success, result.Message);
        Assert.Contains("Version 1.5 Build 5", result.Message, StringComparison.Ordinal);
        var after = JsonSerializer.Deserialize<InstalledApplicationManifest>(
            await File.ReadAllTextAsync(Path.Combine(installRoot, "current.json")),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(after);
        Assert.Equal("1.5", after.ActiveVersion);
        Assert.Equal(5, after.ActiveBuildRevision);
        Assert.Equal("1.5", after.PreviousVersion);
        Assert.Equal(4, after.PreviousBuildRevision);
        Assert.Contains(
            Path.Combine("Versions", "1.5", "Builds", "5", "Client"),
            after.ClientExecutablePath,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SameProductVersion_LowerBuildIsRejected()
    {
        var (installRoot, dataRoot) = CreateInstalledLayout("1.5");
        await SetInstalledBuildAsync(installRoot, 5);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new VersionedUpdateInstaller().ApplyAsync(new VersionedUpdateOptions(
                CreateVersionedPackage("1.5"),
                installRoot,
                dataRoot,
                "1.5",
                "test-agent",
                SkipBinaryVersionVerification: true,
                UpdateRegistry: false,
                TargetBuildRevision: 4)));

        Assert.Contains("older than installed", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task IdenticalBuild_IsAlreadyCurrentUnlessRepairModeIsExplicit()
    {
        var (installRoot, dataRoot) = CreateInstalledLayout("1.5");
        await SetInstalledBuildAsync(installRoot, 5);
        var options = new VersionedUpdateOptions(
            CreateVersionedPackage("1.5"),
            installRoot,
            dataRoot,
            "1.5",
            "test-agent",
            SkipBinaryVersionVerification: true,
            UpdateRegistry: false,
            TargetBuildRevision: 5);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new VersionedUpdateInstaller().ApplyAsync(options));
        var repaired = await new VersionedUpdateInstaller(
            managedGameProcessDetector: () => true).ApplyAsync(
            options with { AllowSameVersion = true });

        Assert.True(repaired.Success, repaired.Message);
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
        // A shortcut retargeted onto the Stable launcher by an in-app update must carry the
        // fixed Stable AppUserModelID, the same as one created fresh by Setup.exe -- otherwise
        // an already-pinned taskbar icon would stop merging with the running app's button the
        // moment an update retargets it.
        Assert.Equal(
            ProductIdentity.AppUserModelId,
            WindowsShortcutManager.TryReadAppUserModelId(shortcutPath));
    }

    [Fact]
    public void ShortcutMigration_StampsAumidOnAShortcutAlreadyTargetingTheStableLauncher()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Reproduces a real production scenario found on an actual installed 1.3.2 machine:
        // its shortcuts already pointed directly at the permanent stable launcher (1.3.2
        // already had that design) rather than a version-specific path, so
        // IsVersionedClientTarget never recognizes them as needing a retarget -- meaning the
        // AUMID stamp, which previously only ran after a successful retarget, would never be
        // applied to this shortcut during an in-app update at all. This must still stamp it.
        var installRoot = Path.Combine(_root, "already-stable-install");
        var stableLauncher = Path.Combine(installRoot, "Client", "1Salem.ServerManager.exe");
        var shortcutRoot = Path.Combine(_root, "already-stable-pinned");
        var shortcutPath = Path.Combine(shortcutRoot, "1Salem.lnk");
        Directory.CreateDirectory(Path.GetDirectoryName(stableLauncher)!);
        Directory.CreateDirectory(shortcutRoot);
        File.WriteAllText(stableLauncher, "stable launcher");
        // Points at the stable launcher already -- no Versions\<v>\... segment at all.
        CreateShortcut(shortcutPath, stableLauncher);
        Assert.Null(WindowsShortcutManager.TryReadAppUserModelId(shortcutPath));

        var result = StableShortcutMigration.RetargetInstalledShortcuts(
            installRoot,
            stableLauncher,
            [shortcutRoot]);

        Assert.Equal(1, result.Updated);
        Assert.Empty(result.Failures);
        Assert.Equal(
            Path.GetFullPath(stableLauncher),
            Path.GetFullPath(ReadShortcutTarget(shortcutPath)),
            StringComparer.OrdinalIgnoreCase);
        Assert.Equal(
            ProductIdentity.AppUserModelId,
            WindowsShortcutManager.TryReadAppUserModelId(shortcutPath));
    }

    [Fact]
    public async Task FailedAgentActivation_RestoresClientAgentAndCurrentManifest()
    {
        var (installRoot, dataRoot) = CreateInstalledLayout("1.3.1");
        var serviceControl = new HealthyFakeServiceControl();
        var operations = serviceControl.Operations;
        var installer = new VersionedUpdateInstaller(
            serviceControl,
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
        // Recovery stops the service again before restoring the Agent directory, rather than
        // starting it back up on top of files the rollback is about to replace.
        Assert.Equal(["stop", "start", "stop", "start"], operations);
        Assert.Equal("client-old", await File.ReadAllTextAsync(
            Path.Combine(installRoot, "Client", "1Salem.ServerManager.exe")));
        // Rollback restores the whole "Client" directory as a single atomic swap (the same
        // ReplaceDirectory stage/verify/rename primitive the forward install path uses, not a
        // file-by-file copy over the live directory), so the self-updater's own live Updater
        // subfolder nested inside Client comes back correctly too, in the same operation.
        Assert.Equal("updater-old", await File.ReadAllTextAsync(
            Path.Combine(installRoot, "Client", "Updater", "1Salem.ServerManager.Updater.exe")));
        Assert.Equal("agent-old", await File.ReadAllTextAsync(
            Path.Combine(installRoot, "Agent", "1Salem.ServerManager.Agent.exe")));
        var manifestText = await File.ReadAllTextAsync(
            Path.Combine(installRoot, "current.json"));
        Assert.Contains("1.3.1", manifestText, StringComparison.Ordinal);
        // No rollback staging/old-content temp directories are left behind after a successful
        // rollback: neither under installRoot (ReplaceDirectory's own ".staged-*" siblings)...
        Assert.Empty(Directory.EnumerateDirectories(installRoot, "*.staged-*", SearchOption.AllDirectories));
        // ...nor under dataRoot's per-operation work directory (this operation's own
        // workRoot, which holds "rollback-old-client"/"rollback-old-agent" until the whole
        // ApplyAsync call's outer finally block deletes it).
        var workParent = Path.Combine(dataRoot, "updates", "work");
        Assert.Empty(
            Directory.Exists(workParent)
                ? Directory.EnumerateFileSystemEntries(workParent)
                : []);
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

    private static async Task SetInstalledBuildAsync(
        string installRoot,
        int buildRevision)
    {
        var path = Path.Combine(installRoot, "current.json");
        var manifest = JsonSerializer.Deserialize<InstalledApplicationManifest>(
            await File.ReadAllTextAsync(path),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(manifest);
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(
                manifest with { ActiveBuildRevision = buildRevision },
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
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
