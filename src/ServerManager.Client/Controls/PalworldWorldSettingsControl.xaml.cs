using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Diagnostics;
using Microsoft.Win32;
using ServerManager.Contracts;
using ServerManager.Core;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace ServerManager.Client.Controls;

public partial class PalworldWorldSettingsControl : System.Windows.Controls.UserControl, IDisposable
{
    private readonly HttpClient _httpClient = new()
    {
        BaseAddress = new Uri(AgentTransportDefaults.ResolveLoopbackApiUrl()),
        Timeout = TimeSpan.FromMinutes(5)
    };
    private readonly ObservableCollection<SettingRow> _settings = [];
    private readonly ICollectionView _settingsView;
    private Guid? _serverId;
    private bool _loading;
    private bool _syncingEditor;
    private string? _configurationPath;

    public PalworldWorldSettingsControl()
    {
        InitializeComponent();
        _settingsView = CollectionViewSource.GetDefaultView(_settings);
        _settingsView.Filter = FilterSetting;
        SettingsGrid.ItemsSource = _settingsView;
        Loaded += OnLoaded;
    }

    public async void SetServer(Guid? serverId)
    {
        if (_serverId == serverId &&
            (serverId is null || _settings.Count > 0 || !IsLoaded))
        {
            return;
        }

        _serverId = serverId;
        if (serverId is not null && IsLoaded)
        {
            await LoadAsync();
        }
    }

    public void Dispose()
    {
        Loaded -= OnLoaded;
        _httpClient.Dispose();
    }

    public bool HasUnsavedChanges => GetChanges().Count > 0;

    public event EventHandler? UnsavedStateChanged;

    public Task<bool> SaveForNextRestartAsync() => SaveAsync(false);

    public void DiscardChanges()
    {
        foreach (var setting in _settings)
        {
            setting.PendingValue = setting.CurrentValue;
        }

        RenderSelectedEditor();
        RenderUnsaved();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_serverId is not null && _settings.Count == 0)
        {
            await LoadAsync();
        }
    }

    private async Task LoadAsync()
    {
        if (_serverId is null || _loading)
        {
            return;
        }

        _loading = true;
        StatusText.Text = "Loading the live Palworld configuration...";
        try
        {
            var response =
                await _httpClient.GetFromJsonAsync<PalworldWorldSettingsResponse>(
                    $"/api/v1/servers/{_serverId}/palworld/world-settings");
            if (response is null)
            {
                StatusText.Text = "The Agent returned no world settings.";
                return;
            }

            _settings.Clear();
            foreach (var descriptor in response.Settings)
            {
                var row = new SettingRow(descriptor);
                row.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(SettingRow.PendingValue))
                    {
                        RenderUnsaved();
                    }
                };
                _settings.Add(row);
            }

            CategoryList.ItemsSource = new[] { "All" }
                .Concat(response.Categories)
                .ToArray();
            CategoryList.SelectedIndex = 0;
            PresetBox.ItemsSource = response.Presets;
            PresetBox.SelectedItem = "Custom";
            ConfigurationPathText.Text =
                $"Live file: {response.ConfigurationPath}";
            _configurationPath = response.ConfigurationPath;
            UnknownSettingsText.Text =
                response.UnknownSettings is { Count: > 0 } unknown
                    ? $"Preserved unsupported/newer fields ({unknown.Count}): " +
                      string.Join(", ", unknown.Keys.Order(StringComparer.OrdinalIgnoreCase))
                    : "No unsupported fields were detected. Unknown fields are preserved automatically.";
            StatusText.Text =
                "Values are read from the registered server's live WindowsServer configuration.";
            RenderUnsaved();
        }
        catch (Exception exception) when (
            exception is HttpRequestException or
            TaskCanceledException or
            JsonException)
        {
            StatusText.Text = $"World settings unavailable: {exception.Message}";
        }
        finally
        {
            _loading = false;
        }
    }

    private bool FilterSetting(object item)
    {
        if (item is not SettingRow setting)
        {
            return false;
        }

        var category = CategoryList.SelectedItem?.ToString();
        var query = SearchBox.Text.Trim();
        return (string.IsNullOrEmpty(category) ||
                category == "All" ||
                setting.Category.Equals(category, StringComparison.Ordinal)) &&
               (string.IsNullOrEmpty(query) ||
                setting.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                setting.DisplayName.Contains(
                    query,
                    StringComparison.OrdinalIgnoreCase) ||
                setting.ArabicDisplayName.Contains(
                    query,
                    StringComparison.OrdinalIgnoreCase) ||
                setting.Description.Contains(
                    query,
                    StringComparison.OrdinalIgnoreCase));
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) =>
        _settingsView.Refresh();

    private void CategoryList_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e) =>
        _settingsView.Refresh();

    private async void PresetBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_loading ||
            _serverId is null ||
            PresetBox.SelectedItem is not string preset ||
            preset == "Custom")
        {
            return;
        }

        try
        {
            var response =
                await _httpClient.GetFromJsonAsync<PalworldWorldSettingsPresetResponse>(
                    $"/api/v1/servers/{_serverId}/palworld/world-settings/presets/{Uri.EscapeDataString(preset)}");
            if (response is null)
            {
                return;
            }

            foreach (var (name, value) in response.Values)
            {
                var setting = _settings.FirstOrDefault(item =>
                    item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (setting is not null &&
                    !(setting.Name == "ServerPassword" &&
                      value == "(configured)"))
                {
                    setting.PendingValue = value;
                }
            }

            StatusText.Text = $"{preset} loaded as pending changes.";
            RenderSelectedEditor();
        }
        catch (HttpRequestException exception)
        {
            StatusText.Text = $"Preset could not be loaded: {exception.Message}";
        }
    }

    private void SettingsGrid_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e) =>
        RenderSelectedEditor();

    private void SettingsGrid_CellEditEnding(
        object sender,
        DataGridCellEditEndingEventArgs e)
    {
        Dispatcher.BeginInvoke(RenderSelectedEditor);
    }

    private void RenderSelectedEditor()
    {
        if (SettingsGrid.SelectedItem is not SettingRow setting)
        {
            return;
        }

        _syncingEditor = true;
        try
        {
            EditorTitleText.Text = setting.DisplayName;
            EditorArabicTitleText.Text = setting.ArabicDisplayName;
            EditorDescriptionText.Text =
                $"{setting.Description}\n{setting.ArabicDescription}";
            EditorRangeText.Text = setting.Minimum is not null
                ? $"Safe range: {setting.Minimum:0.######} to {setting.Maximum:0.######} {setting.Unit} · Default: {setting.DefaultValue} · Restart required"
                : $"Default: {setting.DefaultValue} · {setting.Unit} · Restart required";
            EditorWarningText.Text = setting.PerformanceWarning ?? string.Empty;
            NumericEditorPanel.Visibility =
                setting.ValueType is PalworldSettingValueType.Number or
                    PalworldSettingValueType.Integer
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            BooleanEditor.Visibility =
                setting.ValueType == PalworldSettingValueType.Boolean
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            EnumEditor.Visibility =
                setting.ValueType is PalworldSettingValueType.Enumeration or
                    PalworldSettingValueType.List
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            TextEditorBox.Visibility =
                setting.ValueType == PalworldSettingValueType.Text
                    ? Visibility.Visible
                    : Visibility.Collapsed;

            if (NumericEditorPanel.Visibility == Visibility.Visible)
            {
                NumericSlider.Minimum = setting.Minimum ?? 0;
                NumericSlider.Maximum = setting.Maximum ?? 100;
                NumericSlider.TickFrequency =
                    setting.ValueType == PalworldSettingValueType.Integer
                        ? 1
                        : Math.Max(
                            0.01,
                            (NumericSlider.Maximum - NumericSlider.Minimum) / 100);
                NumericEditorBox.Text = setting.PendingValue;
                if (double.TryParse(
                        setting.PendingValue,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out var number))
                {
                    NumericSlider.Value = Math.Clamp(
                        number,
                        NumericSlider.Minimum,
                        NumericSlider.Maximum);
                }
            }

            BooleanEditor.IsChecked =
                bool.TryParse(setting.PendingValue, out var enabled) &&
                enabled;
            EnumEditor.ItemsSource = setting.AllowedValues;
            EnumEditor.Text = setting.PendingValue;
            TextEditorBox.Text = setting.PendingValue == "(configured)"
                ? string.Empty
                : setting.PendingValue;
        }
        finally
        {
            _syncingEditor = false;
        }
    }

    private void NumericSlider_ValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingEditor || SettingsGrid.SelectedItem is not SettingRow setting)
        {
            return;
        }

        setting.PendingValue =
            setting.ValueType == PalworldSettingValueType.Integer
                ? Math.Round(e.NewValue).ToString(
                    "0",
                    CultureInfo.InvariantCulture)
                : e.NewValue.ToString(
                    "0.######",
                    CultureInfo.InvariantCulture);
        _syncingEditor = true;
        NumericEditorBox.Text = setting.PendingValue;
        _syncingEditor = false;
    }

    private void NumericEditorBox_TextChanged(
        object sender,
        TextChangedEventArgs e)
    {
        if (_syncingEditor || SettingsGrid.SelectedItem is not SettingRow setting)
        {
            return;
        }

        setting.PendingValue = NumericEditorBox.Text;
        if (double.TryParse(
                NumericEditorBox.Text,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var number))
        {
            _syncingEditor = true;
            NumericSlider.Value = Math.Clamp(
                number,
                NumericSlider.Minimum,
                NumericSlider.Maximum);
            _syncingEditor = false;
        }
    }

    private void TextEditorBox_TextChanged(
        object sender,
        TextChangedEventArgs e)
    {
        if (!_syncingEditor && SettingsGrid.SelectedItem is SettingRow setting)
        {
            setting.PendingValue = TextEditorBox.Text;
        }
    }

    private void BooleanEditor_Changed(object sender, RoutedEventArgs e)
    {
        if (!_syncingEditor && SettingsGrid.SelectedItem is SettingRow setting)
        {
            setting.PendingValue =
                (BooleanEditor.IsChecked == true).ToString().ToLowerInvariant();
        }
    }

    private void EnumEditor_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (!_syncingEditor &&
            SettingsGrid.SelectedItem is SettingRow setting &&
            EnumEditor.SelectedItem is string selected)
        {
            setting.PendingValue = selected;
        }
    }

    private void ResetSetting_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsGrid.SelectedItem is SettingRow setting)
        {
            setting.PendingValue = setting.DefaultValue;
            RenderSelectedEditor();
        }
    }

    private void UndoSetting_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsGrid.SelectedItem is SettingRow setting)
        {
            setting.PendingValue = setting.CurrentValue;
            RenderSelectedEditor();
        }
    }

    private void ResetCategory_Click(object sender, RoutedEventArgs e)
    {
        var category = CategoryList.SelectedItem?.ToString();
        foreach (var setting in _settings.Where(item =>
                     category == "All" || item.Category == category))
        {
            setting.PendingValue = setting.DefaultValue;
        }
    }

    private void ResetAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var setting in _settings)
        {
            setting.PendingValue = setting.DefaultValue;
        }
    }

    private void Compare_Click(object sender, RoutedEventArgs e)
    {
        var changes = GetChanges();
        var text = changes.Count == 0
            ? "No pending changes."
            : string.Join(
                Environment.NewLine,
                changes.Select(change =>
                {
                    var row = _settings.First(item => item.Name == change.Key);
                    return $"{row.DisplayName}: {row.CurrentValue} → {change.Value}";
                }));
        MessageBox.Show(
            text,
            "Current vs Pending",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "JSON settings (*.json)|*.json",
            FileName = "Palworld-World-Settings.json"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var values = _settings
            .Where(setting =>
                setting.Name != "ServerPassword" ||
                setting.PendingValue != "(configured)")
            .ToDictionary(
                setting => setting.Name,
                setting => setting.PendingValue,
                StringComparer.OrdinalIgnoreCase);
        await File.WriteAllTextAsync(
            dialog.FileName,
            JsonSerializer.Serialize(
                values,
                new JsonSerializerOptions { WriteIndented = true }));
        StatusText.Text = $"Settings exported to {dialog.FileName}.";
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "JSON settings (*.json)|*.json"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var values = JsonSerializer.Deserialize<Dictionary<string, string>>(
                await File.ReadAllTextAsync(dialog.FileName)) ??
                throw new InvalidDataException("The import file is empty.");
            foreach (var (name, value) in values)
            {
                var setting = _settings.FirstOrDefault(item =>
                    item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (setting is not null)
                {
                    setting.PendingValue = value;
                }
            }

            StatusText.Text =
                "Imported settings are pending. Review them before saving.";
        }
        catch (Exception exception) when (
            exception is IOException or JsonException)
        {
            StatusText.Text = $"Import failed: {exception.Message}";
        }
    }

    private void OpenRawFile_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_configurationPath) ||
            !File.Exists(_configurationPath))
        {
            StatusText.Text = "The live Palworld configuration file is unavailable.";
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = _configurationPath,
            UseShellExecute = true
        });
        StatusText.Text =
            "Opened the live raw file. Save through this editor for validation, checkpoints, and recovery protection.";
    }

    private async void SaveNextRestart_Click(
        object sender,
        RoutedEventArgs e) =>
        _ = await SaveAsync(false);

    private async void ApplyRestart_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (MessageBox.Show(
                "The world will be saved through the local REST API before Palworld restarts. Continue?",
                "Apply Palworld World Settings",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) == MessageBoxResult.Yes)
        {
            _ = await SaveAsync(true);
        }
    }

    private async Task<bool> SaveAsync(bool restart)
    {
        if (_serverId is null)
        {
            return false;
        }

        var changes = GetChanges();
        if (changes.Count == 0)
        {
            StatusText.Text = "There are no pending changes.";
            return true;
        }

        StatusText.Text = restart
            ? "Saving the world, applying settings, and restarting..."
            : "Saving validated settings for the next restart...";
        try
        {
            using var response = await _httpClient.PostAsJsonAsync(
                $"/api/v1/servers/{_serverId}/palworld/world-settings",
                new PalworldWorldSettingsUpdateRequest(
                    changes,
                    restart,
                    restart,
                    restart ? 10 : 0,
                    restart
                        ? "Server settings will be applied in 10 seconds."
                        : null));
            var result =
                await response.Content.ReadFromJsonAsync<PalworldWorldSettingsUpdateResponse>();
            StatusText.Text = result?.Message ??
                              await response.Content.ReadAsStringAsync();
            if (result?.Success == true)
            {
                await LoadAsync();
                return true;
            }

            return false;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException)
        {
            StatusText.Text = $"Settings operation failed: {exception.Message}";
            return false;
        }
    }

    private Dictionary<string, string> GetChanges() =>
        _settings
            .Where(setting =>
                !setting.PendingValue.Equals(
                    setting.CurrentValue,
                    StringComparison.Ordinal) &&
                !(setting.Name == "ServerPassword" &&
                  setting.PendingValue == "(configured)"))
            .ToDictionary(
                setting => setting.Name,
                setting => setting.PendingValue,
                StringComparer.OrdinalIgnoreCase);

    private void RenderUnsaved()
    {
        var count = GetChanges().Count;
        UnsavedText.Text = count == 0
            ? "No unsaved changes."
            : $"{count} unsaved setting change(s).";
        UnsavedStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class SettingRow(
        PalworldWorldSettingDescriptor descriptor) : INotifyPropertyChanged
    {
        private string _pendingValue = descriptor.CurrentValue;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Name => descriptor.Name;
        public string Category => descriptor.Category;
        public string DisplayName => descriptor.DisplayName;
        public string ArabicDisplayName => descriptor.ArabicDisplayName;
        public PalworldSettingValueType ValueType => descriptor.ValueType;
        public string CurrentValue => descriptor.CurrentValue;
        public string DefaultValue => descriptor.DefaultValue;
        public string Unit => descriptor.Unit;
        public string Description => descriptor.Description;
        public string ArabicDescription => descriptor.ArabicDescription;
        public double? Minimum => descriptor.Minimum;
        public double? Maximum => descriptor.Maximum;
        public IReadOnlyList<string> AllowedValues => descriptor.AllowedValues;
        public bool RestartRequired => descriptor.RestartRequired;
        public string? PerformanceWarning => descriptor.PerformanceWarning;

        public string PendingValue
        {
            get => _pendingValue;
            set
            {
                if (_pendingValue == value)
                {
                    return;
                }

                _pendingValue = value;
                PropertyChanged?.Invoke(
                    this,
                    new PropertyChangedEventArgs(nameof(PendingValue)));
            }
        }
    }
}
