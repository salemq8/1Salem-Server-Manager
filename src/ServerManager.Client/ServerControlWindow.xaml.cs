using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using System.Windows.Threading;
using ServerManager.Contracts;
using ServerManager.Core;

namespace ServerManager.Client;

public partial class ServerControlWindow : Window
{
    private const long Mebibyte = 1024L * 1024;
    private readonly GameType _game;
    private readonly HttpClient _httpClient =
        AgentTransportDefaults.CreateLoopbackHttpClient(TimeSpan.FromMinutes(20));
    private readonly DispatcherTimer _timer = new()
    {
        Interval = TimeSpan.FromSeconds(2)
    };

    public ServerControlWindow(GameType game)
    {
        _game = game;
        InitializeComponent();
        HeadingText.Text = $"{game} Server Control";
        Loaded += OnLoaded;
        Closed += OnClosed;
        _timer.Tick += async (_, _) => await RefreshAsync(false);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await LoadServersAsync();
        _timer.Start();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _timer.Stop();
        _httpClient.Dispose();
    }

    private async void ServerBox_SelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e) =>
        await RefreshAsync(true);

    private async void Start_Click(object sender, RoutedEventArgs e) =>
        await PostServerActionAsync("start");

    private async void Stop_Click(object sender, RoutedEventArgs e) =>
        await PostStopAsync(false);

    private async void Restart_Click(object sender, RoutedEventArgs e) =>
        await PostServerActionAsync("restart");

    private async void ForceStop_Click(object sender, RoutedEventArgs e)
    {
        if (System.Windows.MessageBox.Show(
                "Force stop only after graceful stop has failed. Continue?",
                "Confirm force stop",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) == MessageBoxResult.Yes)
        {
            await PostStopAsync(true);
        }
    }

    private async void Backup_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedServer is { } server)
        {
            await PostAsync($"/api/v1/servers/{server.Id}/backups", null);
        }
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedServer is not { } server)
        {
            return;
        }

        var update = await _httpClient.GetFromJsonAsync<UpdateCheckResult>(
            $"/api/v1/servers/{server.Id}/updates");
        OperationText.Text = update is null
            ? "Update status unavailable."
            : $"Installed: {update.InstalledVersion ?? "unknown"} | Latest: " +
              $"{update.LatestVersion ?? "unknown"} | " +
              (update.IsUpdateAvailable ? "Update available" : "Up to date");
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedServer is { } server &&
            System.Windows.MessageBox.Show(
                "The server will stop, create a safety backup, update, restart, and roll back when supported if verification fails. Continue?",
                "Confirm safe update",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) == MessageBoxResult.Yes)
        {
            await PostAsync($"/api/v1/servers/{server.Id}/updates", null);
        }
    }

    private async void Prioritize_Click(object sender, RoutedEventArgs e) =>
        await PostAsync($"/api/v1/resources/prioritize/{_game}", null);

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedServer is not { } server ||
            !Directory.Exists(server.RootPath))
        {
            OperationText.Text = "The registered server folder does not exist.";
            return;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "explorer.exe",
            UseShellExecute = true
        };
        startInfo.ArgumentList.Add(server.RootPath);
        Process.Start(startInfo);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) =>
        await RefreshAsync(true);

    private async void SendCommand_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedServer is not { } server || string.IsNullOrWhiteSpace(CommandBox.Text))
        {
            return;
        }

        await PostAsync(
            $"/api/v1/servers/{server.Id}/console",
            new ConsoleCommandRequest(CommandBox.Text.Trim()));
        CommandBox.Clear();
    }

    private GameServerDefinition? SelectedServer =>
        ServerBox.SelectedItem as GameServerDefinition;

    private async Task LoadServersAsync()
    {
        try
        {
            var servers = await _httpClient.GetFromJsonAsync<GameServerDefinition[]>(
                "/api/v1/servers") ?? [];
            ServerBox.ItemsSource = servers.Where(server => server.Game == _game).ToArray();
            ServerBox.SelectedIndex = ServerBox.Items.Count > 0 ? 0 : -1;
            if (ServerBox.Items.Count == 0)
            {
                StatusText.Text = $"No {_game} server is registered. Use Install first.";
            }
        }
        catch (HttpRequestException exception)
        {
            OperationText.Text = $"Agent unavailable: {exception.Message}";
        }
    }

    private async Task PostServerActionAsync(string action)
    {
        if (SelectedServer is { } server)
        {
            await PostAsync($"/api/v1/servers/{server.Id}/{action}", null);
        }
    }

    private async Task PostStopAsync(bool force)
    {
        if (SelectedServer is { } server)
        {
            await PostAsync(
                $"/api/v1/servers/{server.Id}/stop",
                new ServerStopRequest(force));
        }
    }

    private async Task PostAsync(string path, object? body)
    {
        try
        {
            using var response = body is null
                ? await _httpClient.PostAsync(path, null)
                : await _httpClient.PostAsJsonAsync(path, body);
            OperationText.Text = response.IsSuccessStatusCode
                ? "Operation completed."
                : $"Operation failed: {await response.Content.ReadAsStringAsync()}";
            await RefreshAsync(true);
        }
        catch (HttpRequestException exception)
        {
            OperationText.Text = $"Operation failed: {exception.Message}";
        }
    }

    private async Task RefreshAsync(bool showErrors)
    {
        if (SelectedServer is not { } server)
        {
            return;
        }

        try
        {
            var processes = await _httpClient.GetFromJsonAsync<ProcessSnapshot[]>(
                "/api/v1/processes") ?? [];
            var process = processes.FirstOrDefault(item => item.ServerId == server.Id);
            StatusText.Text = process?.State.ToString() ?? "Stopped";
            PidText.Text = $"PID: {(process?.ProcessId > 0 ? process.ProcessId : "-")}";
            CpuText.Text = $"CPU: {process?.CpuPercent ?? 0:F1}%";
            MemoryText.Text = $"RAM: {(process?.WorkingSetBytes ?? 0) / (double)Mebibyte:F0} MiB";
            VersionText.Text = $"Version: {server.InstalledVersion ?? "unknown"}";

            var logs = await _httpClient.GetFromJsonAsync<LogEntry[]>(
                $"/api/v1/servers/{server.Id}/logs") ?? [];
            LogList.ItemsSource = logs.Select(entry => new LogDisplay(
                $"{entry.TimestampUtc.ToLocalTime():T} [{entry.Level}] {entry.Message}"))
                .ToArray();
            if (LogList.Items.Count > 0)
            {
                LogList.ScrollIntoView(LogList.Items[^1]);
            }
        }
        catch (HttpRequestException exception) when (!showErrors)
        {
            _ = exception;
        }
        catch (HttpRequestException exception)
        {
            OperationText.Text = $"Refresh failed: {exception.Message}";
        }
    }

    private sealed record LogDisplay(string Display);
}
