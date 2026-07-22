using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ServerManager.Client.Controls;
using ServerManager.Client.Shell;
using ServerManager.Contracts;

namespace ServerManager.Client.Preview;

public partial class PreviewWindow : Window
{
    private static readonly string[] States = ["running", "stopped", "rest-unavailable", "playit-offline"];
    private string _stateName;
    private AppTheme _theme;
    private string _language;
    private bool _compact;

    public PreviewWindow(PreviewOptions options)
    {
        InitializeComponent();
        _stateName = options.State;
        _theme = options.Theme;
        _language = options.Language;
        Width = Math.Min(options.Width, SystemParameters.WorkArea.Width);
        Height = Math.Min(options.Height, SystemParameters.WorkArea.Height);
        MinWidth = 700;
        MinHeight = 720;
        FlowDirection = CultureInfo.GetCultureInfo(options.Language).TextInfo.IsRightToLeft
            ? FlowDirection.RightToLeft
            : FlowDirection.LeftToRight;
        Title = $"1Salem Server Manager — {_stateName}";
        Left = SystemParameters.WorkArea.Left;
        Top = SystemParameters.WorkArea.Top;
        ApplyNavigation(Width);
        Render(_stateName);
        KeyDown += PreviewWindow_KeyDown;
    }

    private async void PreviewWindow_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.F2)
        {
            var index = Array.IndexOf(States, _stateName);
            _stateName = States[(Math.Max(0, index) + 1) % States.Length];
            Title = $"1Salem Server Manager — {_stateName}";
            Render(_stateName);
        }
        else if (e.Key == System.Windows.Input.Key.F3)
        {
            _theme = _theme == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark;
            ThemeService.Apply(_theme);
        }
        else if (e.Key == System.Windows.Input.Key.F4)
        {
            _language = _language.StartsWith("ar", StringComparison.OrdinalIgnoreCase)
                ? "en-US"
                : "ar-SA";
            LocalizationService.Apply(_language);
            FlowDirection = CultureInfo.GetCultureInfo(_language).TextInfo.IsRightToLeft
                ? FlowDirection.RightToLeft
                : FlowDirection.LeftToRight;
            Render(_stateName);
        }
        else if (e.Key == System.Windows.Input.Key.F5)
        {
            _compact = !_compact;
            Width = _compact
                ? Math.Min(700, SystemParameters.WorkArea.Width)
                : SystemParameters.WorkArea.Width;
            Height = SystemParameters.WorkArea.Height;
            Left = SystemParameters.WorkArea.Left;
            Top = SystemParameters.WorkArea.Top;
            ApplyNavigation(Width);
        }
        else if (e.Key == System.Windows.Input.Key.F11)
        {
            await ExportAllScreenshotsAsync();
        }
    }

    private void ApplyNavigation(double width)
    {
        var visible = width >= 1_000;
        NavigationPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        NavigationColumn.Width = visible ? new GridLength(220) : new GridLength(0);
    }

    private void Render(string requestedState)
    {
        Dashboard.ResetHistory();
        var stateName = requestedState.ToLowerInvariant();
        var now = DateTimeOffset.UtcNow;
        var running = stateName != "stopped";
        var restUnavailable = stateName == "rest-unavailable";
        var playitOffline = stateName == "playit-offline";
        var server = CreateServer(now, running, restUnavailable, playitOffline);
        var dashboard = CreateDashboard(server, now);
        var state = PalworldOverviewStateFactory.Create(
            server,
            dashboard,
            CreateMemoryPolicy(running),
            2L * 1024 * 1024 * 1024 * 1024,
            now);

        if (running)
        {
            for (var index = 30; index >= 1; index--)
            {
                var sampleTime = now.AddSeconds(-index * 10);
                var players = 3 + index % 3;
                var cpu = 15d + Math.Sin(index / 3d) * 5;
                var ram = 48d + Math.Cos(index / 4d) * 4;
                Dashboard.SetState(
                    state with
                    {
                        PlayersValue = players,
                        CpuPercentValue = cpu,
                        RamPercentValue = ram,
                        CapturedAtUtc = sampleTime
                    });
            }
        }

        Dashboard.SetState(state);
        Dashboard.SetActivity(CreateActivity(now, running));
        Dashboard.SetOperationStatus(stateName switch
        {
            "stopped" => "Server is stopped. Start Server is available.",
            "rest-unavailable" => "REST metrics are unavailable; process and tunnel monitoring remain active.",
            "playit-offline" => "Playit is offline. The saved public mapping is shown but not reported online.",
            _ => "All live status sources refreshed successfully."
        }, stateName is "rest-unavailable" or "playit-offline");
    }

    internal async Task ExportAllScreenshotsAsync()
    {
        var root = FindRepositoryRoot();
        var output = Path.Combine(
            root,
            "artifacts",
            "validation",
            File.ReadAllText(Path.Combine(root, "VERSION")).Trim(),
            "palworld-overview");
        Directory.CreateDirectory(output);
        var jobs = new[]
        {
            new ExportJob("after-1920x1080-dark-running.png", "running", AppTheme.Dark, "en-US", 1920, 1080),
            new ExportJob("after-1920x1080-light-running.png", "running", AppTheme.Light, "en-US", 1920, 1080),
            new ExportJob("after-minimum-width-dark-running.png", "running", AppTheme.Dark, "en-US", 700, 1080),
            new ExportJob("after-1920x1080-arabic-rtl.png", "running", AppTheme.Dark, "ar-SA", 1920, 1080),
            new ExportJob("after-1920x1080-server-running.png", "running", AppTheme.Dark, "en-US", 1920, 1080),
            new ExportJob("after-1920x1080-server-stopped.png", "stopped", AppTheme.Dark, "en-US", 1920, 1080),
            new ExportJob("after-1920x1080-rest-unavailable.png", "rest-unavailable", AppTheme.Dark, "en-US", 1920, 1080),
            new ExportJob("after-1920x1080-playit-offline.png", "playit-offline", AppTheme.Dark, "en-US", 1920, 1080)
        };

        foreach (var job in jobs)
        {
            _stateName = job.State;
            _theme = job.Theme;
            _language = job.Language;
            ThemeService.Apply(job.Theme);
            LocalizationService.Apply(job.Language);
            WindowState = WindowState.Normal;
            Width = job.Width;
            Height = job.Height;
            PreviewRoot.FlowDirection = CultureInfo.GetCultureInfo(job.Language).TextInfo.IsRightToLeft
                ? FlowDirection.RightToLeft
                : FlowDirection.LeftToRight;
            ApplyNavigation(job.Width);
            Render(job.State);
            PreviewRoot.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.Render);

            var bitmap = new RenderTargetBitmap(
                job.Width,
                job.Height,
                96,
                96,
                PixelFormats.Pbgra32);
            var drawingVisual = new DrawingVisual();
            using (var drawingContext = drawingVisual.RenderOpen())
            {
                drawingContext.DrawRectangle(
                    new VisualBrush(PreviewRoot) { Stretch = Stretch.Fill },
                    null,
                    new Rect(0, 0, job.Width, job.Height));
            }

            bitmap.Render(drawingVisual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            await using var stream = new FileStream(
                Path.Combine(output, job.FileName),
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                131072,
                useAsync: true);
            encoder.Save(stream);
        }

        Width = Math.Min(1920, SystemParameters.WorkArea.Width);
        Height = Math.Min(1080, SystemParameters.WorkArea.Height);
        FlowDirection = FlowDirection.LeftToRight;
        PreviewRoot.FlowDirection = FlowDirection.LeftToRight;
        _stateName = "running";
        _theme = AppTheme.Dark;
        _language = "en-US";
        ThemeService.Apply(_theme);
        LocalizationService.Apply(_language);
        ApplyNavigation(Width);
        Render(_stateName);
        SetOperationStatusAfterExport(output);
    }

    private void SetOperationStatusAfterExport(string output) =>
        Dashboard.SetOperationStatus($"Screenshots exported to {output}");

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    private static ServerDashboardCard CreateServer(
        DateTimeOffset now,
        bool running,
        bool restUnavailable,
        bool playitOffline)
    {
        var serverId = Guid.Parse("21B8B496-206E-44D9-BBB3-D1172B9B90FD");
        PalworldManagementSnapshot? management = restUnavailable
            ? null
            : new PalworldManagementSnapshot(
                serverId,
                running ? PalworldManagementState.Online : PalworldManagementState.Disabled,
                true,
                8212,
                running ? "Connected" : "Server stopped",
                "1Salem Palworld",
                "Community world",
                "0.6.5.0",
                "731CC70D4CD743A4A8CB324D87EE13FA",
                running ? 59.8 : null,
                running ? 16.72 : null,
                running ? 4 : null,
                32,
                running ? 186_420 : null,
                [],
                now,
                IsStale: false);
        return new ServerDashboardCard(
            serverId,
            GameType.Palworld,
            true,
            "1Salem Palworld",
            running ? ServerState.Running : ServerState.Stopped,
            "192.168.3.66:8211",
            "0.6.5.0",
            "Win64 Shipping",
            running && !restUnavailable ? 4 : null,
            32,
            running ? 14620 : null,
            running ? 18.4 : 0,
            running ? 5_402L * 1024 * 1024 : 0,
            running ? 5_116L * 1024 * 1024 : 0,
            running ? 6_008L * 1024 * 1024 : 0,
            running ? TimeSpan.FromDays(2.1576) : null,
            now.AddMinutes(-18),
            "Current",
            8211,
            null,
            null,
            null,
            null,
            "Server ready",
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
            Priority: ProcessPriorityClass.Normal,
            GameProcessId: running ? 14704 : null,
            ChildProcessCount: running ? 2 : 0,
            RootExecutableName: "PalServer",
            GameExecutableName: "PalServer-Win64-Test-Cmd",
            InternetAddress: "click-jackets.gl.at.ply.gg:7551",
            PlayitState: playitOffline ? "Offline" : "Online · Linked · Verified",
            PublicTunnelOnline: running && !playitOffline,
            PublicTunnelVerified: !playitOffline,
            PalworldManagement: management,
            LocalPortOpen: running,
            PlayitOnline: !playitOffline,
            RestManagementConnected: running && !restUnavailable,
            ThreadCount: running ? 104 : 0);
    }

    private static DashboardSnapshot CreateDashboard(
        ServerDashboardCard server,
        DateTimeOffset now) =>
        new(
            new AgentStatusResponse(
                "desktop-agent",
                "DESKTOP-RJVS6U7",
                ProductIdentity.VersionOf(typeof(PreviewWindow).Assembly),
                now.AddDays(-3),
                true,
                "Named pipe",
                "127.0.0.1:5251"),
            "192.168.3.66",
            32L * 1024 * 1024 * 1024,
            14L * 1024 * 1024 * 1024,
            18L * 1024 * 1024 * 1024,
            21.6,
            714L * 1024 * 1024 * 1024,
            188L * 1024 * 1024,
            server.State == ServerState.Running ? 1 : 0,
            0,
            [],
            [server],
            now);

    private static MemoryPerformancePolicySnapshot CreateMemoryPolicy(bool running) =>
        new(
            32L * 1024 * 1024 * 1024,
            14L * 1024 * 1024 * 1024,
            18L * 1024 * 1024 * 1024,
            6L * 1024 * 1024 * 1024,
            188L * 1024 * 1024,
            running ? 5_402L * 1024 * 1024 : 0,
            running ? 5_116L * 1024 * 1024 : 0,
            running ? 6_008L * 1024 * 1024 : 0,
            0,
            11L * 1024 * 1024 * 1024,
            13L * 1024 * 1024 * 1024,
            18L * 1024 * 1024 * 1024,
            null,
            ResourceMode.Balanced,
            ProcessPriorityClass.Normal,
            ProcessPriorityClass.Normal,
            null,
            running ? 14620 : null,
            running ? 14704 : null,
            running ? 4 : 0,
            true,
            true,
            true,
            [],
            [],
            false,
            true,
            false,
            running,
            false,
            running ? 5_402L * 1024 * 1024 : 0,
            "Balanced");

    private static IReadOnlyList<ServerActivityItem> CreateActivity(
        DateTimeOffset now,
        bool running) =>
        running
            ?
            [
                new(now.AddMinutes(-2), "Administrator", "PalworldSaveWorld", "World saved", true, "REST request completed"),
                new(now.AddMinutes(-18), "Scheduler", "BackupCreated", "Backup created", true, "palworld-20260722-1840.zip"),
                new(now.AddHours(-1), "Administrator", "PalworldAnnouncement", "Announcement sent", true, "Message delivered"),
                new(now.AddHours(-2), "Agent", "ProcessStarted", "Server started", true, "Game process verified"),
                new(now.AddDays(-1), "Administrator", "PalworldWorldSettingChanged", "Configuration changed", true, "DropItemMaxNum restored")
            ]
            :
            [
                new(now.AddMinutes(-5), "Administrator", "ProcessStopped", "Server stopped", true, "Graceful shutdown completed"),
                new(now.AddMinutes(-6), "Administrator", "PalworldSaveWorld", "World saved", true, "Pre-stop save completed"),
                new(now.AddHours(-1), "Scheduler", "BackupCreated", "Backup created", true, "Verified ZIP backup")
            ];

    private sealed record ExportJob(
        string FileName,
        string State,
        AppTheme Theme,
        string Language,
        int Width,
        int Height);
}
