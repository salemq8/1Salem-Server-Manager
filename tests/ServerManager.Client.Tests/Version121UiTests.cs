namespace ServerManager.Client.Tests;

public sealed class Version121UiTests
{
    [Fact]
    public void GlobalStyles_KeepTabsAndDisabledButtonsReadable()
    {
        var xaml = ReadSource("src", "ServerManager.Client", "App.xaml");

        Assert.Contains(
            "<Style TargetType=\"TabItem\">",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "<Trigger Property=\"IsSelected\" Value=\"True\">",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "<Trigger Property=\"IsEnabled\" Value=\"False\">",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains("#F1F6FA", xaml, StringComparison.Ordinal);
        Assert.Contains("#405064", xaml, StringComparison.Ordinal);
        Assert.Contains("#43B581", xaml, StringComparison.Ordinal);
        Assert.Contains("#2CB8B3", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void PalworldDashboard_ShowsLiveMetricsPlayersAndBothAddresses()
    {
        var xaml = ReadSource(
            "src",
            "ServerManager.Client",
            "Controls",
            "GameServerPageControl.xaml");

        Assert.Contains(
            "PalworldManagementStatusText",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains("PalworldFpsText", xaml, StringComparison.Ordinal);
        Assert.Contains("PalworldPlayersGrid", xaml, StringComparison.Ordinal);
        Assert.Contains("InternetAddressText", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "Copy Internet Address",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Enable Local Management",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "ServerResourceGraph.AddSample",
            ReadSource(
                "src",
                "ServerManager.Client",
                "Controls",
                "GameServerPageControl.xaml.cs"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void BackupAndUpdateStatus_UseSeparateWrappingLines()
    {
        var xaml = ReadSource(
            "src",
            "ServerManager.Client",
            "Controls",
            "GameServerPageControl.xaml");

        Assert.Contains("x:Name=\"BackupText\"", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "x:Name=\"UpdateStatusText\"",
            xaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "BackupUpdateText",
            xaml,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Client_DeclaresPerMonitorHighDpiAndInheritsRtlFlow()
    {
        var manifest = ReadSource(
            "src",
            "ServerManager.Client",
            "app.manifest");
        var mainWindow = ReadSource(
            "src",
            "ServerManager.Client",
            "MainWindow.xaml");

        Assert.Contains("PerMonitorV2", manifest, StringComparison.Ordinal);
        Assert.Contains(
            "FlowDirection=\"{Binding FlowDirection}\"",
            mainWindow,
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
        return File.ReadAllText(
            Path.Combine([root!.FullName, .. parts]));
    }
}
