using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ServerManager.Contracts;
using ServerManager.Core;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using MessageBox = System.Windows.MessageBox;

namespace ServerManager.Client.Controls;

public partial class PalworldMemoryPerformanceControl :
    System.Windows.Controls.UserControl,
    IDisposable
{
    private const long Gibibyte = 1024L * 1024 * 1024;
    private readonly HttpClient _httpClient = new()
    {
        BaseAddress = new Uri(AgentTransportDefaults.ResolveLoopbackApiUrl()),
        Timeout = TimeSpan.FromMinutes(3)
    };
    private readonly MemoryPolicyEditorViewModel _editor = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private Guid? _serverId;
    private MemoryPerformancePolicySnapshot? _snapshot;
    private ServerStartBudgetSnapshot? _startBudget;
    private bool _activatingPreset;

    public PalworldMemoryPerformanceControl()
    {
        InitializeComponent();
        DataContext = _editor;
        _editor.SetSaveHandler(SaveChangesAsync);
        _editor.PresetSelectionRequested += async (_, mode) =>
            await ActivatePresetAsync(mode);
    }

    public MemoryPolicyEditorViewModel Editor => _editor;

    public async void SetServer(Guid? serverId)
    {
        _serverId = serverId;
        if (serverId is not null && IsLoaded)
        {
            await RefreshAsync();
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) =>
        await RefreshAsync();

    private async Task RefreshAsync(bool forceEditorValues = false)
    {
        if (forceEditorValues)
        {
            await _refreshGate.WaitAsync();
        }
        else if (!await _refreshGate.WaitAsync(0))
        {
            return;
        }

        try
        {
            _snapshot =
                await _httpClient.GetFromJsonAsync<MemoryPerformancePolicySnapshot>(
                    "/api/v1/resources/memory-performance");
            if (_snapshot is null)
            {
                return;
            }

            _editor.LoadSnapshot(_snapshot, forceEditorValues);
            SystemMemoryText.Text =
                $"Total {GiB(_snapshot.TotalSystemMemoryBytes):F2} GiB\n" +
                $"Used {GiB(_snapshot.UsedSystemMemoryBytes):F2} GiB\n" +
                $"Available {GiB(_snapshot.AvailableSystemMemoryBytes):F2} GiB\n" +
                $"Agent {GiB(_snapshot.AgentMemoryBytes):F2} GiB";
            PalworldMemoryText.Text = _snapshot.HasPalworldServer
                ? $"Working {GiB(_snapshot.PalworldWorkingSetBytes):F2} GiB\n" +
                  $"Private {GiB(_snapshot.PalworldPrivateMemoryBytes):F2} GiB\n" +
                  $"Peak {GiB(_snapshot.PalworldPeakMemoryBytes):F2} GiB"
                : "No Palworld server registered";
            ServerMemoryText.Text =
                $"Active usage {GiB(_snapshot.ActiveManagedServerMemoryBytes):F2} GiB\n" +
                (_snapshot.HasMinecraftServer
                    ? $"Minecraft configured {GiB(_snapshot.MinecraftConfiguredMemoryBytes):F2} GiB\n"
                    : string.Empty) +
                $"Budget {GiB(_snapshot.MaximumServerBudgetBytes):F2} GiB\n" +
                $"Windows reserve {GiB(_snapshot.WindowsReserveBytes):F2} GiB";
            ProcessIdentityText.Text = _snapshot.HasPalworldServer
                ? $"Root PID {_snapshot.PalworldRootProcessId?.ToString() ?? "-"}\n" +
                  $"Game PID {_snapshot.PalworldGameProcessId?.ToString() ?? "-"}\n" +
                  $"Players {_snapshot.PalworldPlayers}"
                : "No managed Palworld process";

            if (!_editor.HasUnsavedChanges || forceEditorValues)
            {
                HardLimitBox.Text = _snapshot.HardMemoryLimitBytes is { } hard
                    ? FormatGiB(hard)
                    : string.Empty;
                AffinityBox.Text =
                    _snapshot.CpuAffinityMask?.ToString(CultureInfo.InvariantCulture) ??
                    string.Empty;
                RestoreBalancedBox.IsChecked =
                    _snapshot.RestoreBalancedOnServerStop;
                StopLowerBox.IsChecked =
                    _snapshot.ActiveProfile == ResourceMode.OneGameAtATime;
            }

            RecommendationText.Text =
                string.Join(Environment.NewLine, _snapshot.Recommendations);
            PolicyStatusText.Text = _snapshot.Warnings.Count == 0
                ? "No active resource warnings."
                : string.Join(Environment.NewLine, _snapshot.Warnings);
            await RefreshPriorityStatusAsync();
            if (_serverId is not null)
            {
                await RecalculateBudgetAsync();
            }
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException)
        {
            PolicyStatusText.Text =
                $"Memory policy unavailable: {exception.Message}";
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task ActivatePresetAsync(ResourceMode mode)
    {
        if (_snapshot is null || _activatingPreset)
        {
            return;
        }

        _activatingPreset = true;
        PolicyStatusText.Text = $"Activating {mode}...";
        try
        {
            ResourceProfileRequest request;
            if (mode == ResourceMode.Custom)
            {
                if (!_editor.TryBuildRequest(out request, out var error))
                {
                    PolicyStatusText.Text = error;
                    return;
                }
            }
            else
            {
                request = new ResourceProfileRequest(
                    mode,
                    RestoreBalancedOnServerStop:
                        RestoreBalancedBox.IsChecked == true);
            }

            request = request with
            {
                CpuAffinityMask = _snapshot.CpuAffinityMask,
                HardMemoryLimitBytes = _snapshot.HardMemoryLimitBytes,
                RestoreBalancedOnServerStop =
                    RestoreBalancedBox.IsChecked == true
            };
            if (!await PostPolicyAsync(request))
            {
                return;
            }

            await RefreshAsync(true);
            PolicyStatusText.Text =
                $"{mode} is active and persisted. " +
                (mode == ResourceMode.Custom
                    ? "Custom values are now editable."
                    : "Preset values are controlled by the selected profile.");
        }
        finally
        {
            _activatingPreset = false;
        }
    }

    private async Task SaveChangesAsync()
    {
        if (!_editor.TryBuildRequest(out var request, out var error))
        {
            PolicyStatusText.Text = error;
            return;
        }

        if (!TryOptionalPolicyValues(
                out var affinity,
                out var hardLimit,
                out error))
        {
            PolicyStatusText.Text = error;
            return;
        }

        request = request with
        {
            CpuAffinityMask = affinity,
            StopLowerPriorityGame = StopLowerBox.IsChecked == true,
            HardMemoryLimitBytes = hardLimit,
            HardLimitConfirmation = HardConfirmationBox.Text,
            RestoreBalancedOnServerStop =
                RestoreBalancedBox.IsChecked == true
        };
        PolicyStatusText.Text = "Saving and verifying policy values...";
        if (!await PostPolicyAsync(request))
        {
            return;
        }

        await RefreshAsync(true);
        PolicyStatusText.Text =
            $"{_editor.SelectedMemoryPolicyPreset} values are saved in the Agent database. " +
            "No Palworld restart was required.";
    }

    private bool TryOptionalPolicyValues(
        out long? affinity,
        out long? hardLimit,
        out string error)
    {
        affinity = null;
        hardLimit = null;
        error = string.Empty;
        if (!string.IsNullOrWhiteSpace(AffinityBox.Text))
        {
            if (!long.TryParse(
                    AffinityBox.Text,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var parsedAffinity) ||
                parsedAffinity <= 0)
            {
                error = "CPU affinity must be a positive decimal bit mask.";
                return false;
            }

            affinity = parsedAffinity;
        }

        if (!string.IsNullOrWhiteSpace(HardLimitBox.Text))
        {
            if (!MemoryPolicyEditorViewModel.TryParsePositiveGiB(
                    HardLimitBox.Text,
                    out var parsedHardLimit))
            {
                error = "Enter a valid hard-limit value in GiB.";
                return false;
            }

            hardLimit = parsedHardLimit;
        }

        return true;
    }

    private async Task<bool> PostPolicyAsync(ResourceProfileRequest request)
    {
        using var response = await _httpClient.PostAsJsonAsync(
            "/api/v1/resources/profile",
            request);
        if (response.IsSuccessStatusCode)
        {
            return true;
        }

        var operation =
            await response.Content.ReadFromJsonAsync<OperationResult>();
        PolicyStatusText.Text =
            $"{operation?.ErrorCode ?? "Policy save failed"}: " +
            $"{operation?.Message ?? await response.Content.ReadAsStringAsync()}";
        return false;
    }

    private async void Priority_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string profile })
        {
            return;
        }

        PriorityStatusText.Text = "Applying...";
        try
        {
            HttpResponseMessage response;
            if (profile == "Balanced")
            {
                response = await _httpClient.PostAsJsonAsync(
                    "/api/v1/resources/profile",
                    new ResourceProfileRequest(
                        ResourceMode.Balanced,
                        RestoreBalancedOnServerStop:
                            RestoreBalancedBox.IsChecked == true));
            }
            else
            {
                response = await _httpClient.PostAsync(
                    $"/api/v1/resources/prioritize/{profile}",
                    null);
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    var error =
                        await response.Content.ReadFromJsonAsync<OperationResult>();
                    PriorityStatusText.Text =
                        error?.ErrorCode == "PermissionDenied"
                            ? $"Permission denied: {error.Message}"
                            : $"Failed: {error?.Message ?? await response.Content.ReadAsStringAsync()}";
                    return;
                }
            }

            await RefreshAsync(true);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException)
        {
            PriorityStatusText.Text = $"Failed: {exception.Message}";
        }
    }

    private async Task RefreshPriorityStatusAsync()
    {
        var status =
            await _httpClient.GetFromJsonAsync<ResourceProfileApplyResponse>(
                "/api/v1/resources/profile/status");
        if (status is null)
        {
            return;
        }

        var processLines = status.Processes
            .Select(process =>
                $"{process.Game}: {process.Message} " +
                $"Root {process.RootProcessId?.ToString() ?? "-"} / " +
                $"Game {process.GameProcessId?.ToString() ?? "-"} / " +
                $"Priority {process.ActualPriority?.ToString() ?? "-"}")
            .ToArray();
        PriorityStatusText.Text =
            $"{status.State}: {status.Message}" +
            (processLines.Length == 0
                ? string.Empty
                : Environment.NewLine +
                  string.Join(Environment.NewLine, processLines));
        HighlightPriority(status.ActiveMode);
    }

    private async void RecalculateBudget_Click(
        object sender,
        RoutedEventArgs e) =>
        await RecalculateBudgetAsync();

    private async Task RecalculateBudgetAsync()
    {
        if (_serverId is null)
        {
            BudgetStatusText.Text = "Select a registered Palworld server.";
            StartAnywayButton.Visibility = Visibility.Collapsed;
            return;
        }

        try
        {
            _startBudget =
                await _httpClient.GetFromJsonAsync<ServerStartBudgetSnapshot>(
                    $"/api/v1/servers/{_serverId}/start-budget");
            if (_startBudget is null)
            {
                return;
            }

            BudgetStatusText.Text =
                $"{_startBudget.Reason}\n" +
                $"Available now {GiB(_startBudget.AvailableMemoryBytes):F2} GiB · " +
                $"Expected remaining {GiB(_startBudget.ExpectedRemainingMemoryBytes):F2} GiB";
            BudgetStatusText.Foreground = _startBudget.IsSafe
                ? (Brush)Application.Current.Resources["SuccessBrush"]
                : (Brush)Application.Current.Resources["WarningBrush"];
            StartAnywayButton.Visibility =
                _startBudget.CanStartAnywayOnce
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException)
        {
            BudgetStatusText.Text =
                $"Budget recalculation failed: {exception.Message}";
            StartAnywayButton.Visibility = Visibility.Collapsed;
        }
    }

    private async void StartAnywayOnce_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_serverId is null ||
            _startBudget is not
            {
                CanStartAnywayOnce: true
            } budget)
        {
            return;
        }

        var confirmation =
            "Start Palworld once outside the shared-budget recommendation?\n\n" +
            $"Available RAM: {GiB(budget.AvailableMemoryBytes):F2} GiB\n" +
            $"Windows reserve: {GiB(budget.WindowsReserveBytes):F2} GiB\n" +
            $"Active server usage: {GiB(budget.ActiveManagedServerMemoryBytes):F2} GiB\n" +
            $"Estimated Palworld allowance: {GiB(budget.EstimatedServerAllowanceBytes):F2} GiB\n" +
            $"Expected remaining RAM: {GiB(budget.ExpectedRemainingMemoryBytes):F2} GiB\n\n" +
            "This applies to this manual start only. Auto Start and the hard memory limit remain unchanged.";
        if (MessageBox.Show(
                confirmation,
                "Start Anyway Once",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        using var response = await _httpClient.PostAsJsonAsync(
            $"/api/v1/servers/{_serverId}/start/override",
            new ServerStartRequest(true, "START ANYWAY"));
        BudgetStatusText.Text = response.IsSuccessStatusCode
            ? "One-time manual start accepted. The override was audited."
            : await response.Content.ReadAsStringAsync();
        await RecalculateBudgetAsync();
    }

    private void HighlightPriority(ResourceMode mode)
    {
        var selected = mode switch
        {
            ResourceMode.MinecraftPriority => MinecraftPriorityButton,
            ResourceMode.PalworldPriority => PalworldPriorityButton,
            _ => BalancedPriorityButton
        };
        foreach (var button in new[]
                 {
                     BalancedPriorityButton,
                     MinecraftPriorityButton,
                     PalworldPriorityButton
                 })
        {
            button.Background = button == selected
                ? (Brush)Application.Current.Resources["AccentBrush"]
                : (Brush)Application.Current.Resources["PanelAltBrush"];
            button.Foreground = button == selected
                ? Brushes.Black
                : (Brush)Application.Current.Resources["TextBrush"];
        }
    }

    private static double GiB(long bytes) => bytes / (double)Gibibyte;

    private static string FormatGiB(long bytes) =>
        GiB(bytes).ToString("0.00", CultureInfo.CurrentCulture);
}
