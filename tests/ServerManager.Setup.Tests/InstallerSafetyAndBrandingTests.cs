using System.Drawing;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ServerManager.Contracts;

namespace ServerManager.Setup.Tests;

public sealed class InstallerSafetyAndBrandingTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        $"1salem-setup-tests-{Guid.NewGuid():N}");

    [Fact]
    public void SetupNotElevated_IsRejectedBeforeServiceInstallation()
    {
        var request = new InstallRequest(
            "AllInOne",
            @"C:\Program Files\1Salem Server Manager",
            true,
            true,
            true,
            false,
            true,
            true);

        var exception = Assert.Throws<InstallerFailureException>(
            () => InstallerPreflight.ValidateRequest(request, isElevated: false));

        Assert.Equal("Administrator preflight", exception.Step);
    }

    [Fact]
    public async Task FileRollback_NeverDeletesDataOutsideInstallRoot()
    {
        var installRoot = Path.Combine(_tempRoot, "application");
        var backupRoot = Path.Combine(_tempRoot, "rollback");
        var sourceRoot = Path.Combine(_tempRoot, "source");
        var protectedData = Path.Combine(
            _tempRoot,
            "ProgramData",
            "Minecraft",
            "world",
            "level.dat");
        Directory.CreateDirectory(Path.GetDirectoryName(protectedData)!);
        File.WriteAllText(protectedData, "world-data");
        Directory.CreateDirectory(sourceRoot);
        var source = Path.Combine(sourceRoot, "new.exe");
        File.WriteAllText(source, "new application file");
        Directory.CreateDirectory(installRoot);

        var journal = new DeploymentJournal(installRoot, backupRoot);
        var deployed = Path.Combine(installRoot, "Client", "new.exe");
        journal.DeployFile(source, deployed);
        using var log = new InstallerLog(Path.Combine(_tempRoot, "logs"));
        await journal.RollBackAsync(log, CancellationToken.None);

        Assert.False(File.Exists(deployed));
        Assert.Equal("world-data", File.ReadAllText(protectedData));
    }

    [Fact]
    public void OfficialIconResourcesExistAndPngHasTransparency()
    {
        var root = FindRepositoryRoot();
        var pngPath = Path.Combine(
            root,
            "assets",
            "branding",
            "1SalemServerManager-Icon.png");
        var icoPath = Path.Combine(
            root,
            "assets",
            "branding",
            "1SalemServerManager.ico");
        Assert.True(File.Exists(pngPath));
        Assert.True(File.Exists(icoPath));

        using var bitmap = new Bitmap(pngPath);
        Assert.Equal(0, bitmap.GetPixel(0, 0).A);
        Assert.Equal(255, bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2).A);
    }

    [Fact]
    public void GeneratedIco_ContainsEveryRequiredDimensionAsPng()
    {
        var icoPath = Path.Combine(
            FindRepositoryRoot(),
            "assets",
            "branding",
            "1SalemServerManager.ico");
        var entries = ReadIcoEntries(icoPath);
        Assert.Equal(
            [16, 20, 24, 32, 40, 48, 64, 128, 256],
            entries.Select(entry => entry.Size).ToArray());
        Assert.All(
            entries,
            entry => Assert.Equal(
                [0x89, 0x50, 0x4E, 0x47],
                entry.Signature));
    }

    [Fact]
    public void EveryExecutableProjectDeclaresOfficialApplicationIcon()
    {
        var root = FindRepositoryRoot();
        foreach (var project in new[]
                 {
                     @"src\ServerManager.Client\ServerManager.Client.csproj",
                     @"src\ServerManager.Agent\ServerManager.Agent.csproj",
                     @"src\ServerManager.Updater\ServerManager.Updater.csproj",
                     @"tools\ServerManager.Setup\ServerManager.Setup.csproj"
                 })
        {
            var text = File.ReadAllText(Path.Combine(root, project));
            Assert.Contains(
                "<ApplicationIcon>..\\..\\assets\\branding\\" +
                "1SalemServerManager.ico</ApplicationIcon>",
                text,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void BuiltExecutablesContainTheSameConfiguredProductIcon()
    {
        var root = FindRepositoryRoot();
        string? productIcon = null;
        foreach (var (projectDirectory, executableName) in new[]
                 {
                     (@"src\ServerManager.Client", "1Salem.ServerManager.exe"),
                     (@"src\ServerManager.Agent", "1Salem.ServerManager.Agent.exe"),
                     (@"src\ServerManager.Updater", "1Salem.ServerManager.Updater.exe"),
                     (@"tools\ServerManager.Setup", "1Salem.ServerManager.Setup.exe")
                 })
        {
            var path = ResolveReleaseOutputPath(root, projectDirectory, executableName);
            Assert.True(File.Exists(path), path);
            var digest = IconDigest(path);
            productIcon ??= digest;
            Assert.Equal(productIcon, digest);
        }

        Assert.NotNull(productIcon);
    }

    // Resolves the actual Release output path from the project's own <TargetFramework>
    // instead of a hard-coded TFM directory, so a future TargetFramework change cannot
    // silently desynchronize this test from the real build output (as previously happened
    // for ServerManager.Agent, which is net8.0-windows but was hard-coded here as net8.0).
    private static string ResolveReleaseOutputPath(
        string root,
        string projectDirectoryRelative,
        string outputFileName)
    {
        var projectDirectory = Path.Combine(root, projectDirectoryRelative);
        var csprojFile = Directory.EnumerateFiles(projectDirectory, "*.csproj").Single();
        var match = Regex.Match(
            File.ReadAllText(csprojFile),
            @"<TargetFramework>(?<tfm>[^<]+)</TargetFramework>");
        if (!match.Success)
        {
            throw new InvalidOperationException(
                $"Could not resolve <TargetFramework> from {csprojFile}.");
        }

        return Path.Combine(
            projectDirectory,
            "bin",
            "Release",
            match.Groups["tfm"].Value,
            outputFileName);
    }

    [Fact]
    public void ShortcutDefinitionsUseTargetExecutableAsIcon()
    {
        var root = FindRepositoryRoot();
        var installer = File.ReadAllText(
            Path.Combine(
                root,
                "tools",
                "ServerManager.Setup",
                "InstallerEngine.cs"));
        var application = File.ReadAllText(
            Path.Combine(
                root,
                "src",
                "ServerManager.Infrastructure",
                "Windows",
                "WindowsShortcutManager.cs"));

        Assert.Contains("shortcut.IconLocation = $\"{iconPath},0\"", installer);
        Assert.Contains("shellLink.SetIconLocation(Path.GetFullPath(targetPath), 0)", application);
    }

    [Fact]
    public void CreateShortcuts_StampsTheStableAppUserModelIdOnEveryAppLaunchingShortcut()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Exercises the real Setup.exe shortcut-creation path (WScript.Shell, the same
        // mechanism a real install uses) end to end: a prior independent review found that the
        // installer's real shortcuts never received the AppUserModelID property at all, only a
        // manually-triggered "Create Shortcuts" admin-tools button did -- meaning the P0-level
        // AppUserModelID repair never actually reached shortcuts a normal install/pin produces.
        var clientExecutable = Path.Combine(_tempRoot, "Client", "1Salem.ServerManager.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(clientExecutable)!);
        File.WriteAllText(clientExecutable, "client");
        var desktop = Path.Combine(_tempRoot, "Desktop");
        var startMenu = Path.Combine(_tempRoot, "StartMenu");
        Directory.CreateDirectory(desktop);
        Directory.CreateDirectory(startMenu);
        var installRoot = Path.Combine(_tempRoot, "install");
        Directory.CreateDirectory(installRoot);
        File.WriteAllText(
            Path.Combine(installRoot, "Uninstall 1Salem Server Manager.exe"),
            "uninstaller");

        var desktopShortcut = Path.Combine(desktop, "1Salem Server Manager.lnk");
        var startMenuFolder = Path.Combine(startMenu, "1Salem Server Manager");
        Directory.CreateDirectory(startMenuFolder);
        InstallerEngine.CreateShortcut(desktopShortcut, clientExecutable, string.Empty, clientExecutable, stampAppUserModelId: true);
        InstallerEngine.CreateShortcut(
            Path.Combine(startMenuFolder, "1Salem Server Manager (Administrator).lnk"),
            clientExecutable,
            "--admin",
            clientExecutable,
            stampAppUserModelId: true);
        var uninstallShortcut = Path.Combine(startMenuFolder, "Uninstall 1Salem Server Manager.lnk");
        InstallerEngine.CreateShortcut(
            uninstallShortcut,
            Path.Combine(installRoot, "Uninstall 1Salem Server Manager.exe"),
            "--uninstall",
            Path.Combine(installRoot, "Uninstall 1Salem Server Manager.exe"));

        Assert.Equal(
            ProductIdentity.AppUserModelId,
            ProductIdentity.TryReadShortcutAppUserModelId(desktopShortcut));
        Assert.Equal(
            ProductIdentity.AppUserModelId,
            ProductIdentity.TryReadShortcutAppUserModelId(
                Path.Combine(startMenuFolder, "1Salem Server Manager (Administrator).lnk")));
        // The uninstaller shortcut launches a different utility, not the app itself, so it
        // must NOT carry the app's AppUserModelID.
        Assert.Null(ProductIdentity.TryReadShortcutAppUserModelId(uninstallShortcut));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, true);
        }
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "VERSION")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private static IReadOnlyList<IcoEntry> ReadIcoEntries(string path)
    {
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(0, BitConverter.ToUInt16(bytes, 0));
        Assert.Equal(1, BitConverter.ToUInt16(bytes, 2));
        var count = BitConverter.ToUInt16(bytes, 4);
        var entries = new List<IcoEntry>(count);
        for (var index = 0; index < count; index++)
        {
            var entryOffset = 6 + (index * 16);
            var size = bytes[entryOffset] == 0 ? 256 : bytes[entryOffset];
            var dataOffset = BitConverter.ToInt32(bytes, entryOffset + 12);
            entries.Add(
                new IcoEntry(
                    size,
                    bytes[dataOffset..(dataOffset + 4)]));
        }

        return entries;
    }

    private static string IconDigest(string path)
    {
        using var icon = Icon.ExtractAssociatedIcon(path) ??
            throw new InvalidDataException($"No icon found in {path}");
        using var bitmap = icon.ToBitmap();
        using var stream = new MemoryStream();
        bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    private sealed record IcoEntry(int Size, byte[] Signature);
}
