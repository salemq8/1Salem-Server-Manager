namespace ServerManager.Client.Tests;

public sealed class Version130StabilizationTests
{
    [Fact]
    public void DesignSystem_StylesInputsDisabledLabelsAndFocusStates()
    {
        var app = ReadSource(
            "src",
            "ServerManager.Client",
            "App.xaml");

        Assert.Contains("ControlSurfaceBrush", app, StringComparison.Ordinal);
        Assert.Contains("<Style TargetType=\"TextBox\">", app, StringComparison.Ordinal);
        Assert.Contains("<Style TargetType=\"ComboBox\">", app, StringComparison.Ordinal);
        Assert.Contains("<Style TargetType=\"PasswordBox\">", app, StringComparison.Ordinal);
        Assert.Contains("Validation.HasError", app, StringComparison.Ordinal);
        Assert.Contains("IsKeyboardFocused", app, StringComparison.Ordinal);
        Assert.Contains("IsEnabled\" Value=\"False\"", app, StringComparison.Ordinal);
        Assert.Contains("Opacity\" Value=\"1\"", app, StringComparison.Ordinal);
        Assert.Contains("WarningButtonStyle", app, StringComparison.Ordinal);
        Assert.Contains("LoadingButtonStyle", app, StringComparison.Ordinal);
    }

    [Fact]
    public void Navigation_ContainsAllRequiredDestinationsAndResponsiveToggle()
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

        string[] required =
        [
            "Home",
            "Minecraft",
            "Palworld",
            "RemoteAccess",
            "Backups",
            "Updates",
            "Resources",
            "Network",
            "Files",
            "Logs",
            "Settings",
            "About"
        ];
        foreach (var destination in required)
        {
            Assert.Contains(
                $"CreateNavigationItem(\"{destination}\")",
                viewModel,
                StringComparison.Ordinal);
        }

        Assert.Contains("NavigationToggleButton", window, StringComparison.Ordinal);
        Assert.Contains("NavigationColumn", window, StringComparison.Ordinal);
    }

    [Fact]
    public void ThemesAndLanguage_IncludeFollowWindowsImmediateApplyAndRtl()
    {
        var preferences = ReadSource(
            "src",
            "ServerManager.Client",
            "Shell",
            "UiPreferences.cs");
        var appearance = ReadSource(
            "src",
            "ServerManager.Client",
            "AppearanceWindow.xaml");
        var appearanceCode = ReadSource(
            "src",
            "ServerManager.Client",
            "AppearanceWindow.xaml.cs");

        Assert.Contains("FollowWindows", preferences, StringComparison.Ordinal);
        Assert.Contains("AppsUseLightTheme", preferences, StringComparison.Ordinal);
        Assert.Contains("UserPreferenceChanged", preferences, StringComparison.Ordinal);
        Assert.Contains("Appearance.FollowWindows", appearance, StringComparison.Ordinal);
        Assert.Contains("PreferencesApplied", appearanceCode, StringComparison.Ordinal);
        Assert.Contains("LayoutDirectionService", appearanceCode, StringComparison.Ordinal);
        Assert.Contains("العربية", preferences, StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_HaveDirtyGuardDiagnosticsAndProtectedRecoveryUi()
    {
        var page = ReadSource(
            "src",
            "ServerManager.Client",
            "Controls",
            "GameServerPageControl.xaml");
        var pageCode = ReadSource(
            "src",
            "ServerManager.Client",
            "Controls",
            "GameServerPageControl.xaml.cs");
        var recovery = ReadSource(
            "src",
            "ServerManager.Client",
            "Controls",
            "ConfigurationRestorePointsControl.xaml");
        var recoveryCode = ReadSource(
            "src",
            "ServerManager.Client",
            "Controls",
            "ConfigurationRestorePointsControl.xaml.cs");
        var diagnostics = ReadSource(
            "src",
            "ServerManager.Client",
            "Controls",
            "ServerDiagnosticsControl.xaml");

        Assert.Contains("Active settings:", page, StringComparison.Ordinal);
        Assert.Contains("Pending settings:", page, StringComparison.Ordinal);
        Assert.Contains("Unsaved changes:", page, StringComparison.Ordinal);
        Assert.Contains("ServerTabs_SelectionChanged", pageCode, StringComparison.Ordinal);
        Assert.Contains("MessageBoxButton.YesNoCancel", pageCode, StringComparison.Ordinal);
        Assert.Contains("PalworldWorldSettingsControl", page, StringComparison.Ordinal);
        Assert.Contains("ConfigurationRestorePointsControl", page, StringComparison.Ordinal);
        Assert.Contains("ServerDiagnosticsControl", page, StringComparison.Ordinal);
        Assert.Contains("Restore and Verify", recovery, StringComparison.Ordinal);
        Assert.Contains("Known working", recoveryCode, StringComparison.Ordinal);
        Assert.Contains("Retry Failed Checks", diagnostics, StringComparison.Ordinal);
        Assert.Contains("Copy Report", diagnostics, StringComparison.Ordinal);
    }

    [Fact]
    public void WorldSettings_LoadAfterReceivingServerBeforeLoaded()
    {
        var source = ReadSource(
            "src",
            "ServerManager.Client",
            "Controls",
            "PalworldWorldSettingsControl.xaml.cs");

        Assert.Contains("Loaded += OnLoaded", source, StringComparison.Ordinal);
        Assert.Contains("_settings.Count == 0", source, StringComparison.Ordinal);
        Assert.Contains("HasUnsavedChanges", source, StringComparison.Ordinal);
        Assert.Contains("UnknownSettings", source, StringComparison.Ordinal);
    }

    [Fact]
    public void UpdateActions_AreDisabledUntilStateAllowsThem()
    {
        var applicationUpdate = ReadSource(
            "src",
            "ServerManager.Client",
            "Controls",
            "ApplicationUpdateControl.xaml");
        var gamePage = ReadSource(
            "src",
            "ServerManager.Client",
            "Controls",
            "GameServerPageControl.xaml");

        Assert.Contains(
            "x:Name=\"DownloadButton\"",
            applicationUpdate,
            StringComparison.Ordinal);
        Assert.Contains(
            "x:Name=\"UpdateNowButton\"",
            applicationUpdate,
            StringComparison.Ordinal);
        Assert.Contains(
            "IsEnabled=\"False\"",
            applicationUpdate,
            StringComparison.Ordinal);
        Assert.Contains(
            "x:Name=\"UpdateGameNowButton\"",
            gamePage,
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
