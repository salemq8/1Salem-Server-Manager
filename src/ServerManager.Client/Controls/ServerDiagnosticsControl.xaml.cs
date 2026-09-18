using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using ServerManager.Contracts;
using ServerManager.Core;
using ServerManager.Infrastructure.Games.Minecraft;
using ServerManager.Infrastructure.Games.Palworld;
using Clipboard = System.Windows.Clipboard;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace ServerManager.Client.Controls;

public partial class ServerDiagnosticsControl :
    System.Windows.Controls.UserControl,
    IDisposable
{
    private readonly HttpClient _httpClient =
        AgentTransportDefaults.CreateLoopbackHttpClient(TimeSpan.FromSeconds(20));
    private Guid? _serverId;
    private IReadOnlyList<DiagnosticRow> _checks = [];
    private bool _running;

    public ServerDiagnosticsControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public event EventHandler? OpenLogsRequested;

    public async void SetServer(Guid? serverId)
    {
        var changed = _serverId != serverId;
        _serverId = serverId;
        if (serverId is null)
        {
            _checks = [];
            ChecksGrid.ItemsSource = null;
            return;
        }

        if (changed && IsLoaded)
        {
            await RunAsync();
        }
    }

    public void Dispose()
    {
        Loaded -= OnLoaded;
        _httpClient.Dispose();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_serverId is not null && _checks.Count == 0)
        {
            await RunAsync();
        }
    }

    private async void Run_Click(object sender, RoutedEventArgs e) =>
        await RunAsync();

    private async Task RunAsync()
    {
        if (_serverId is null || _running)
        {
            return;
        }

        _running = true;
        StatusText.Text = "Running independent process, configuration, storage, and integration checks...";
        try
        {
            var dashboardTask = _httpClient.GetFromJsonAsync<DashboardSnapshot>(
                "/api/v1/dashboard");
            var serversTask = _httpClient.GetFromJsonAsync<GameServerDefinition[]>(
                "/api/v1/servers");
            var historyTask =
                _httpClient.GetFromJsonAsync<ConfigurationRestorePointItem[]>(
                    $"/api/v1/servers/{_serverId}/configuration-restore-points");
            await Task.WhenAll(dashboardTask, serversTask, historyTask);
            var dashboard = await dashboardTask ??
                throw new InvalidDataException("The Agent returned no dashboard.");
            var definition = (await serversTask ?? [])
                .First(server => server.Id == _serverId);
            var card = dashboard.Servers.First(server =>
                server.ServerId == _serverId);
            var history = await historyTask ?? [];
            _checks = BuildChecks(dashboard, definition, card, history);
            ChecksGrid.ItemsSource = _checks;
            var failed = _checks.Count(check => check.Status == "Failed");
            var warnings = _checks.Count(check => check.Status == "Warning");
            StatusText.Text =
                $"{_checks.Count} checks completed · {failed} failed · {warnings} warning(s) · {DateTime.Now:t}";
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException or
            JsonException or InvalidDataException or InvalidOperationException)
        {
            StatusText.Text = $"Diagnostics failed: {exception.Message}";
        }
        finally
        {
            _running = false;
        }
    }

    private static IReadOnlyList<DiagnosticRow> BuildChecks(
        DashboardSnapshot dashboard,
        GameServerDefinition server,
        ServerDashboardCard card,
        IReadOnlyList<ConfigurationRestorePointItem> history)
    {
        var checks = new List<DiagnosticRow>();
        var executable = server.Game == GameType.Minecraft
            ? Path.Combine(server.RootPath, "server.jar")
            : Path.Combine(server.RootPath, "PalServer.exe");
        checks.Add(Check(
            "Executable",
            File.Exists(executable),
            File.Exists(executable)
                ? $"Found {Path.GetFileName(executable)}."
                : $"Missing {Path.GetFileName(executable)}.",
            "Repair or import the registered server installation."));
        var rootExists = Directory.Exists(server.RootPath);
        var rootReadOnly = rootExists &&
            (File.GetAttributes(server.RootPath) & FileAttributes.ReadOnly) != 0;
        checks.Add(Check(
            "Server root",
            rootExists && !rootReadOnly,
            !rootExists
                ? "Registered root does not exist."
                : rootReadOnly
                    ? "Registered root is marked read-only."
                    : "Registered root exists and is not marked read-only.",
            "Correct the registered root or its write permissions."));

        var (configurationValid, configurationDetail) =
            ValidateConfiguration(server);
        checks.Add(Check(
            "Configuration",
            configurationValid,
            configurationDetail,
            "Open Settings, correct the reported value, or restore the latest known-working point."));
        checks.Add(Check(
            "Process",
            card.State == ServerState.Running,
            card.State == ServerState.Running
                ? $"Running · root PID {card.ProcessId} · game PID {card.GameProcessId}."
                : $"{card.State}{FormatError(card.LastError)}",
            "Start the server or inspect Console for the exact startup failure."));
        checks.Add(Check(
            "Local port",
            card.LocalPortOpen,
            card.LocalPortOpen
                ? $"{card.LocalAddress ?? $"Port {card.Port}"} is open."
                : $"{card.LocalAddress ?? $"Port {card.Port}"} is not confirmed open.",
            "Confirm the game process is ready and the configured port matches."));
        checks.Add(Check(
            "Game ready",
            card.State == ServerState.Running && card.LocalPortOpen,
            card.State == ServerState.Running && card.LocalPortOpen
                ? "Process and local port checks agree."
                : "The process and readiness checks do not both pass.",
            "Wait for startup to finish, then retry failed checks."));
        checks.Add(Optional(
            "Playit",
            string.IsNullOrWhiteSpace(card.InternetAddress)
                ? "No Internet mapping is configured."
                : card.PlayitOnline && card.PublicTunnelVerified
                    ? $"Online, linked, and verified · {card.InternetAddress}."
                    : $"Saved mapping {card.InternetAddress}; current state {card.PlayitState ?? "unavailable"}.",
            string.IsNullOrWhiteSpace(card.InternetAddress) ||
            (card.PlayitOnline && card.PublicTunnelVerified),
            "Open Remote Access and retry the existing Playit agent. Local hosting remains available."));
        checks.Add(Optional(
            "Management API",
            server.Game == GameType.Minecraft
                ? "Not required for Minecraft."
                : card.RestManagementConnected
                    ? "Palworld localhost management is connected."
                    : "Palworld localhost management is unavailable.",
            server.Game == GameType.Minecraft || card.RestManagementConnected,
            "Repair Local Palworld Management from Overview. Do not expose it publicly."));
        checks.Add(Check(
            "Disk space",
            dashboard.SystemDriveFreeBytes >= 5L * 1024 * 1024 * 1024,
            $"{FormatBytes(dashboard.SystemDriveFreeBytes)} free on the system drive.",
            "Free at least 5 GB before updates, backups, or restores."));
        checks.Add(Check(
            "Available RAM",
            dashboard.AvailableMemoryBytes >= 2L * 1024 * 1024 * 1024,
            $"{FormatBytes(dashboard.AvailableMemoryBytes)} available.",
            "Close other workloads or reduce managed memory limits."));
        checks.Add(new DiagnosticRow(
            "Last crash",
            string.IsNullOrWhiteSpace(card.LastError) ? "Passed" : "Warning",
            string.IsNullOrWhiteSpace(card.LastError)
                ? "No active server error is recorded."
                : DiagnosticsService.Redact(card.LastError),
            "Open Console and the exported diagnostic report for details."));
        checks.Add(new DiagnosticRow(
            "Last update",
            string.IsNullOrWhiteSpace(card.UpdateStatus) ? "Warning" : "Passed",
            card.UpdateStatus ?? "Update state has not been checked.",
            "Open Updates and run Check Updates."));
        checks.Add(new DiagnosticRow(
            "Last backup",
            card.LastBackupAtUtc is null ? "Warning" : "Passed",
            card.LastBackupAtUtc?.ToLocalTime().ToString("g") ??
                "No completed backup is recorded.",
            "Create and verify a backup before risky maintenance."));
        checks.Add(new DiagnosticRow(
            "Configuration recovery",
            history.Count == 0 ? "Warning" : "Passed",
            history.Count == 0
                ? "No settings restore point exists yet."
                : $"{history.Count} protected restore point(s); {history.Count(item => item.KnownWorking)} known working.",
            "A restore point is created automatically before the next settings change."));
        return checks;
    }

    private static (bool Valid, string Detail) ValidateConfiguration(
        GameServerDefinition server)
    {
        try
        {
            if (server.Game == GameType.Minecraft)
            {
                var path = Path.Combine(server.RootPath, "server.properties");
                if (!File.Exists(path))
                {
                    return (false, "server.properties is missing.");
                }

                _ = MinecraftPropertiesSerializer.Parse(File.ReadAllText(path));
                return (true, "server.properties parsed successfully.");
            }

            var palworldPath =
                PalworldConfigurationFile.ResolvePath(server.RootPath);
            if (!File.Exists(palworldPath))
            {
                return (false, "PalWorldSettings.ini is missing.");
            }

            _ = PalworldSettingsSerializer.ParseValues(
                File.ReadAllText(palworldPath));
            return (true, "PalWorldSettings.ini parsed successfully.");
        }
        catch (Exception exception) when (
            exception is IOException or ArgumentException or
            InvalidDataException)
        {
            return (false, DiagnosticsService.Redact(exception.Message));
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(BuildReport());
        StatusText.Text = "Redacted diagnostic report copied.";
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save Diagnostic Report",
            Filter = "ZIP archive (*.zip)|*.zip",
            FileName = $"1Salem-Diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
            AddExtension = true,
            DefaultExt = ".zip",
            OverwritePrompt = true
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        StatusText.Text = "Collecting and redacting diagnostics...";
        try
        {
            var path = await DiagnosticsService.ExportAsync(dialog.FileName);
            StatusText.Text = $"Diagnostic report saved: {path}";
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
            ArgumentException)
        {
            StatusText.Text = $"Diagnostic export failed: {exception.Message}";
        }
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e) =>
        OpenLogsRequested?.Invoke(this, EventArgs.Empty);

    private string BuildReport()
    {
        var builder = new StringBuilder();
        builder.AppendLine("1Salem Server Manager - Server Diagnostics");
        builder.AppendLine($"Captured: {DateTimeOffset.Now:O}");
        foreach (var check in _checks)
        {
            builder.AppendLine(
                $"[{check.Status}] {check.Name}: {DiagnosticsService.Redact(check.Detail)}");
            builder.AppendLine($"Suggested action: {check.SuggestedAction}");
        }

        return builder.ToString();
    }

    private static DiagnosticRow Check(
        string name,
        bool passed,
        string detail,
        string suggestedAction) =>
        new(name, passed ? "Passed" : "Failed", detail, suggestedAction);

    private static DiagnosticRow Optional(
        string name,
        string detail,
        bool availableOrNotRequired,
        string suggestedAction) =>
        new(
            name,
            availableOrNotRequired ? "Passed" : "Warning",
            detail,
            suggestedAction);

    private static string FormatError(string? error) =>
        string.IsNullOrWhiteSpace(error)
            ? "."
            : $" · {DiagnosticsService.Redact(error)}";

    private static string FormatBytes(long bytes) =>
        $"{bytes / (1024d * 1024d * 1024d):0.0} GB";

    private sealed record DiagnosticRow(
        string Name,
        string Status,
        string Detail,
        string SuggestedAction);
}
