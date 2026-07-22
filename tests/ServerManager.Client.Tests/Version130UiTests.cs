namespace ServerManager.Client.Tests;

public sealed class Version130UiTests
{
    [Fact]
    public void MainNavigation_IsFocusedAndRemovesMarketingSubtitle()
    {
        var viewModel = ReadSource(
            "src",
            "ServerManager.Client",
            "Shell",
            "MainViewModel.cs");
        var window = ReadSource(
            "src",
            "ServerManager.Client",
            "MainWindow.xaml");
        foreach (var name in new[]
                 {
                     "Home",
                     "Minecraft",
                     "Palworld",
                     "RemoteAccess",
                     "Backups",
                     "Updates",
                     "System",
                     "Settings"
                 })
        {
            Assert.Contains($"\"{name}\"", viewModel, StringComparison.Ordinal);
        }

        Assert.DoesNotContain(
            "Complete native game-server dashboard",
            window,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ConnectionLabel", window, StringComparison.Ordinal);
    }

    [Fact]
    public void PalworldDashboard_ExposesFiveManagementActionsAndNewCenters()
    {
        var xaml = ReadSource(
            "src",
            "ServerManager.Client",
            "Controls",
            "GameServerPageControl.xaml");
        foreach (var label in new[]
                 {
                     "Enable Local Management",
                     "Retry Connection",
                     "Repair Configuration",
                     "Test REST Connection",
                     "Disable Local Management",
                     "World Settings",
                     "Save &amp; Backups"
                 })
        {
            Assert.Contains(label, xaml, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void WorldSettings_HasSearchCategoriesUnsavedStateAndSafeActions()
    {
        var xaml = ReadSource(
            "src",
            "ServerManager.Client",
            "Controls",
            "PalworldWorldSettingsControl.xaml");
        foreach (var token in new[]
                 {
                     "Search settings",
                     "CategoryList",
                     "UnsavedText",
                     "Reset Category",
                     "Reset All",
                     "Undo",
                     "Compare",
                     "Export",
                     "Import",
                     "Save for Next Restart",
                     "Apply and Restart"
                 })
        {
            Assert.Contains(token, xaml, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void MemoryAndBackupCenters_SurfaceRequiredSafetyControls()
    {
        var memory = ReadSource(
            "src",
            "ServerManager.Client",
            "Controls",
            "PalworldMemoryPerformanceControl.xaml");
        var backup = ReadSource(
            "src",
            "ServerManager.Client",
            "Controls",
            "PalworldSaveBackupControl.xaml");

        Assert.Contains("APPLY HARD LIMIT", memory, StringComparison.Ordinal);
        Assert.Contains("Restore Balanced on server stop", memory, StringComparison.Ordinal);
        Assert.Contains("ActiveProfileText", memory, StringComparison.Ordinal);
        Assert.Contains("Save World Now", backup, StringComparison.Ordinal);
        Assert.Contains("Protect Backup", backup, StringComparison.Ordinal);
        Assert.Contains("Verify Backup", backup, StringComparison.Ordinal);
        Assert.Contains("Maximum total storage", backup, StringComparison.Ordinal);
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
