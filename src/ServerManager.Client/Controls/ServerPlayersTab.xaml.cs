using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using System.Windows.Threading;
using ServerManager.Client.Shell;
using ServerManager.Contracts;
using ServerManager.Core.Minecraft;
using Button = System.Windows.Controls.Button;
using UserControl = System.Windows.Controls.UserControl;
using static ServerManager.Client.Controls.MinecraftPlayersPresentation;

namespace ServerManager.Client.Controls;

public partial class ServerPlayersTab : UserControl
{
    private readonly ServerDetailContext _context = ServerDetailContext.Shared;
    private readonly HttpClient _http = ServerDetailContext.Shared.CreateClient(TimeSpan.FromSeconds(20));
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(10) };
    private MinecraftPlayerDashboardSnapshot? _snapshot;
    private Guid _serverId;
    private bool _loading, _acting, _rendering;
    private CancellationTokenSource? _request;
    private sealed record Row(MinecraftPlayerProfile Player)
    {
        public string Name => Player.Username ?? Text("Unknown name", "اسم غير معروف");
        public string Uuid => Player.Uuid.ToString("D");
        public string Status => Online(Player.IsOnline);
    }

    public ServerPlayersTab()
    {
        InitializeComponent();
        Localize();
        _timer.Tick += async (_, _) => await LoadAsync();
        IsVisibleChanged += async (_, _) =>
        {
            if (IsVisible) { _timer.Start(); await LoadAsync(); }
            else { _timer.Stop(); _request?.Cancel(); }
        };
        Unloaded += (_, _) => { _timer.Stop(); _request?.Cancel(); };
        _context.Changed += (_, _) => Dispatcher.Invoke(() =>
        {
            if (_serverId != _context.ServerId)
            {
                _request?.Cancel(); _serverId = _context.ServerId;
                _snapshot = null; ActionResult.Text = string.Empty; RenderRows();
                if (IsVisible) _ = LoadAsync();
            }
        });
        LocalizationService.LanguageChanged += (_, _) => Dispatcher.Invoke(Localize);
    }

    private void Localize()
    {
        if (Heading is null) return;
        FlowDirection = LayoutDirectionService.ForCulture(CultureInfo.CurrentUICulture);
        _rendering = true;
        var filter = Math.Max(0, FilterBox.SelectedIndex); var sort = Math.Max(0, SortBox.SelectedIndex);
        Heading.Text = Text("Players", "اللاعبون"); SearchLabel.Text = Text("Search name or UUID", "ابحث بالاسم أو UUID");
        FilterLabel.Text = Text("Show", "عرض"); SortLabel.Text = Text("Sort", "ترتيب");
        FilterBox.ItemsSource = new[] { Text("All", "الكل"), Text("Online", "المتصلون"), Text("Offline", "غير المتصلين"), Text("Operators", "المشرفون"), Text("Whitelisted", "القائمة البيضاء"), Text("Banned", "المحظورون") };
        SortBox.ItemsSource = new[] { Text("Online first", "المتصلون أولاً"), Text("Name", "الاسم"), Text("Last seen", "آخر ظهور"), Text("First joined", "أول انضمام") };
        FilterBox.SelectedIndex = filter; SortBox.SelectedIndex = sort;
        RefreshButton.Content = Text("Refresh", "تحديث"); CopyButton.Content = Text("Copy UUID", "نسخ UUID");
        InventoryButton.Content = Text("View inventory", "عرض المخزون"); AdminHeading.Text = Text("Player administration", "إدارة اللاعب");
        ActionPanel.Children.Clear();
        foreach (var action in Enum.GetValues<MinecraftPlayerAction>())
        {
            var button = new Button { Content = MinecraftGameplayPresentation.ActionLabel(action), Tag = action, Margin = new Thickness(0, 0, 8, 8) };
            button.Click += Action_Click; ActionPanel.Children.Add(button);
        }
        _rendering = false; RenderRows();
    }

    private async Task LoadAsync()
    {
        if (_loading || !IsVisible || _context.Source?.Game != GameType.Minecraft) return;
        _loading = true; RefreshButton.IsEnabled = false;
        var serverId = _context.ServerId; _serverId = serverId;
        using var request = new CancellationTokenSource(); _request = request;
        try
        {
            var snapshot = await _http.GetFromJsonAsync<MinecraftPlayerDashboardSnapshot>(
                $"/api/v1/servers/{serverId:D}/minecraft/players/dashboard", request.Token);
            if (serverId != _context.ServerId || request.IsCancellationRequested) return;
            if (snapshot is null) throw new HttpRequestException();
            _snapshot = snapshot; RenderRows();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            if (!request.IsCancellationRequested && serverId == _context.ServerId)
            {
                if (_snapshot is not null) _snapshot = _snapshot with { IsStale = true };
                RenderRows();
                StatusText.Text = Text("Agent unavailable. Last verified values are retained; refresh to retry.", "الوكيل غير متاح. تم الاحتفاظ بآخر قيم موثوقة؛ حدّث للمحاولة مجدداً.");
            }
        }
        finally
        {
            _request = null; _loading = false; RefreshButton.IsEnabled = true;
            if (serverId != _context.ServerId && IsVisible) _ = LoadAsync();
        }
    }

    private void Filter_Changed(object sender, RoutedEventArgs e) { if (!_rendering) RenderRows(); }
    private void RenderRows()
    {
        if (PlayerList is null || _rendering) return;
        var selected = (PlayerList.SelectedItem as Row)?.Player.Uuid;
        var rows = Select(_snapshot?.Players ?? [], SearchBox.Text, FilterBox.SelectedIndex, SortBox.SelectedIndex).Select(p => new Row(p)).ToArray();
        PlayerList.ItemsSource = rows;
        PlayerList.SelectedItem = rows.FirstOrDefault(r => r.Player.Uuid == selected) ?? rows.FirstOrDefault();
        EmptyText.Text = Text("No known players match. Names and history appear only from verified server data.", "لا يوجد لاعبون مطابقون. تظهر الأسماء والسجلات من بيانات الخادم الموثوقة فقط.");
        EmptyText.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        CountText.Text = Count(_snapshot?.OnlinePlayers, _snapshot?.MaxPlayers, _snapshot?.IsStale ?? true);
        StatusText.Text = _snapshot?.Control == MinecraftLiveControl.NoConsole
            ? Text("Re-adopted server: counts use the Minecraft status protocol; live moderation is unavailable without the original console. The server will not be restarted.", "خادم مُعاد اعتماده: الأعداد من بروتوكول حالة ماينكرافت؛ الإدارة المباشرة غير متاحة دون وحدة التحكم الأصلية. لن تتم إعادة تشغيل الخادم.")
            : Text("UUID-based history for this server. — means unavailable; first joined is the earliest verified observation.", "سجل بمعرّف UUID لهذا الخادم. — تعني غير متاح؛ أول انضمام هو أقدم ظهور موثوق.");
        if (_snapshot is { OnlineIdentitiesKnown: false })
            StatusText.Text += " " + Text("Online names may be only a partial sample; others remain unknown.", "قد تكون الأسماء المتصلة عينة جزئية فقط؛ حالة الآخرين غير معروفة.");
        if (_snapshot?.LastVerifiedAtUtc is { } verified)
            StatusText.Text += " " + Text("Verified: ", "تم التحقق: ") + verified.ToLocalTime().ToString("g", CultureInfo.CurrentUICulture);
        RenderPlayer();
    }

    private void Player_Selected(object sender, RoutedEventArgs e) => RenderPlayer();
    private void RenderPlayer()
    {
        var player = (PlayerList.SelectedItem as Row)?.Player;
        CopyButton.IsEnabled = InventoryButton.IsEnabled = player is not null;
        ActionPanel.IsEnabled = player?.Username is not null && _snapshot?.Control == MinecraftLiveControl.Live && !_acting;
        PlayerName.Text = player?.Username ?? Text("Select a player", "اختر لاعباً");
        UuidText.Text = player?.Uuid.ToString("D") ?? string.Empty;
        if (player is null) { DetailsText.Text = string.Empty; return; }
        string Date(DateTimeOffset? value) => value?.ToLocalTime().ToString("g", CultureInfo.CurrentUICulture) ?? "—";
        string Duration(TimeSpan? value) => value is { } time ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}" : "—";
        DetailsText.Text = string.Join(Environment.NewLine,
            Online(player.IsOnline),
            Text("First joined: ", "أول انضمام: ") + Date(player.FirstJoinedAtUtc),
            Text("Last seen: ", "آخر ظهور: ") + Date(player.LastSeenAtUtc),
            Text("Session: ", "الجلسة: ") + Duration(player.IsOnline == true && player.SessionStartedAtUtc is { } start ? DateTimeOffset.UtcNow - start : null),
            Text("Total play time (last saved): ", "إجمالي اللعب (آخر حفظ): ") + Duration(player.TotalPlayTime),
            Text("Operator: ", "مشرف: ") + Known(player.IsOperator),
            Text("Whitelisted: ", "القائمة البيضاء: ") + Known(player.IsWhitelisted),
            Text("Banned: ", "محظور: ") + Known(player.IsBanned),
            Text("Game mode (last saved): ", "نمط اللعب (آخر حفظ): ") + (player.GameMode ?? "—"),
            Text("Ping: ", "زمن الاستجابة: ") + (player.PingMilliseconds?.ToString() ?? "—"),
            Text("Dimension (last saved): ", "البعد (آخر حفظ): ") + (player.Dimension ?? "—"));
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();
    private void Copy_Click(object sender, RoutedEventArgs e) { if (PlayerList.SelectedItem is Row row) SafeClipboard.TrySetText(row.Uuid); }
    private void Inventory_Click(object sender, RoutedEventArgs e)
    {
        if (PlayerList.SelectedItem is Row row) InventoryWindow.Open(Window.GetWindow(this), _serverId, row.Player.Uuid, row.Name);
    }
    private async void Action_Click(object sender, RoutedEventArgs e)
    {
        if (_acting || sender is not Button { Tag: MinecraftPlayerAction action } || PlayerList.SelectedItem is not Row row || _snapshot?.Control != MinecraftLiveControl.Live) return;
        var serverId = _serverId;
        if (MinecraftPlayerCommandPolicy.NeedsConfirmation(action) && !ConfirmationDialog.Confirm(Window.GetWindow(this),
            LocalizationService.Get("Gameplay.Confirm.Title"), LocalizationService.Format($"Gameplay.Confirm.{action}", row.Name),
            LocalizationService.Get("Gameplay.Confirm.Body"), null, MinecraftGameplayPresentation.ActionLabel(action))) return;
        _acting = true; RenderPlayer();
        try
        {
            using var response = await _http.PostAsJsonAsync($"/api/v1/servers/{serverId:D}/minecraft/players/administration",
                new MinecraftPlayerAdministrationRequest(row.Player.Uuid, action, Confirmed: true));
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<MinecraftChangeResult>();
            if (serverId != _context.ServerId) return;
            ActionResult.Text = result?.Outcome == MinecraftChangeOutcome.AppliedLive
                ? LocalizationService.Format("Gameplay.Players.Done", MinecraftGameplayPresentation.ActionLabel(action), row.Name)
                : MinecraftGameplayPresentation.ErrorText(result?.ErrorCode, result?.Message, row.Name);
            await LoadAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        { if (serverId == _context.ServerId) ActionResult.Text = Text("Action could not be verified. Refresh before retrying.", "تعذر التحقق من الإجراء. حدّث قبل المحاولة مجدداً."); }
        finally { _acting = false; RenderPlayer(); }
    }
}
