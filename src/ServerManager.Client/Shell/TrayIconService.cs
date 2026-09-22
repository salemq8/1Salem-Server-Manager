using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using ServerManager.Contracts;
using ServerManager.Infrastructure.Windows;
using Forms = System.Windows.Forms;

namespace ServerManager.Client.Shell;

public sealed class TrayIconService : IDisposable
{
    private readonly MainWindow _window;
    private readonly Action _exitDashboard;
    private readonly HttpClient _httpClient =
        AgentTransportDefaults.CreateLoopbackHttpClient(TimeSpan.FromSeconds(15));
    private readonly WindowsStartupManager _startupManager = new();
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ContextMenuStrip _menu = new();
    private readonly Forms.ToolStripMenuItem _minecraftMenu;
    private readonly Forms.ToolStripMenuItem _palworldMenu;
    private readonly Forms.ToolStripMenuItem _resourceMenu;
    private readonly Forms.ToolStripMenuItem _agentStatus;
    private readonly Forms.ToolStripMenuItem _startupItem;
    private readonly Forms.ToolStripMenuItem _stopAndExitItem;
    private readonly Forms.Timer _refreshTimer;
    private readonly Icon? _applicationIcon;
    private DashboardSnapshot? _snapshot;
    private bool _refreshing;

    public TrayIconService(MainWindow window, Action exitDashboard)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _exitDashboard = exitDashboard ?? throw new ArgumentNullException(nameof(exitDashboard));

        var open = _menu.Items.Add(TrayMenuLabels.RequiredItems[0]);
        open.Click += (_, _) => _window.Dispatcher.Invoke(_window.ShowDashboard);

        _minecraftMenu = new Forms.ToolStripMenuItem("Minecraft");
        _palworldMenu = new Forms.ToolStripMenuItem("Palworld");
        _menu.Items.Add(_minecraftMenu);
        _menu.Items.Add(_palworldMenu);

        _resourceMenu = new Forms.ToolStripMenuItem("Resource Mode");
        AddResourceMode("Balanced", "balanced");
        AddResourceMode("Minecraft Priority", "minecraft");
        AddResourceMode("Palworld Priority", "palworld");
        // "Custom" opens the PC-wide resource policy editor itself, as Build 5's Resources
        // section did. Landing on the server list promised an editor and showed none.
        var custom = _resourceMenu.DropDownItems.Add("Custom");
        custom.Click += (_, _) => _window.Dispatcher.Invoke(_window.OpenResourcePolicy);
        _menu.Items.Add(_resourceMenu);

        _agentStatus = new Forms.ToolStripMenuItem("Agent status: checking")
        {
            Enabled = false
        };
        _menu.Items.Add(_agentStatus);

        _startupItem = new Forms.ToolStripMenuItem("Start Dashboard with Windows")
        {
            CheckOnClick = false
        };
        _startupItem.Click += StartupItem_Click;
        _menu.Items.Add(_startupItem);

        _menu.Items.Add(new Forms.ToolStripSeparator());
        _stopAndExitItem = new Forms.ToolStripMenuItem("Stop servers and exit");
        _stopAndExitItem.Click += StopAndExit_Click;
        _menu.Items.Add(_stopAndExitItem);

        var exit = _menu.Items.Add("Exit Dashboard");
        exit.Click += (_, _) => _window.Dispatcher.Invoke(_exitDashboard);

        _applicationIcon = LoadApplicationIcon();
        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _applicationIcon ?? SystemIcons.Application,
            Text = "1Salem Server Manager",
            ContextMenuStrip = _menu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) =>
            _window.Dispatcher.Invoke(_window.ShowDashboard);

        _refreshTimer = new Forms.Timer { Interval = 2_000 };
        _refreshTimer.Tick += async (_, _) => await RefreshAsync();
        _menu.Opening += async (_, _) => await RefreshAsync();
        _refreshTimer.Start();
        _ = RefreshAsync();
    }

    public void Dispose()
    {
        _refreshTimer.Stop();
        _refreshTimer.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _applicationIcon?.Dispose();
        _httpClient.Dispose();
    }

    private async Task RefreshAsync()
    {
        if (_refreshing)
        {
            return;
        }

        _refreshing = true;
        try
        {
            _snapshot = await _httpClient.GetFromJsonAsync<DashboardSnapshot>(
                "/api/v1/dashboard");
            _agentStatus.Text =
                $"Agent status: Online · {_snapshot?.Agent.MachineName ?? "local PC"}";
            RebuildServerMenu(_minecraftMenu, GameType.Minecraft);
            RebuildServerMenu(_palworldMenu, GameType.Palworld);
            _resourceMenu.Text =
                $"Resource Mode · {_snapshot?.ResourceProfileSummary ?? "unavailable"}";
            _resourceMenu.Enabled = true;
            _stopAndExitItem.Enabled = _snapshot?.Servers.Any(
                server => server.Actions.CanStop) == true;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException)
        {
            _agentStatus.Text = "Agent status: Offline";
            _minecraftMenu.Enabled = false;
            _palworldMenu.Enabled = false;
            _resourceMenu.Enabled = false;
            _stopAndExitItem.Enabled = false;
        }

        try
        {
            _startupItem.Checked = _startupManager.IsEnabled();
            _startupItem.Enabled = IsDashboardExecutable(Environment.ProcessPath);
        }
        catch (Exception)
        {
            _startupItem.Enabled = false;
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void RebuildServerMenu(
        Forms.ToolStripMenuItem menu,
        GameType game)
    {
        menu.DropDownItems.Clear();
        menu.Enabled = true;
        var server = _snapshot?.Servers.FirstOrDefault(item => item.Game == game);
        if (server is null)
        {
            var create = menu.DropDownItems.Add($"Create {game} Server");
            create.Click += (_, _) => _window.Dispatcher.Invoke(
                () => _window.OpenServerCreation(game));
            return;
        }

        menu.Text = $"{game} · {server.State}";
        AddServerAction(menu, "Start", server.Actions.CanStart, server, "start");
        AddServerAction(
            menu,
            "Graceful Stop",
            server.Actions.CanStop,
            server,
            "stop",
            new ServerStopRequest());
        AddServerAction(menu, "Restart", server.Actions.CanRestart, server, "restart");
        AddServerAction(menu, "Backup", server.Actions.CanBackup, server, "backups");
        menu.DropDownItems.Add(new Forms.ToolStripSeparator());

        var console = menu.DropDownItems.Add("Open Console");
        console.Enabled = server.IsInstalled;
        console.Click += (_, _) => _window.Dispatcher.Invoke(
            () => _window.ShowServer(game, 1));

        var settings = menu.DropDownItems.Add("Settings");
        settings.Enabled = server.IsInstalled;
        settings.Click += (_, _) => _window.Dispatcher.Invoke(
            () => _window.ShowServer(game, 2));

        var copyLocal = menu.DropDownItems.Add(
            $"Copy Local Address · {server.LocalAddress ?? $"port {server.Port}"}");
        copyLocal.Enabled = !string.IsNullOrWhiteSpace(server.LocalAddress);
        copyLocal.Click += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(server.LocalAddress))
            {
                SafeClipboard.TrySetText(server.LocalAddress);
            }
        };

        var copyInternet = menu.DropDownItems.Add(
            $"Copy Internet Address · {server.InternetAddress ?? "not configured"}");
        copyInternet.Enabled = !string.IsNullOrWhiteSpace(server.InternetAddress);
        copyInternet.Click += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(server.InternetAddress))
            {
                SafeClipboard.TrySetText(server.InternetAddress);
            }
        };

        menu.DropDownItems.Add(new Forms.ToolStripMenuItem(
            $"Playit: {server.PlayitState ?? "unavailable"} · " +
            (server.PublicTunnelOnline
                ? "verified online"
                : "public reachability not verified"))
        {
            Enabled = false
        });
    }

    private void AddServerAction(
        Forms.ToolStripMenuItem menu,
        string label,
        bool enabled,
        ServerDashboardCard server,
        string action,
        object? body = null)
    {
        var item = menu.DropDownItems.Add(label);
        item.Enabled = enabled;
        item.Click += async (_, _) =>
        {
            using var response = body is null
                ? await _httpClient.PostAsync(
                    $"/api/v1/servers/{server.ServerId}/{action}",
                    null)
                : await _httpClient.PostAsJsonAsync(
                    $"/api/v1/servers/{server.ServerId}/{action}",
                    body);
            if (!response.IsSuccessStatusCode)
            {
                _notifyIcon.ShowBalloonTip(
                    5_000,
                    $"{server.Name}: {label} failed",
                    await response.Content.ReadAsStringAsync(),
                    Forms.ToolTipIcon.Error);
            }

            await RefreshAsync();
        };
    }

    private void AddResourceMode(string label, string mode)
    {
        var item = _resourceMenu.DropDownItems.Add(label);
        item.Click += async (_, _) =>
        {
            var path = mode == "balanced"
                ? "/api/v1/resources/profile"
                : $"/api/v1/resources/prioritize/{mode}";
            using var response = mode == "balanced"
                ? await _httpClient.PostAsJsonAsync(
                    path,
                    new ResourceProfileRequest(ResourceMode.Balanced))
                : await _httpClient.PostAsync(path, null);
            if (!response.IsSuccessStatusCode)
            {
                _notifyIcon.ShowBalloonTip(
                    5_000,
                    "Resource mode failed",
                    await response.Content.ReadAsStringAsync(),
                    Forms.ToolTipIcon.Error);
            }
        };
    }

    private void StartupItem_Click(object? sender, EventArgs e)
    {
        var executable = Environment.ProcessPath;
        if (!IsDashboardExecutable(executable))
        {
            return;
        }

        var result = _startupManager.SetEnabled(
            executable!,
            !_startupManager.IsEnabled());
        if (!result.Success)
        {
            _notifyIcon.ShowBalloonTip(
                5_000,
                "Startup setting failed",
                result.Message ?? "Windows did not accept the startup setting.",
                Forms.ToolTipIcon.Error);
        }

        _startupItem.Checked = _startupManager.IsEnabled();
    }

    private async void StopAndExit_Click(object? sender, EventArgs e)
    {
        if (Forms.MessageBox.Show(
                "Gracefully stop every running server, then exit the dashboard?",
                "1Salem Server Manager",
                Forms.MessageBoxButtons.YesNo,
                Forms.MessageBoxIcon.Warning) != Forms.DialogResult.Yes)
        {
            return;
        }

        _stopAndExitItem.Enabled = false;
        foreach (var server in _snapshot?.Servers.Where(
                     item => item.Actions.CanStop).ToArray() ?? [])
        {
            using var response = await _httpClient.PostAsJsonAsync(
                $"/api/v1/servers/{server.ServerId}/stop",
                new ServerStopRequest());
            if (!response.IsSuccessStatusCode)
            {
                _notifyIcon.ShowBalloonTip(
                    5_000,
                    $"{server.Name} did not stop",
                    await response.Content.ReadAsStringAsync(),
                    Forms.ToolTipIcon.Error);
                _stopAndExitItem.Enabled = true;
                return;
            }
        }

        _window.Dispatcher.Invoke(_exitDashboard);
    }

    /// <summary>
    /// Only the real dashboard executable may register itself to start with Windows; a
    /// development run hosted by dotnet.exe must never write that registry value.
    /// </summary>
    internal static bool IsDashboardExecutable(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        File.Exists(path) &&
        Path.GetFileName(path).StartsWith(
            "1Salem.ServerManager",
            StringComparison.OrdinalIgnoreCase);

    private static Icon? LoadApplicationIcon()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath) || !File.Exists(processPath))
        {
            return null;
        }

        using var extracted = Icon.ExtractAssociatedIcon(processPath);
        return extracted is null ? null : (Icon)extracted.Clone();
    }
}
