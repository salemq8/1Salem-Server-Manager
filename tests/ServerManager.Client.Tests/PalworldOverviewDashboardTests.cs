using ServerManager.Client.Controls;
using ServerManager.Contracts;

namespace ServerManager.Client.Tests;

public sealed class PalworldOverviewDashboardTests
{
    [Fact]
    public void Dashboard_UsesSixEqualMetricCardsAndStructuredPanels()
    {
        var xaml = ReadSource("src", "ServerManager.Client", "Controls", "PalworldOverviewDashboardControl.xaml");

        Assert.Contains("x:Name=\"MetricsGrid\" Columns=\"6\"", xaml, StringComparison.Ordinal);
        Assert.Equal(6, Count(xaml, "Style=\"{StaticResource MetricCardStyle}\""));
        Assert.Contains("Palworld.Overview.ServerActivity", xaml, StringComparison.Ordinal);
        Assert.Contains("Palworld.Overview.RecentActivity", xaml, StringComparison.Ordinal);
        Assert.Contains("Palworld.Overview.ResourceSummary", xaml, StringComparison.Ordinal);
        Assert.Contains("Palworld.Overview.Connections", xaml, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1920, 6)]
    [InlineData(1000, 6)]
    [InlineData(800, 3)]
    [InlineData(620, 2)]
    public void Dashboard_CardColumnsRespondWithoutCompressingLabels(double width, int columns)
    {
        Assert.Equal(columns, PalworldOverviewStateFactory.GetMetricColumns(width));
        Assert.Equal(width < 1060, PalworldOverviewStateFactory.UseStackedContent(width));
        Assert.True(PalworldOverviewStateFactory.MinimumSupportedContentWidth >= 620);

        var xaml = ReadSource("src", "ServerManager.Client", "Controls", "PalworldOverviewDashboardControl.xaml");
        Assert.Contains("MinWidth=\"620\"", xaml, StringComparison.Ordinal);
        Assert.Contains("TextWrapping\" Value=\"Wrap\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("CharacterEllipsis", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Dashboard_ShowsAllSixStatusBadges()
    {
        var xaml = ReadSource("src", "ServerManager.Client", "Controls", "PalworldOverviewDashboardControl.xaml");
        foreach (var name in new[]
                 {
                     "ProcessBadge", "PortBadge", "GameReadyBadge", "RestBadge",
                     "PlayitBadge", "PublicMappingBadge"
                 })
        {
            Assert.Contains($"x:Name=\"{name}\"", xaml, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void QuickActions_ReflectRunningAndStoppedServerStates()
    {
        var now = DateTimeOffset.UtcNow;
        var running = PalworldOverviewStateFactory.Create(
            CreateServer(ServerState.Running, now),
            CreateDashboard(now),
            null,
            2L * 1024 * 1024 * 1024 * 1024,
            now);
        var stopped = PalworldOverviewStateFactory.Create(
            CreateServer(ServerState.Stopped, now),
            CreateDashboard(now),
            null,
            2L * 1024 * 1024 * 1024 * 1024,
            now);

        Assert.True(running.Actions.CanSaveWorld);
        Assert.True(running.Actions.CanAnnounce);
        Assert.True(running.Actions.CanGracefullyStop);
        Assert.True(running.Actions.CanRestart);
        Assert.False(running.Actions.CanStart);
        Assert.True(stopped.Actions.CanStart);
        Assert.False(stopped.Actions.CanSaveWorld);
        Assert.False(stopped.Actions.CanAnnounce);
        Assert.False(stopped.Actions.CanGracefullyStop);
    }

    [Fact]
    public void RightToLeft_ReversesQuickActionReadingOrder()
    {
        var leftToRight = PalworldOverviewStateFactory.GetQuickActionOrder(false);
        var rightToLeft = PalworldOverviewStateFactory.GetQuickActionOrder(true);

        Assert.Equal(leftToRight.Reverse(), rightToLeft);
        Assert.Equal("Refresh", leftToRight[0]);
        Assert.Equal("CopyInternetAddress", rightToLeft[0]);
    }

    [Fact]
    public void Dashboard_UsesThemeBrushesWithReadableDarkAndLightContrast()
    {
        var xaml = ReadSource("src", "ServerManager.Client", "Controls", "PalworldOverviewDashboardControl.xaml");
        Assert.Contains("DynamicResource MutedTextBrush", xaml, StringComparison.Ordinal);
        Assert.Contains("DynamicResource PanelBrush", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Foreground=\"#", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Background=\"#", xaml, StringComparison.Ordinal);
        Assert.True(ContrastRatio("#F1F6FA", "#08131F") >= 7);
        Assert.True(ContrastRatio("#16202D", "#F4F6F9") >= 7);
    }

    [Fact]
    public void StaleSnapshot_MarksEveryLiveStatusAndMetricAsStale()
    {
        var now = DateTimeOffset.UtcNow;
        var old = now - TimeSpan.FromSeconds(30);
        var state = PalworldOverviewStateFactory.Create(
            CreateServer(ServerState.Running, old),
            CreateDashboard(old),
            null,
            1L * 1024 * 1024 * 1024 * 1024,
            now);

        Assert.True(state.IsStale);
        Assert.Equal(DashboardMetricAvailability.Stale, state.Process.Availability);
        Assert.Equal(DashboardMetricAvailability.Stale, state.Players.Availability);
        Assert.Equal(DashboardMetricAvailability.Stale, state.Cpu.Availability);
    }

    [Fact]
    public void RecentActivity_IsSortedAndBounded()
    {
        var feed = new BoundedServerActivityFeed();
        var now = DateTimeOffset.UtcNow;
        feed.Replace(Enumerable.Range(0, 75).Select(index => new ServerActivityItem(
            now.AddMinutes(-index),
            "Agent",
            "BackupCreated",
            $"Backup {index}",
            true)));

        Assert.Equal(BoundedServerActivityFeed.MaximumItems, feed.Items.Count);
        Assert.Equal("Backup 0", feed.Items[0].DisplayName);
        Assert.Equal("Backup 39", feed.Items[^1].DisplayName);
    }

    [Fact]
    public void GraphHistory_UsesBoundedStorageAndSelectableTimeRange()
    {
        var history = new BoundedPalworldMetricHistory();
        var now = DateTimeOffset.UtcNow;
        for (var index = 0; index < BoundedPalworldMetricHistory.MaximumSamples + 50; index++)
        {
            history.Add(new PalworldMetricSample(
                now.AddSeconds(index - BoundedPalworldMetricHistory.MaximumSamples),
                index % 12,
                index % 100,
                index % 80));
        }

        Assert.Equal(BoundedPalworldMetricHistory.MaximumSamples, history.Count);
        Assert.All(
            history.GetRange(TimeSpan.FromMinutes(5), now),
            sample => Assert.True(sample.TimestampUtc >= now - TimeSpan.FromMinutes(5)));
    }

    private static ServerDashboardCard CreateServer(ServerState state, DateTimeOffset capturedAt)
    {
        var running = state == ServerState.Running;
        var serverId = Guid.NewGuid();
        var management = new PalworldManagementSnapshot(
            serverId,
            running ? PalworldManagementState.Online : PalworldManagementState.Disabled,
            running,
            8212,
            running ? "Connected" : "Stopped",
            "1Salem Palworld",
            "Test server",
            "0.6.5.0",
            "world-guid",
            running ? 59.8 : null,
            running ? 16.72 : null,
            running ? 4 : null,
            32,
            running ? 3_600 : null,
            [],
            capturedAt);
        return new ServerDashboardCard(
            serverId,
            GameType.Palworld,
            true,
            "1Salem Palworld",
            state,
            "192.168.3.66:8211",
            "0.6.5.0",
            "Win64",
            running ? 4 : null,
            32,
            running ? 1001 : null,
            running ? 18.4 : 0,
            running ? 5L * 1024 * 1024 * 1024 : 0,
            running ? 4L * 1024 * 1024 * 1024 : 0,
            running ? 6L * 1024 * 1024 * 1024 : 0,
            running ? TimeSpan.FromHours(1) : null,
            capturedAt.AddHours(-1),
            "Current",
            8211,
            null,
            null,
            null,
            null,
            null,
            null,
            new ServerActionAvailability(
                !running,
                running,
                running,
                running,
                true,
                true,
                true,
                false),
            InternetAddress: "click-jackets.gl.at.ply.gg:7551",
            PlayitState: "Online",
            PublicTunnelOnline: running,
            PublicTunnelVerified: true,
            PalworldManagement: management,
            LocalPortOpen: running,
            PlayitOnline: true,
            RestManagementConnected: running);
    }

    private static DashboardSnapshot CreateDashboard(DateTimeOffset capturedAt) =>
        new(
            new AgentStatusResponse(
                "agent",
                "DESKTOP",
                "1.3.0",
                capturedAt.AddHours(-2),
                true,
                "Named pipe",
                "127.0.0.1"),
            "192.168.3.66",
            32L * 1024 * 1024 * 1024,
            12L * 1024 * 1024 * 1024,
            20L * 1024 * 1024 * 1024,
            12.4,
            500L * 1024 * 1024 * 1024,
            200L * 1024 * 1024,
            1,
            0,
            [],
            [],
            capturedAt);

    private static int Count(string value, string token) =>
        value.Split(token, StringSplitOptions.None).Length - 1;

    private static double ContrastRatio(string first, string second)
    {
        static double Luminance(string hex)
        {
            var components = new[] { hex[1..3], hex[3..5], hex[5..7] }
                .Select(part => Convert.ToInt32(part, 16) / 255d)
                .Select(value => value <= 0.03928
                    ? value / 12.92
                    : Math.Pow((value + 0.055) / 1.055, 2.4))
                .ToArray();
            return 0.2126 * components[0] + 0.7152 * components[1] + 0.0722 * components[2];
        }

        var a = Luminance(first);
        var b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
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
