using System.Drawing;
using System.Security.Cryptography;
using System.Text;

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
        using var log = new InstallerLog();
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
        foreach (var executable in new[]
                 {
                     @"src\ServerManager.Client\bin\Release\net8.0-windows\1Salem.ServerManager.exe",
                     @"src\ServerManager.Agent\bin\Release\net8.0\1Salem.ServerManager.Agent.exe",
                     @"src\ServerManager.Updater\bin\Release\net8.0-windows\1Salem.ServerManager.Updater.exe",
                     @"tools\ServerManager.Setup\bin\Release\net8.0-windows\1Salem.ServerManager.Setup.exe"
                 })
        {
            var path = Path.Combine(root, executable);
            Assert.True(File.Exists(path), path);
            var digest = IconDigest(path);
            productIcon ??= digest;
            Assert.Equal(productIcon, digest);
        }

        Assert.NotNull(productIcon);
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
