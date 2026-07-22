using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using ServerManager.Contracts;
using ServerManager.Core;

namespace ServerManager.Client;

public partial class ResourceGovernorWindow : Window
{
    private const long Gibibyte = 1024L * 1024 * 1024;
    private readonly HttpClient _httpClient = new()
    {
        BaseAddress = new Uri(AgentTransportDefaults.ResolveLoopbackApiUrl()),
        Timeout = TimeSpan.FromSeconds(20)
    };

    public ResourceGovernorWindow()
    {
        InitializeComponent();
        ModeBox.ItemsSource = Enum.GetValues<ResourceMode>();
        ModeBox.SelectedItem = ResourceMode.Balanced;
        Loaded += async (_, _) => await RefreshAsync();
        Closed += (_, _) => _httpClient.Dispose();
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (ModeBox.SelectedItem is not ResourceMode mode ||
            !TryReadGibibytes(ReserveBox.Text, out var reserve) ||
            !TryReadGibibytes(BudgetBox.Text, out var budget))
        {
            StatusText.Text = "Enter valid whole or decimal GiB values.";
            return;
        }

        var request = new ResourceProfileRequest(
            mode,
            reserve,
            budget,
            null,
            StopLowerBox.IsChecked == true);
        using var response = await _httpClient.PostAsJsonAsync(
            "/api/v1/resources/profile",
            request);
        if (!response.IsSuccessStatusCode)
        {
            StatusText.Text = await response.Content.ReadAsStringAsync();
            return;
        }

        Render(await response.Content.ReadFromJsonAsync<SystemResourceSnapshot>());
        StatusText.Text = $"{mode} profile applied.";
    }

    private async void PrioritizeMinecraft_Click(object sender, RoutedEventArgs e) =>
        await PrioritizeAsync(GameType.Minecraft);

    private async void PrioritizePalworld_Click(object sender, RoutedEventArgs e) =>
        await PrioritizeAsync(GameType.Palworld);

    private async void Refresh_Click(object sender, RoutedEventArgs e) =>
        await RefreshAsync();

    private async Task PrioritizeAsync(GameType game)
    {
        using var response = await _httpClient.PostAsync(
            $"/api/v1/resources/prioritize/{game}",
            null);
        response.EnsureSuccessStatusCode();
        Render(await response.Content.ReadFromJsonAsync<SystemResourceSnapshot>());
        ModeBox.SelectedItem = game == GameType.Minecraft
            ? ResourceMode.MinecraftPriority
            : ResourceMode.PalworldPriority;
        StatusText.Text = $"{game} now has safe Above Normal priority.";
    }

    private async Task RefreshAsync()
    {
        try
        {
            Render(await _httpClient.GetFromJsonAsync<SystemResourceSnapshot>(
                "/api/v1/resources"));
            StatusText.Text = "Resource status refreshed.";
        }
        catch (HttpRequestException exception)
        {
            StatusText.Text = $"Resource governor unavailable: {exception.Message}";
        }
    }

    private void Render(SystemResourceSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return;
        }

        CpuText.Text = $"{snapshot.CpuPercent:F1}%";
        MemoryText.Text = FormatBytes(snapshot.AvailableMemoryBytes);
        DiskText.Text = FormatBytes(snapshot.SystemDriveFreeBytes);
        WarningText.Text = snapshot.Warnings.Count == 0
            ? "No resource warnings."
            : string.Join(Environment.NewLine, snapshot.Warnings);
        ModeBox.SelectedItem = snapshot.ActivePolicy.Mode;
        ReserveBox.Text = (snapshot.ActivePolicy.WindowsReserveBytes / (double)Gibibyte)
            .ToString("0.##", CultureInfo.InvariantCulture);
        BudgetBox.Text = (snapshot.ActivePolicy.MaximumServerBudgetBytes / (double)Gibibyte)
            .ToString("0.##", CultureInfo.InvariantCulture);
        StopLowerBox.IsChecked = snapshot.ActivePolicy.StopLowerPriorityGame;
    }

    private static bool TryReadGibibytes(string text, out long bytes)
    {
        if (double.TryParse(
                text,
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var value) &&
            value >= 1 &&
            value <= long.MaxValue / (double)Gibibyte)
        {
            bytes = checked((long)(value * Gibibyte));
            return true;
        }

        bytes = 0;
        return false;
    }

    private static string FormatBytes(long bytes) =>
        $"{bytes / (double)Gibibyte:F1} GiB";
}
