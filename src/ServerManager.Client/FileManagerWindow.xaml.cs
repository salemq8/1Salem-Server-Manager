using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using Microsoft.Win32;
using ServerManager.Contracts;
using ServerManager.Core;

namespace ServerManager.Client;

public partial class FileManagerWindow : Window
{
    private readonly HttpClient _httpClient = new()
    {
        BaseAddress = new Uri(AgentTransportDefaults.ResolveLoopbackApiUrl()),
        Timeout = TimeSpan.FromSeconds(30)
    };
    private ManagedFileEntry[] _entries = [];
    private string _currentPath = string.Empty;
    private string? _openTextPath;

    public FileManagerWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadServersAsync();
        Closed += (_, _) => _httpClient.Dispose();
    }

    private GameServerDefinition? SelectedServer =>
        ServerBox.SelectedItem as GameServerDefinition;

    private ManagedFileEntry? SelectedEntry =>
        (FileList.SelectedItem as FileDisplay)?.Entry;

    private async void ServerBox_SelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _currentPath = string.Empty;
        await RefreshAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) =>
        await RefreshAsync();

    private async void Up_Click(object sender, RoutedEventArgs e)
    {
        _currentPath = string.IsNullOrWhiteSpace(_currentPath)
            ? string.Empty
            : Path.GetDirectoryName(_currentPath) ?? string.Empty;
        await RefreshAsync();
    }

    private async void FileList_MouseDoubleClick(
        object sender,
        System.Windows.Input.MouseButtonEventArgs e)
    {
        if (SelectedEntry is { IsDirectory: true } directory)
        {
            _currentPath = directory.RelativePath;
            await RefreshAsync();
        }
        else
        {
            await OpenSelectedTextAsync();
        }
    }

    private void FileList_SelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        RenameBox.Text = SelectedEntry?.Name ?? string.Empty;
        DeleteConfirmationBox.Text = SelectedEntry is { IsDirectory: false } entry
            ? $"DELETE {entry.Name}"
            : string.Empty;
    }

    private async void OpenText_Click(object sender, RoutedEventArgs e) =>
        await OpenSelectedTextAsync();

    private async void SaveText_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedServer is not { } server || _openTextPath is null)
        {
            StatusText.Text = "Open a supported text file first.";
            return;
        }

        await PostAsync(
            $"/api/v1/servers/{server.Id}/file-text",
            new FileWriteRequest(_openTextPath, EditorBox.Text));
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_openTextPath is null)
        {
            StatusText.Text = "Open a supported text file first.";
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = Path.GetFileName(_openTextPath),
            Filter = "Text files|*.txt;*.log;*.json;*.properties;*.ini;*.cfg;*.yml;*.yaml;*.xml;*.cmd;*.bat|All files|*.*",
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) == true)
        {
            File.WriteAllText(dialog.FileName, EditorBox.Text);
            StatusText.Text = $"Exported to {dialog.FileName}";
        }
    }

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedServer is { } server && SelectedEntry is { } entry)
        {
            await PostAsync(
                $"/api/v1/servers/{server.Id}/files/rename",
                new FileRenameRequest(entry.RelativePath, RenameBox.Text));
            await RefreshAsync();
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedServer is { } server && SelectedEntry is { IsDirectory: false } entry)
        {
            await PostAsync(
                $"/api/v1/servers/{server.Id}/files/delete",
                new FileDeleteRequest(entry.RelativePath, DeleteConfirmationBox.Text));
            EditorBox.Clear();
            _openTextPath = null;
            await RefreshAsync();
        }
    }

    private void SearchBox_TextChanged(
        object sender,
        System.Windows.Controls.TextChangedEventArgs e) =>
        RenderEntries();

    private async Task LoadServersAsync()
    {
        try
        {
            ServerBox.ItemsSource = await _httpClient.GetFromJsonAsync<GameServerDefinition[]>(
                "/api/v1/servers") ?? [];
            ServerBox.SelectedIndex = ServerBox.Items.Count > 0 ? 0 : -1;
        }
        catch (HttpRequestException exception)
        {
            StatusText.Text = $"Agent unavailable: {exception.Message}";
        }
    }

    private async Task RefreshAsync()
    {
        if (SelectedServer is not { } server)
        {
            return;
        }

        try
        {
            var encoded = Uri.EscapeDataString(_currentPath);
            _entries = await _httpClient.GetFromJsonAsync<ManagedFileEntry[]>(
                $"/api/v1/servers/{server.Id}/files?relativePath={encoded}") ?? [];
            PathBox.Text = string.IsNullOrWhiteSpace(_currentPath) ? "." : _currentPath;
            RenderEntries();
            StatusText.Text =
                "Only the registered server root is visible. Reparse points and path escapes are blocked.";
        }
        catch (HttpRequestException exception)
        {
            StatusText.Text = $"Folder refresh failed: {exception.Message}";
        }
    }

    private void RenderEntries()
    {
        if (FileList is null)
        {
            return;
        }

        var query = SearchBox?.Text?.Trim() ?? string.Empty;
        FileList.ItemsSource = _entries
            .Where(entry =>
                string.IsNullOrEmpty(query) ||
                entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(entry => new FileDisplay(
                entry.IsDirectory
                    ? $"[Folder] {entry.Name}"
                    : $"{entry.Name} ({entry.SizeBytes / 1024d:F1} KiB)",
                entry))
            .ToArray();
    }

    private async Task OpenSelectedTextAsync()
    {
        if (SelectedServer is not { } server ||
            SelectedEntry is not { IsDirectory: false } entry)
        {
            return;
        }

        try
        {
            var encoded = Uri.EscapeDataString(entry.RelativePath);
            var text = await _httpClient.GetFromJsonAsync<ManagedTextFile>(
                $"/api/v1/servers/{server.Id}/file-text?relativePath={encoded}");
            EditorBox.Text = text?.Content ?? string.Empty;
            _openTextPath = text?.RelativePath;
            StatusText.Text =
                "Saving creates a timestamped safety copy under backups/config-edits first.";
        }
        catch (HttpRequestException exception)
        {
            StatusText.Text = $"File cannot be opened as managed text: {exception.Message}";
        }
    }

    private async Task PostAsync(string path, object body)
    {
        using var response = await _httpClient.PostAsJsonAsync(path, body);
        var result = await response.Content.ReadFromJsonAsync<OperationResult>();
        StatusText.Text = result?.Success == true
            ? "Operation completed."
            : $"Operation failed: {result?.Message ?? response.ReasonPhrase}";
    }

    private sealed record FileDisplay(string Display, ManagedFileEntry Entry);
}
