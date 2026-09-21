using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ServerManager.Client.Shell;
using ServerManager.Contracts;
using ServerManager.Core;
using AutomationProperties = System.Windows.Automation.AutomationProperties;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using UserControl = System.Windows.Controls.UserControl;

namespace ServerManager.Client.Controls;

/// <summary>
/// Server output and a command line. Clearing affects the display only — it never touches the
/// server's own logs — and destructive commands are confirmed before they are sent.
/// </summary>
public partial class ServerConsoleTab : UserControl
{
    private static readonly string[] DestructiveCommands =
    [
        "stop", "shutdown", "restart", "reload", "kill", "ban", "ban-ip", "deop",
        "whitelist off", "save-off", "kick", "doexit", "broadcast"
    ];

    private readonly ServerDetailContext _context = ServerDetailContext.Shared;
    private readonly HttpClient _httpClient;
    private readonly DispatcherTimer _timer;
    private readonly List<string> _history = [];
    private int _historyIndex = -1;
    private int _hiddenBefore;
    private bool _active;
    private bool _sending;

    public ServerConsoleTab()
    {
        InitializeComponent();
        _httpClient = _context.CreateClient(TimeSpan.FromSeconds(30));
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _timer.Tick += async (_, _) => await LoadAsync();
        _context.Changed += (_, _) => Dispatcher.Invoke(Localize);
        LocalizationService.LanguageChanged += (_, _) => Dispatcher.Invoke(Localize);
        ThemeService.ThemeChanged += (_, _) => Dispatcher.Invoke(Localize);
        Loaded += (_, _) => Localize();
        Unloaded += (_, _) => _timer.Stop();
    }

    /// <summary>Only poll while this tab is the one on screen.</summary>
    public void SetActive(bool active)
    {
        _active = active;
        if (active)
        {
            Localize();
            _timer.Start();
            _ = LoadAsync();
        }
        else
        {
            _timer.Stop();
        }
    }

    private void Localize()
    {
        if (CopyButton is null)
        {
            return;
        }

        CopyButton.Content = LocalizationService.Get("Action.Copy");
        ExportButton.Content = LocalizationService.Get("Action.Export");
        ClearButton.Content = LocalizationService.Get("Console.ClearDisplay");
        ClearButton.ToolTip = LocalizationService.Get("Console.ClearDisplayHint");
        FollowToggle.Content = LocalizationService.Get("Console.Follow");
        SendButton.Content = LocalizationService.Get("Console.Send");

        var label = LocalizationService.Get("Console.CommandLabel");
        AutomationProperties.SetName(CommandBox, label);
        CommandBox.ToolTip = label;
        AutomationProperties.SetName(
            OutputBox,
            LocalizationService.Get("Console.OutputLabel"));

        var canSend = _context.Actions.CanSendCommand && !_sending;
        CommandBox.IsEnabled = canSend;
        SendButton.IsEnabled = canSend;

        var connected = _context.Card?.Status == UiStatus.Running;
        ConnectionText.Text = LocalizationService.Get(
            connected ? "Console.Connected" : "Console.Disconnected");
        ConnectionDot.Fill = Resolve(connected ? "SuccessBrush" : "TextSecondaryBrush");
    }

    private static Brush Resolve(string key) =>
        Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;

    private async Task LoadAsync()
    {
        if (!_active || _context.Card is null)
        {
            return;
        }

        try
        {
            var entries = await _httpClient.GetFromJsonAsync<LogEntry[]>(
                $"/api/v1/servers/{_context.ServerId}/logs") ?? [];

            // _hiddenBefore is how many lines the person cleared from their view. The server
            // keeps them; we simply do not re-draw them.
            var visible = entries.Skip(_hiddenBefore).ToArray();
            var builder = new StringBuilder();
            foreach (var entry in visible)
            {
                builder.Append(entry.TimestampUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture));
                builder.Append("  ");
                builder.AppendLine(entry.Message);
            }

            var text = builder.ToString();
            if (!string.Equals(text, OutputBox.Text, StringComparison.Ordinal))
            {
                OutputBox.Text = text;
                if (FollowToggle.IsChecked == true)
                {
                    OutputScroller.ScrollToEnd();
                }
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A dropped poll is not worth a message here; the header already shows state.
        }

        Localize();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        var text = string.IsNullOrEmpty(OutputBox.SelectedText)
            ? OutputBox.Text
            : OutputBox.SelectedText;
        if (!string.IsNullOrWhiteSpace(text))
        {
            SafeClipboard.TrySetText(text);
        }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            FileName = $"{_context.Card?.Name ?? "server"}-console.txt",
            Filter = "Text file (*.txt)|*.txt"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            File.WriteAllText(dialog.FileName, OutputBox.Text, new UTF8Encoding(false));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            NotificationService.Publish(
                NotificationKind.Error,
                LocalizationService.Get("Action.Export"),
                exception.Message);
        }
    }

    /// <summary>Hides what is currently shown. The server's logs are untouched.</summary>
    private async void Clear_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var entries = await _httpClient.GetFromJsonAsync<LogEntry[]>(
                $"/api/v1/servers/{_context.ServerId}/logs") ?? [];
            _hiddenBefore = entries.Length;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // If the count cannot be read, clear the view anyway; it will refill on the
            // next poll rather than silently hiding new output.
        }

        OutputBox.Clear();
    }

    private void CommandBox_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                Send_Click(sender, e);
                break;
            case Key.Up when _history.Count > 0:
                _historyIndex = _historyIndex <= 0 ? _history.Count - 1 : _historyIndex - 1;
                CommandBox.Text = _history[_historyIndex];
                CommandBox.CaretIndex = CommandBox.Text.Length;
                e.Handled = true;
                break;
            case Key.Down when _history.Count > 0:
                _historyIndex = _historyIndex >= _history.Count - 1 ? 0 : _historyIndex + 1;
                CommandBox.Text = _history[_historyIndex];
                CommandBox.CaretIndex = CommandBox.Text.Length;
                e.Handled = true;
                break;
        }
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        var command = CommandBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(command) || _sending)
        {
            return;
        }

        if (!_context.Actions.CanSendCommand)
        {
            return;
        }

        if (IsDestructive(command) && !Confirm(command))
        {
            return;
        }

        _sending = true;
        Localize();
        try
        {
            var response = await _httpClient.PostAsJsonAsync(
                $"/api/v1/servers/{_context.ServerId}/console",
                new ConsoleCommandRequest(command));
            if (response.IsSuccessStatusCode)
            {
                _history.Add(command);
                _historyIndex = _history.Count;
                CommandBox.Clear();
            }
            else
            {
                NotificationService.Publish(
                    NotificationKind.Error,
                    LocalizationService.Get("Console.Send"),
                    ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture));
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            NotificationService.Publish(
                NotificationKind.Error,
                LocalizationService.Get("Console.Send"),
                exception.Message);
        }
        finally
        {
            _sending = false;
            Localize();
            await LoadAsync();
        }
    }

    /// <summary>
    /// Whether a command can disconnect players or stop the server, and therefore needs
    /// confirming first. Pure and public so the rule is testable without a window.
    /// </summary>
    public static bool IsDestructive(string command)
    {
        var text = command.Trim().TrimStart('/').ToLowerInvariant();
        return DestructiveCommands.Any(candidate =>
            text.Equals(candidate, StringComparison.Ordinal) ||
            text.StartsWith(candidate + " ", StringComparison.Ordinal));
    }

    private static bool Confirm(string command) =>
        MessageBox.Show(
            LocalizationService.Format("Confirm.Command", command),
            LocalizationService.Get("Console.Send"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes;
}
