namespace ServerManager.Client.Tests;

/// <summary>
/// Windows freezes a process's AppUserModelID at first-window-creation time, so the actual
/// runtime behavior can only be exercised inside a real WPF message loop, not a headless unit
/// test. These structural checks confirm the call is wired in at the right place and with the
/// right identity; the resulting taskbar grouping/pinning behavior itself still REQUIRES LOCAL
/// WINDOWS VALIDATION. The interop itself (writing/reading PKEY_AppUserModel_ID on a real
/// shortcut) is exercised for real in WindowsShortcutManagerTests, in the Infrastructure test
/// project.
/// </summary>
public sealed class AppUserModelIdWiringTests
{
    [Fact]
    public void ClientApp_SetsTheStableAppUserModelId_BeforeCreatingAnyWindow()
    {
        var source = ReadSource("src", "ServerManager.Client", "App.xaml.cs");
        var setIndex = source.IndexOf(
            "ProductIdentity.ApplyExplicitAppUserModelId(ProductIdentity.AppUserModelId)",
            StringComparison.Ordinal);
        var baseStartupIndex = source.IndexOf("base.OnStartup(e)", StringComparison.Ordinal);
        var windowConstructedIndex = source.IndexOf("new MainWindow(", StringComparison.Ordinal);

        Assert.True(setIndex >= 0, "App.xaml.cs must set the Stable AppUserModelID.");
        Assert.True(
            setIndex < baseStartupIndex && setIndex < windowConstructedIndex,
            "The AppUserModelID must be set before base.OnStartup and before any window is created " +
            "-- Windows freezes the identity at first-window-creation time.");
    }

    [Fact]
    public void PreviewApp_UsesADistinctAppUserModelId_NotTheStableOne()
    {
        var source = ReadSource("tools", "ServerManager.Client.Preview", "App.xaml.cs");

        Assert.Contains(
            "ProductIdentity.ApplyExplicitAppUserModelId(ProductIdentity.PreviewAppUserModelId)",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ApplyExplicitAppUserModelId(ProductIdentity.AppUserModelId)",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Launcher_SetsTheStableAppUserModelId()
    {
        var source = ReadSource("src", "ServerManager.Launcher", "Program.cs");

        Assert.Contains(
            "ProductIdentity.ApplyExplicitAppUserModelId(ProductIdentity.AppUserModelId)",
            source,
            StringComparison.Ordinal);
    }

    private static string ReadSource(params string[] parts)
    {
        var root = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (root is not null &&
               !File.Exists(Path.Combine(root.FullName, "Directory.Build.props")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine([root!.FullName, .. parts]));
    }
}
