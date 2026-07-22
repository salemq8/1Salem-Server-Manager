namespace ServerManager.Client.Tests;

public sealed class Version131UiTests
{
    [Fact]
    public void MemoryPreset_UsesRadioSemanticsAndDistinctHoverSelectedStates()
    {
        var xaml = ReadSource(
            "src",
            "ServerManager.Client",
            "Controls",
            "PalworldMemoryPerformanceControl.xaml");

        Assert.Contains("GroupName=\"MemoryPolicyPreset\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsChecked", xaml, StringComparison.Ordinal);
        Assert.Contains("IsMouseOver", xaml, StringComparison.Ordinal);
        Assert.Contains("IsChecked\" Value=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Contains("#1C3448", xaml, StringComparison.Ordinal);
        Assert.Contains("AccentBrush", xaml, StringComparison.Ordinal);
        Assert.Contains("Save Changes", xaml, StringComparison.Ordinal);
        Assert.Contains("Cancel", xaml, StringComparison.Ordinal);
        Assert.Contains("Apply Recommended", xaml, StringComparison.Ordinal);
        Assert.Contains("Recalculate Now", xaml, StringComparison.Ordinal);
        Assert.Contains("Start Anyway Once", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void MemoryEditor_BindsCustomEditabilityAndPendingValues()
    {
        var xaml = ReadSource(
            "src",
            "ServerManager.Client",
            "Controls",
            "PalworldMemoryPerformanceControl.xaml");
        var viewModel = ReadSource(
            "src",
            "ServerManager.Client",
            "Controls",
            "MemoryPolicyEditorViewModel.cs");

        Assert.Contains("MemoryFieldsReadOnly", xaml, StringComparison.Ordinal);
        Assert.Contains("CanEditMemoryThresholds", xaml, StringComparison.Ordinal);
        Assert.Contains("UpdateSourceTrigger=PropertyChanged", xaml, StringComparison.Ordinal);
        Assert.Contains("ValidatesOnNotifyDataErrors=True", xaml, StringComparison.Ordinal);
        Assert.Contains("Active values", xaml, StringComparison.Ordinal);
        Assert.Contains("Pending values", xaml, StringComparison.Ordinal);
        Assert.Contains("SelectedMemoryPolicyPreset", viewModel, StringComparison.Ordinal);
        Assert.Contains("INotifyPropertyChanged", viewModel, StringComparison.Ordinal);
        Assert.Contains("INotifyDataErrorInfo", viewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void Layout_KeepsSaveBarOutsideScrollableContentAndSupportsRtl()
    {
        var xaml = ReadSource(
            "src",
            "ServerManager.Client",
            "Controls",
            "PalworldMemoryPerformanceControl.xaml");
        var appManifest = ReadSource(
            "src",
            "ServerManager.Client",
            "app.manifest");
        var preferences = ReadSource(
            "src",
            "ServerManager.Client",
            "Shell",
            "UiPreferences.cs");

        Assert.Contains("<Grid.RowDefinitions>", xaml, StringComparison.Ordinal);
        Assert.Contains("Grid.Row=\"1\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Save Changes", xaml, StringComparison.Ordinal);
        Assert.Contains("PerMonitorV2", appManifest, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("RightToLeft", preferences, StringComparison.Ordinal);
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
