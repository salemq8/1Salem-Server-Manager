using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using ServerManager.Contracts;
using Forms = System.Windows.Forms;
using Clipboard = System.Windows.Clipboard;

namespace ServerManager.Client;

public partial class MinecraftInstallWindow : Window
{
    private const long Gibibyte = 1024L * 1024 * 1024;
    private readonly HttpClient _httpClient =
        AgentTransportDefaults.CreateLoopbackHttpClient(TimeSpan.FromMinutes(30));
    private readonly List<string> _progressLines = [];
    private readonly MinecraftInstallFolder _folder = new(MinecraftInstallFolder.DefaultParentFolder);
    private MinecraftCreationPlan? _plan;
    private MinecraftCreationProgress? _lastProgress;
    private int _step = 1;

    public MinecraftInstallWindow()
    {
        InitializeComponent();
        RefreshFolder();
        ServerNameBox.TextChanged += (_, _) => RefreshFolder();
        FolderBox.TextChanged += (_, _) => _folder.PathChanged(FolderBox.Text);
        Loaded += OnLoaded;
        Closed += (_, _) => _httpClient.Dispose();
        RenderStep();
    }

    public event EventHandler<MinecraftInstallResult>? ServerCreated;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            VersionStatusText.Text = "Loading official vanilla releases…";
            var versions = await _httpClient.GetFromJsonAsync<MinecraftVersionDescriptor[]>(
                "/api/v1/minecraft/versions") ?? [];
            VersionBox.ItemsSource = versions;
            VersionBox.SelectedIndex = versions.Length > 0 ? 0 : -1;
            VersionStatusText.Text = versions.Length > 0
                ? $"Latest official release: {versions[0].Id}. " +
                  $"Required Java: {versions[0].RequiredJavaMajor}. " +
                  $"Download: {FormatBytes(versions[0].SizeBytes)}."
                : "No official releases were returned.";
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException)
        {
            VersionStatusText.Text =
                $"Could not load official releases. Ensure the Agent is running. {exception.Message}";
        }
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Choose the parent folder for the new Minecraft server",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };
        if (dialog.ShowDialog() == Forms.DialogResult.OK)
        {
            FolderBox.Text = _folder.ChooseParent(dialog.SelectedPath, ServerNameBox.Text);
        }
    }

    // Keeps the automatic folder in step with the server name and re-checks it is still free.
    private void RefreshFolder()
    {
        if (_folder.Suggest(ServerNameBox.Text) is { } path)
        {
            FolderBox.Text = path;
        }
    }

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateStep(_step))
        {
            return;
        }

        if (_step == 2)
        {
            if (!await RefreshPlanAsync())
            {
                return;
            }
        }
        else if (_step is 3 or 4)
        {
            if (!await RefreshPlanAsync())
            {
                return;
            }
        }

        _step = Math.Min(5, _step + 1);
        if (_step == 5)
        {
            RenderReview();
        }

        RenderStep();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        _step = Math.Max(1, _step - 1);
        RenderStep();
    }

    private bool ValidateStep(int step)
    {
        FooterStatusText.Text = string.Empty;
        if (step == 1)
        {
            // Only when leaving step 1: after the Review the destination must not change silently;
            // a folder that appears later is refused by the Agent instead.
            if (_step == 1)
            {
                RefreshFolder();
            }

            if (string.IsNullOrWhiteSpace(ServerNameBox.Text) ||
                string.IsNullOrWhiteSpace(FolderBox.Text) ||
                VersionBox.SelectedItem is not MinecraftVersionDescriptor ||
                !int.TryParse(PortBox.Text, out var port) ||
                port is < 1 or > 65_535 ||
                !int.TryParse(MaxPlayersBox.Text, out var players) ||
                players is < 1 or > 1_000)
            {
                FooterStatusText.Text =
                    "Enter a name, fresh path, official version, valid port, and player limit.";
                return false;
            }
        }
        else if (step == 2)
        {
            if (!int.TryParse(ViewDistanceBox.Text, out var view) ||
                !int.TryParse(SimulationDistanceBox.Text, out var simulation) ||
                view is < 2 or > 32 ||
                simulation is < 2 or > 32)
            {
                FooterStatusText.Text =
                    "View and simulation distances must be between 2 and 32.";
                return false;
            }
        }
        else if (step == 3)
        {
            if (!int.TryParse(XmsBox.Text, out var xms) ||
                !int.TryParse(XmxBox.Text, out var xmx) ||
                xms <= 0 ||
                xmx < xms)
            {
                FooterStatusText.Text =
                    "Xms must be greater than zero and Xmx must be greater than or equal to Xms.";
                return false;
            }
        }

        return true;
    }

    private async Task<bool> RefreshPlanAsync()
    {
        if (!TryReadNumbers(
                out var port,
                out _,
                out _,
                out _,
                out var xms,
                out var xmx))
        {
            FooterStatusText.Text = "Review numeric settings before continuing.";
            return false;
        }

        try
        {
            using var response = await _httpClient.PostAsJsonAsync(
                "/api/v1/minecraft/plan",
                new MinecraftPlanRequest(
                    FolderBox.Text,
                    ((MinecraftVersionDescriptor)VersionBox.SelectedItem).Id,
                    port,
                    xms,
                    xmx));
            if (!response.IsSuccessStatusCode)
            {
                FooterStatusText.Text = await ReadErrorAsync(response);
                return false;
            }

            _plan = await response.Content.ReadFromJsonAsync<MinecraftCreationPlan>();
            if (_plan is null)
            {
                FooterStatusText.Text = "The Agent returned an empty creation plan.";
                return false;
            }

            RenderPlan();
            return true;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException)
        {
            FooterStatusText.Text = $"Preflight failed: {exception.Message}";
            return false;
        }
    }

    private void RenderPlan()
    {
        if (_plan is null)
        {
            return;
        }

        TotalRamText.Text = FormatBytes(_plan.TotalMemoryBytes);
        AvailableRamText.Text = FormatBytes(_plan.AvailableMemoryBytes);
        ReserveRamText.Text = FormatBytes(_plan.WindowsReserveBytes);
        RecommendedRamText.Text =
            $"{_plan.RecommendedMinimumMemoryBytes / (double)Gibibyte:F1}–" +
            $"{_plan.RecommendedMaximumMemoryBytes / (double)Gibibyte:F1} GB";
        MemoryWarningText.Text = _plan.Warnings.Count == 0
            ? "The selected memory values are within the current safety limit."
            : string.Join(Environment.NewLine, _plan.Warnings);
        ResultAddressText.Text = _plan.LocalAddress ?? $"Port {_plan.Port}";
        PortStatusText.Text = _plan.PortAvailable
            ? $"TCP port {_plan.Port} is available."
            : $"TCP port {_plan.Port} is currently in use.";
    }

    private void RenderReview()
    {
        if (_plan is null ||
            !TryReadNumbers(
                out var port,
                out var maxPlayers,
                out var view,
                out var simulation,
                out var xms,
                out var xmx))
        {
            return;
        }

        var version = (MinecraftVersionDescriptor)VersionBox.SelectedItem;
        ReviewText.Text = string.Join(
            Environment.NewLine,
            $"Server name:       {ServerNameBox.Text}",
            $"Destination:       {Path.GetFullPath(FolderBox.Text)}",
            $"Minecraft version: {version.Id}",
            $"Required Java:     {version.RequiredJavaMajor}",
            $"Download size:     {FormatBytes(version.SizeBytes)}",
            $"Address:           {_plan.LocalAddress ?? $"port {port}"}",
            $"Xms / Xmx:         {xms} MB / {xmx} MB",
            $"Players:           {maxPlayers}",
            $"Mode / difficulty: {Selected(GameModeBox)} / {Selected(DifficultyBox)}",
            $"View / simulation: {view} / {simulation}",
            $"Online / whitelist:{OnlineModeBox.IsChecked == true} / {WhitelistBox.IsChecked == true}",
            $"Firewall:          {FirewallBox.IsChecked == true}");
        ReviewWarningText.Text = _plan.Warnings.Count == 0
            ? "Creation is ready. The server is not marked successful until first startup and port verification complete."
            : string.Join(Environment.NewLine, _plan.Warnings);
    }

    private async void TestPort_Click(object sender, RoutedEventArgs e) =>
        _ = await RefreshPlanAsync();

    private async void RefreshNetwork_Click(object sender, RoutedEventArgs e) =>
        _ = await RefreshPlanAsync();

    private void MemoryPreset_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (XmsBox is null || XmxBox is null)
        {
            return;
        }

        var preset = Selected(MemoryPresetBox);
        (var xms, var xmx) = preset switch
        {
            "Light" => (1024, 3072),
            "Balanced" => (2048, 4096),
            "Performance" => (4096, 8192),
            _ => (0, 0)
        };
        if (xms > 0)
        {
            XmsBox.Text = xms.ToString();
            XmxBox.Text = xmx.ToString();
        }
    }

    private async void CreateServer_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateStep(1) ||
            !ValidateStep(2) ||
            !ValidateStep(3) ||
            EulaBox.IsChecked != true)
        {
            FooterStatusText.Text =
                EulaBox.IsChecked == true
                    ? FooterStatusText.Text
                    : "Explicit Minecraft EULA acceptance is required.";
            return;
        }

        if (!await RefreshPlanAsync() || _plan?.PortAvailable != true)
        {
            FooterStatusText.Text =
                _plan?.PortAvailable == false
                    ? $"TCP port {_plan.Port} is already in use."
                    : FooterStatusText.Text;
            return;
        }

        if (!TryBuildRequest(out var request))
        {
            return;
        }

        try
        {
            SetProgressMode();
            using var response = await _httpClient.PostAsJsonAsync(
                "/api/v1/minecraft/create",
                request);
            if (!response.IsSuccessStatusCode)
            {
                ShowImmediateFailure(await ReadErrorAsync(response));
                return;
            }

            var started =
                await response.Content.ReadFromJsonAsync<MinecraftCreationStartResponse>();
            if (started is null)
            {
                ShowImmediateFailure("The Agent did not return a creation operation ID.");
                return;
            }

            await PollCreationAsync(started.OperationId);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException)
        {
            ShowImmediateFailure($"Creation request failed: {exception.Message}");
        }
    }

    private async Task PollCreationAsync(Guid operationId)
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            var progress =
                await _httpClient.GetFromJsonAsync<MinecraftCreationProgress>(
                    $"/api/v1/minecraft/creation/{operationId}");
            if (progress is null)
            {
                ShowImmediateFailure(
                    "The Agent lost the creation operation. It may have restarted.");
                return;
            }

            _lastProgress = progress;
            ProgressStageText.Text = progress.StageLabel;
            ProgressMessageText.Text = progress.Message;
            CreationProgressBar.Value = progress.Percent;
            var timestamp = (progress.UpdatedAtUtc ?? DateTimeOffset.UtcNow)
                .ToLocalTime()
                .ToString("HH:mm:ss");
            var line = $"{timestamp}  {progress.Percent,3}%  " +
                       $"{progress.StageLabel}: {progress.Message}";
            if (_progressLines.Count == 0 || _progressLines[^1] != line)
            {
                _progressLines.Add(line);
                ProgressList.ItemsSource = _progressLines.ToArray();
                ProgressList.ScrollIntoView(ProgressList.Items[^1]);
            }

            if (!progress.IsComplete)
            {
                continue;
            }

            CloseButton.Visibility = Visibility.Visible;
            if (progress.Succeeded && progress.Result is { } result)
            {
                SuccessPanel.Visibility = Visibility.Visible;
                SuccessAddressText.Text = result.LocalAddress ?? $"Port {PortBox.Text}";
                FooterStatusText.Text =
                    "Minecraft was downloaded, verified, started, and its port is ready.";
                ServerCreated?.Invoke(this, result);
            }
            else
            {
                RenderFailure(progress.Failure);
            }

            return;
        }
    }

    private void RenderFailure(StartupFailureDetails? failure)
    {
        FailureDetailsPanel.Visibility = Visibility.Visible;
        FailureDetailsText.Text = failure is null
            ? _lastProgress?.Message ?? "Minecraft creation failed."
            : string.Join(
                Environment.NewLine,
                $"Stage: {failure.Stage}",
                $"Error: {failure.ErrorCode}",
                $"Message: {failure.Message}",
                $"Executable: {failure.ExecutablePath ?? "not resolved"}",
                $"Working directory: {failure.WorkingDirectory ?? "not created"}",
                $"Exit code: {failure.ExitCode?.ToString() ?? "not available"}",
                $"Port conflict: {failure.PortConflict}",
                $"Java: {failure.JavaDetection}",
                $"Memory: {failure.MemoryAllocation}",
                $"Suggested fix: {failure.SuggestedFix}",
                string.Empty,
                "Last console lines:",
                string.Join(Environment.NewLine, failure.ConsoleLines));
        FooterStatusText.Text =
            "Creation was not marked successful. Review the failed stage and retry after correction.";
    }

    private void ShowImmediateFailure(string message)
    {
        FailureDetailsPanel.Visibility = Visibility.Visible;
        FailureDetailsText.Text = message;
        CloseButton.Visibility = Visibility.Visible;
        FooterStatusText.Text = "Creation failed before completion.";
    }

    private void SetProgressMode()
    {
        Step1Panel.Visibility = Visibility.Collapsed;
        Step2Panel.Visibility = Visibility.Collapsed;
        Step3Panel.Visibility = Visibility.Collapsed;
        Step4Panel.Visibility = Visibility.Collapsed;
        Step5Panel.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Visible;
        BackButton.Visibility = Visibility.Collapsed;
        NextButton.Visibility = Visibility.Collapsed;
        CreateServerButton.Visibility = Visibility.Collapsed;
        _progressLines.Clear();
        ProgressList.ItemsSource = null;
        FooterStatusText.Text = "Keep this window open to follow verified creation progress.";
    }

    private void RenderStep()
    {
        Step1Panel.Visibility = _step == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step2Panel.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;
        Step3Panel.Visibility = _step == 3 ? Visibility.Visible : Visibility.Collapsed;
        Step4Panel.Visibility = _step == 4 ? Visibility.Visible : Visibility.Collapsed;
        Step5Panel.Visibility = _step == 5 ? Visibility.Visible : Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Collapsed;
        BackButton.IsEnabled = _step > 1;
        NextButton.Visibility = _step < 5 ? Visibility.Visible : Visibility.Collapsed;
        CreateServerButton.Visibility =
            _step == 5 ? Visibility.Visible : Visibility.Collapsed;
        var labels = new[] { Step1Label, Step2Label, Step3Label, Step4Label, Step5Label };
        for (var index = 0; index < labels.Length; index++)
        {
            labels[index].Foreground = index + 1 == _step
                ? (System.Windows.Media.Brush)FindResource("AccentBrush")
                : (System.Windows.Media.Brush)FindResource("MutedTextBrush");
            labels[index].FontWeight =
                index + 1 == _step ? FontWeights.Bold : FontWeights.Normal;
        }
    }

    private bool TryBuildRequest(out MinecraftInstallRequest request)
    {
        request = null!;
        if (!TryReadNumbers(
                out var port,
                out var maxPlayers,
                out var view,
                out var simulation,
                out var xms,
                out var xmx) ||
            VersionBox.SelectedItem is not MinecraftVersionDescriptor version)
        {
            FooterStatusText.Text = "One or more numeric settings are invalid.";
            return false;
        }

        request = new MinecraftInstallRequest(
            ServerNameBox.Text.Trim(),
            Path.GetFullPath(FolderBox.Text),
            version.Id,
            port,
            xms,
            xmx,
            new MinecraftServerSettings(
                MotdBox.Text,
                maxPlayers,
                Selected(DifficultyBox),
                Selected(GameModeBox),
                OnlineModeBox.IsChecked == true,
                view,
                simulation,
                WhitelistBox.IsChecked == true,
                HardcoreBox.IsChecked == true,
                PvpBox.IsChecked == true),
            true,
            FirewallBox.IsChecked == true,
            InstallJavaBox.IsChecked == true);
        return true;
    }

    private bool TryReadNumbers(
        out int port,
        out int maxPlayers,
        out int view,
        out int simulation,
        out int xms,
        out int xmx)
    {
        var portValid = int.TryParse(PortBox.Text, out port);
        var playersValid = int.TryParse(MaxPlayersBox.Text, out maxPlayers);
        var viewValid = int.TryParse(ViewDistanceBox.Text, out view);
        var simulationValid = int.TryParse(SimulationDistanceBox.Text, out simulation);
        var xmsValid = int.TryParse(XmsBox.Text, out xms);
        var xmxValid = int.TryParse(XmxBox.Text, out xmx);
        return portValid &&
               playersValid &&
               viewValid &&
               simulationValid &&
               xmsValid &&
               xmxValid;
    }

    private void CopyFailure_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(FailureDetailsText.Text))
        {
            FooterStatusText.Text =
                Shell.SafeClipboard.TrySetText(FailureDetailsText.Text)
                    ? "Diagnostics copied."
                    : "The clipboard is busy. Please try Copy Diagnostics again.";
        }
    }

    private void CopySuccessAddress_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(SuccessAddressText.Text))
        {
            FooterStatusText.Text =
                Shell.SafeClipboard.TrySetText(SuccessAddressText.Text)
                    ? "Join address copied."
                    : "The clipboard is busy. Please try Copy Join Address again.";
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private static string Selected(System.Windows.Controls.ComboBox box) =>
        (box.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? string.Empty;

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response)
    {
        try
        {
            var error = await response.Content.ReadFromJsonAsync<ApiErrorResponse>();
            return error is null
                ? await response.Content.ReadAsStringAsync()
                : $"{error.WhatFailed} {error.SuggestedFix}";
        }
        catch (System.Text.Json.JsonException)
        {
            return await response.Content.ReadAsStringAsync();
        }
    }

    private static string FormatBytes(long bytes) =>
        bytes >= Gibibyte
            ? $"{bytes / (double)Gibibyte:F1} GB"
            : $"{bytes / (1024d * 1024):F0} MB";
}
