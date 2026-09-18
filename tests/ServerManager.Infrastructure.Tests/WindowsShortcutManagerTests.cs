using System.Runtime.Versioning;
using ServerManager.Contracts;
using ServerManager.Infrastructure.Windows;

namespace ServerManager.Infrastructure.Tests;

/// <summary>
/// AppUserModelID identity tests. Real Windows Shell COM shortcut creation/read-back (this
/// environment is genuine Windows, matching the pattern already used by
/// VersionedUpdateInstallerTests for shortcut retargeting) -- but actual taskbar
/// grouping/pinning behavior in Explorer itself still REQUIRES LOCAL WINDOWS VALIDATION, since
/// that depends on the interactive shell, not just the .lnk file's property store contents.
/// </summary>
public sealed class WindowsShortcutManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"1salem-shortcut-aumid-{Guid.NewGuid():N}");

    [Fact]
    [SupportedOSPlatform("windows")]
    public void Create_StampsTheFixedStableAppUserModelIdOntoTheShortcut()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Directory.CreateDirectory(_root);
        var target = Path.Combine(_root, "1Salem.ServerManager.exe");
        File.WriteAllText(target, "stub");
        var link = Path.Combine(_root, "1Salem Server Manager.lnk");

        WindowsShortcutManager.Create(link, target, string.Empty);

        var appUserModelId = WindowsShortcutManager.TryReadAppUserModelId(link);
        Assert.Equal(ProductIdentity.AppUserModelId, appUserModelId);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void Create_AdministratorShortcut_UsesTheSameAppUserModelIdAsTheNormalOne()
    {
        // The normal and "(Administrator)" shortcuts both launch the same Stable app with
        // different arguments -- they must share one identity so Explorer treats them as the
        // same application, not two.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Directory.CreateDirectory(_root);
        var target = Path.Combine(_root, "1Salem.ServerManager.exe");
        File.WriteAllText(target, "stub");
        var normalLink = Path.Combine(_root, "1Salem Server Manager.lnk");
        var adminLink = Path.Combine(_root, "1Salem Server Manager (Administrator).lnk");

        WindowsShortcutManager.Create(normalLink, target, string.Empty);
        WindowsShortcutManager.Create(adminLink, target, "--admin");

        Assert.Equal(
            WindowsShortcutManager.TryReadAppUserModelId(normalLink),
            WindowsShortcutManager.TryReadAppUserModelId(adminLink));
    }

    [Fact]
    public void StableAndPreviewAppUserModelIds_AreDistinct()
    {
        // The Preview/design harness must never be able to visually merge with (or steal the
        // pinned taskbar identity of) the real Stable application.
        Assert.NotEqual(ProductIdentity.AppUserModelId, ProductIdentity.PreviewAppUserModelId);
        Assert.StartsWith("1Salem.ServerManager", ProductIdentity.AppUserModelId, StringComparison.Ordinal);
        Assert.StartsWith("1Salem.ServerManager", ProductIdentity.PreviewAppUserModelId, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
