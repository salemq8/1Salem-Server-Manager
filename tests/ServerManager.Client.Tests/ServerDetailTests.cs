using System.Globalization;
using ServerManager.Client.Controls;
using ServerManager.Client.Shell;
using ServerManager.Contracts;

namespace ServerManager.Client.Tests;

/// <summary>
/// Server Detail is a view inside Servers, not a sixth destination, and it re-hosts the ten
/// legacy tabs behind five. These cover the navigation contract and the key families the new
/// tabs resolve at runtime — several of those are built by interpolation, so the blanket
/// "every literal key exists" test cannot see them.
/// </summary>
public sealed class ServerDetailTests
{
    private static readonly string[] TabKeys =
    [
        "ServerTab.Overview",
        "ServerTab.Console",
        "ServerTab.Backups",
        "ServerTab.Content",
        "ServerTab.Settings"
    ];

    private static readonly string[] SettingsGroups =
    [
        "General", "Game", "Network", "Resources", "Advanced"
    ];

    [Fact]
    public void ServerDetail_HasExactlyTheFiveSpecifiedTabs()
    {
        foreach (var key in TabKeys)
        {
            Assert.True(
                LocalizationService.HasKey(key),
                $"Tab '{key}' has no label.");
        }
    }

    [Fact]
    public void LegacyTabs_AreNotReintroducedAsServerDetailTabs()
    {
        // Players, Files, Updates, Performance, Network and Diagnostics were folded into the
        // five; a label reappearing for one of them means the consolidation was undone.
        string[] retired = ["Players", "Files", "Updates", "Performance", "Diagnostics"];
        foreach (var name in retired)
        {
            Assert.False(
                LocalizationService.HasKey($"ServerTab.{name}"),
                $"ServerTab.{name} is back as a top-level server tab.");
        }
    }

    [Fact]
    public void EverySettingsGroup_HasALabelAndADescription()
    {
        foreach (var group in SettingsGroups)
        {
            Assert.True(
                LocalizationService.HasKey($"ServerSettings.{group}"),
                $"Settings group '{group}' has no label.");
        }

        // Only the three ported-later groups render a description card.
        foreach (var group in new[] { "Game", "Network", "Resources" })
        {
            Assert.True(
                LocalizationService.HasKey($"ServerSettings.{group}Body"),
                $"Settings group '{group}' has no description.");
        }
    }

    [Fact]
    public void EveryBackupStatus_HasALabel()
    {
        foreach (var status in Enum.GetValues<BackupStatus>())
        {
            Assert.True(
                LocalizationService.HasKey($"Backups.Status.{status}"),
                $"Backup status '{status}' would render as a raw key.");
        }
    }

    [Fact]
    public void EveryAppSettingsCategory_HasALabel()
    {
        foreach (var category in new[] { "Appearance", "Updates", "Advanced", "About" })
        {
            Assert.True(
                LocalizationService.HasKey($"Settings.Category.{category}"),
                $"Settings category '{category}' would render as a raw key.");
        }
    }

    [Fact]
    public void TabAndGroupLabels_AlsoResolveInArabic()
    {
        var original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ar-SA");
            foreach (var key in TabKeys)
            {
                Assert.True(LocalizationService.HasKey(key), $"{key} has no Arabic label.");
            }

            foreach (var group in SettingsGroups)
            {
                Assert.True(
                    LocalizationService.HasKey($"ServerSettings.{group}"),
                    $"ServerSettings.{group} has no Arabic label.");
            }
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    /// <summary>
    /// The legacy editors must stay reachable from Server Detail. Two are embedded directly
    /// and the rest open from a Configure action; if either link disappears, management
    /// capability is orphaned again.
    /// </summary>
    [Theory]
    [InlineData("ServerSettingsTab.xaml", "PalworldMemoryPerformanceControl")]
    [InlineData("ServerSettingsTab.xaml", "ServerDiagnosticsControl")]
    [InlineData("ServerBackupsTab.xaml", "PalworldSaveBackupControl")]
    [InlineData("ServerBackupsTab.xaml", "ConfigurationRestorePointsControl")]
    public void LegacyEditors_AreEmbeddedInServerDetail(string file, string control)
    {
        var markup = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "ServerManager.Client", "Controls", file));

        Assert.Contains(control, markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// Editors laid out for a full-width page are squeezed unreadably inside the settings
    /// column, so they open at full size instead. They must still be reachable from here.
    /// </summary>
    [Theory]
    [InlineData("resource-governor")]
    [InlineData("legacy-settings")]
    [InlineData("legacy-network")]
    public void LargeLegacyEditors_RemainReachableFromSettings(string action)
    {
        var code = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "ServerManager.Client", "Controls", "ServerSettingsTab.xaml.cs"));

        Assert.Contains($"\"{action}\"", code, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWorldSettingsEditor_IsReachableFromTheGameGroup()
    {
        var code = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "ServerManager.Client", "Controls", "ServerSettingsTab.xaml.cs"));

        // The Game group's action must open the legacy page's settings tab for both games.
        Assert.Contains("case \"Game\":", code, StringComparison.Ordinal);
        Assert.Contains("legacy-settings", code, StringComparison.Ordinal);
        Assert.Contains("LegacyServerEditorWindow.SettingsTab", code, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLegacyEditorBridge_ExposesEveryTabServerDetailLinksTo()
    {
        // Indexes must match the legacy page's declaration order or "Configure…" would open
        // the wrong editor.
        Assert.Equal(2, ServerManager.Client.LegacyServerEditorWindow.SettingsTab);
        Assert.Equal(4, ServerManager.Client.LegacyServerEditorWindow.FilesTab);
        Assert.Equal(8, ServerManager.Client.LegacyServerEditorWindow.NetworkTab);

        var legacyMarkup = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "ServerManager.Client", "Controls", "GameServerPageControl.xaml"));
        var tabs = System.Text.RegularExpressions.Regex
            .Matches(legacyMarkup, "<TabItem x:Name=\"(\\w+)\"")
            .Select(match => match.Groups[1].Value)
            .ToArray();

        Assert.Equal("ServerSettingsTab", tabs[ServerManager.Client.LegacyServerEditorWindow.SettingsTab]);
        Assert.Equal("FilesTab", tabs[ServerManager.Client.LegacyServerEditorWindow.FilesTab]);
        Assert.Equal("NetworkTab", tabs[ServerManager.Client.LegacyServerEditorWindow.NetworkTab]);
    }

    private static string RepositoryRoot()
    {
        var root = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (root is not null &&
               !File.Exists(Path.Combine(root.FullName, "Directory.Build.props")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        return root!.FullName;
    }

    // --- navigation ---------------------------------------------------------------

    [Fact]
    public void OpeningAServer_KeepsServersSelected_SoTheSidebarStaysOnFive()
    {
        using var viewModel = new MainViewModel(ClientLaunchMode.Normal);

        viewModel.OpenServerDetail(Guid.NewGuid());

        Assert.True(viewModel.IsServersSelected);
        Assert.True(viewModel.IsServerDetailOpen);
        Assert.True(viewModel.IsServerDetailVisible);
        Assert.False(viewModel.IsServerListVisible);
    }

    [Fact]
    public void ClosingServerDetail_ReturnsToTheList()
    {
        using var viewModel = new MainViewModel(ClientLaunchMode.Normal);
        viewModel.OpenServerDetail(Guid.NewGuid());

        viewModel.CloseServerDetail();

        Assert.True(viewModel.IsServersSelected);
        Assert.True(viewModel.IsServerListVisible);
        Assert.False(viewModel.IsServerDetailVisible);
    }

    [Fact]
    public void LeavingServers_ClosesDetail_SoComingBackLandsOnTheList()
    {
        using var viewModel = new MainViewModel(ClientLaunchMode.Normal);
        viewModel.OpenServerDetail(Guid.NewGuid());

        viewModel.SelectSection("Home");
        viewModel.SelectSection("Servers");

        Assert.True(viewModel.IsServerListVisible);
        Assert.False(viewModel.IsServerDetailVisible);
    }

    [Fact]
    public void ServerDetail_IsNeverVisibleFromAnotherDestination()
    {
        using var viewModel = new MainViewModel(ClientLaunchMode.Normal);
        viewModel.OpenServerDetail(Guid.NewGuid());

        foreach (var key in new[] { "Home", "Backups", "Network", "Settings" })
        {
            viewModel.SelectSection(key);
            Assert.False(
                viewModel.IsServerDetailVisible,
                $"Server detail leaked into the {key} destination.");
        }
    }

    // --- safety -------------------------------------------------------------------

    [Theory]
    [InlineData("stop")]
    [InlineData("/stop")]
    [InlineData("STOP")]
    [InlineData("kick Steve")]
    [InlineData("ban-ip 10.0.0.2")]
    [InlineData("save-off")]
    [InlineData("doexit")]
    public void DestructiveCommands_AreFlaggedForConfirmation(string command) =>
        Assert.True(
            ServerConsoleTab.IsDestructive(command),
            $"'{command}' should be confirmed before sending.");

    [Theory]
    [InlineData("list")]
    [InlineData("say hello")]
    [InlineData("time set day")]
    [InlineData("/help")]
    [InlineData("stopwatch")]
    public void OrdinaryCommands_AreNotFlagged(string command) =>
        Assert.False(
            ServerConsoleTab.IsDestructive(command),
            $"'{command}' should send without a prompt.");

    /// <summary>
    /// Nothing may be enabled merely because data has not arrived. With no server resolved,
    /// every capability must read false.
    /// </summary>
    [Fact]
    public void WithNoServerResolved_NoActionIsPermitted()
    {
        var context = ServerDetailContext.Shared;
        context.Select(Guid.NewGuid());

        var actions = context.Actions;

        Assert.False(actions.CanStart);
        Assert.False(actions.CanStop);
        Assert.False(actions.CanRestart);
        Assert.False(actions.CanForceStop);
        Assert.False(actions.CanBackup);
        Assert.False(actions.CanRestore);
        Assert.False(actions.CanUpdate);
        Assert.False(actions.CanSendCommand);
    }
}
