using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using ServerManager.Client.Shell;
using ServerManager.Contracts;
using ServerManager.Core;

namespace ServerManager.Client;

public partial class ServerSoftwareWindow : Window
{
    private readonly Guid _serverId;
    private readonly string _serverName;
    private readonly ServerPlatform? _preferred;
    private readonly HttpClient _http = AgentTransportDefaults.CreateLoopbackHttpClient(TimeSpan.FromMinutes(30));
    private MinecraftSoftwareStatus? _status;
    private bool _busy;
    public bool MigrationSucceeded { get; private set; }

    public ServerSoftwareWindow(Guid serverId, string serverName, ServerPlatform? preferred = null)
    {
        _serverId = serverId;
        _serverName = serverName;
        _preferred = preferred;
        InitializeComponent();
        ApplyLanguage();
        Loaded += async (_, _) => await RefreshAsync();
        LocalizationService.LanguageChanged += LanguageChanged;
        Closing += (_, e) => e.Cancel = _busy;
        Closed += (_, _) => { LocalizationService.LanguageChanged -= LanguageChanged; _http.Dispose(); };
    }

    public static bool Open(Window owner, Guid serverId, string serverName, ServerPlatform? preferred = null)
    {
        var window = new ServerSoftwareWindow(serverId, serverName, preferred) { Owner = owner };
        window.ShowDialog();
        return window.MigrationSucceeded;
    }

    private static bool Arabic => CultureInfo.CurrentUICulture.TextInfo.IsRightToLeft;
    private static string Text(string english, string arabic) => Arabic ? arabic : english;
    private void LanguageChanged(object? sender, EventArgs e) => ApplyLanguage();
    private void ApplyLanguage()
    {
        FlowDirection = Arabic ? System.Windows.FlowDirection.RightToLeft : System.Windows.FlowDirection.LeftToRight;
        Title = Heading.Text = Text("Server Software", "برنامج الخادم");
        SafetyText.Text = Text(
            "Software changes keep the exact Minecraft version. Incompatible changes delete the current world and start a fresh world, only after the server stops and you confirm. Server settings, moderation lists and existing backups are preserved.",
            "يحافظ تغيير البرنامج على إصدار Minecraft نفسه. التغييرات غير المتوافقة تحذف العالم الحالي وتنشئ عالمًا جديدًا، فقط بعد إيقاف الخادم وموافقتك. تُحفظ إعدادات الخادم وقوائم الإدارة والنسخ الاحتياطية الموجودة.");
        RefreshButton.Content = Text("Refresh", "تحديث");
        MigrateButton.Content = Text("Change software", "تغيير البرنامج");
        CloseButton.Content = Text("Close", "إغلاق");
        Render();
    }

    private void Render()
    {
        if (_status is null) return;
        CurrentText.Text = $"{_serverName} — {_status.CurrentPlatform} — Minecraft {_status.MinecraftVersion ?? "?"}\n" +
            Text("World: ", "العالم: ") + _status.LevelName;
        var selected = (OptionsList.SelectedItem as OptionView)?.Option.Platform ?? _preferred;
        OptionsList.ItemsSource = _status.Options.Select(option => new OptionView(option, Explain(option))).ToArray();
        OptionsList.SelectedItem = OptionsList.Items.Cast<OptionView>().FirstOrDefault(item => item.Option.Platform == selected);
        UpdateButtons();
    }

    private static string Explain(MinecraftSoftwareOption option)
    {
        if (!Arabic) return option.Message;
        return option.Code switch
        {
            "WorldResetRequired" => "متاح للإصدار نفسه. يتطلب حذف العالم الحالي وإنشاء عالم جديد بعد موافقتك الصريحة.",
            "Ready" => "متاح للإصدار نفسه مع نسخة احتياطية موثقة وتغيير برنامج التشغيل فقط.",
            "AlreadyInstalled" => "هذا البرنامج مثبت حاليًا.",
            "WorldLayoutChangeRequired" => "يتطلب هذا الانتقال نقل بيانات العالم أو قواعد اللعب. تم منعه للحفاظ على المسارات دون أي تغيير.",
            "WorldLayoutUnverified" => "تعذر التحقق من تخطيط العالم الحالي؛ التغيير ممنوع لحماية البيانات.",
            "FoliaCompatibilityNotVerified" => "يتطلب Folia التحقق من توافق الإضافات ونظام الخيوط؛ التغيير التلقائي غير متاح.",
            "SpigotBuildToolsRequired" => "يتطلب Spigot أداة BuildTools الرسمية؛ لا يتوفر ملف تشغيل رسمي جاهز للتنزيل التلقائي.",
            "ExactVersionUnavailable" => "لا يتوفر إصدار رسمي مطابق لإصدار Minecraft الحالي؛ لن يتم الرجوع لإصدار أقدم.",
            "ProviderUnavailable" => "تعذر الوصول للمزود الرسمي؛ التوفر غير معروف.",
            "CurrentSoftwareUnknown" => "تعذر التعرف على ملف تشغيل الخادم الحالي بأمان.",
            "InterruptedMigration" => "يوجد تغيير سابق غير مكتمل يتطلب استعادة برنامج التشغيل والإعدادات دون إرجاع العالم.",
            _ => "هذا الانتقال غير مدعوم بأمان."
        };
    }

    private async Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true;
        UpdateButtons();
        StatusText.Text = Text("Checking current runtime and exact-version availability…", "جارٍ التحقق من البرنامج الحالي وتوفر الإصدار المطابق…");
        try
        {
            _status = await _http.GetFromJsonAsync<MinecraftSoftwareStatus>($"/api/v1/servers/{_serverId}/minecraft/software");
            StatusText.Text = "";
            Render();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        { StatusText.Text = Text("Unable to verify software availability. No changes were made.", "تعذر التحقق من توفر البرنامج. لم يتم إجراء أي تغيير."); }
        finally { _busy = false; UpdateButtons(); }
    }

    private async void MigrateClicked(object sender, RoutedEventArgs e)
    {
        if (_busy || OptionsList.SelectedItem is not OptionView { Option.Available: true } selected) return;
        var reset = selected.Option.RequiresWorldReset;
        if (!reset && System.Windows.MessageBox.Show(this, Text(
                "Stop this server, create a protected backup, and change only its runtime? Minecraft version and world paths must remain unchanged. The server will be started briefly to verify the new runtime.",
                "هل تريد إيقاف هذا الخادم وإنشاء نسخة احتياطية محمية وتغيير برنامج التشغيل فقط؟ يبقى إصدار Minecraft ومسارات العالم كما هي. سيُشغّل الخادم للتحقق من البرنامج الجديد."),
                Title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _busy = true;
        UpdateButtons();
        try
        {
            if (reset)
            {
                StatusText.Text = Text("Stopping the server safely before confirmation…", "جارٍ إيقاف الخادم بأمان قبل طلب الموافقة…");
                using var prepare = await _http.PostAsJsonAsync($"/api/v1/servers/{_serverId}/minecraft/software/prepare", new { });
                prepare.EnsureSuccessStatusCode();
                var stopped = await prepare.Content.ReadFromJsonAsync<MinecraftSoftwareMigrationResult>();
                if (stopped?.Success != true)
                {
                    StatusText.Text = Text("The server could not be stopped safely. No world was deleted. ", "تعذر إيقاف الخادم بأمان. لم يُحذف أي عالم. ") + stopped?.Code;
                    return;
                }
                if (System.Windows.MessageBox.Show(this, Text(
                    "Changing server software will delete the current world and create a new world.\n\nThe server is stopped. Server settings, whitelist, ops, bans and existing backups will be preserved. World deletion cannot be undone automatically.\n\nDelete the current world and change software?",
                    "سيؤدي تغيير برنامج الخادم إلى حذف العالم الحالي وإنشاء عالم جديد.\n\nالخادم متوقف. ستُحفظ إعدادات الخادم والقائمة البيضاء والمشرفون والحظر والنسخ الاحتياطية الموجودة. لا يمكن التراجع تلقائيًا عن حذف العالم.\n\nهل تريد حذف العالم الحالي وتغيير البرنامج؟"),
                    Title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                {
                    StatusText.Text = Text("Canceled. The server remains stopped; the world and runtime were not changed.", "تم الإلغاء. يبقى الخادم متوقفًا؛ لم يتغير العالم أو برنامج التشغيل.");
                    return;
                }
            }
            StatusText.Text = reset
                ? Text("Installing the verified runtime and creating a fresh world…", "جارٍ تثبيت البرنامج الموثق وإنشاء عالم جديد…")
                : Text("Verified backup → staged runtime → startup verification. Please wait…", "نسخة احتياطية موثقة ← تجهيز البرنامج ← التحقق من التشغيل. يرجى الانتظار…");
            using var response = await _http.PostAsJsonAsync($"/api/v1/servers/{_serverId}/minecraft/software",
                new MinecraftSoftwareMigrationRequest(selected.Option.Platform, selected.Option.MinecraftVersion, ConfirmWorldDeletion: reset));
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<MinecraftSoftwareMigrationResult>();
            MigrationSucceeded = result?.Success == true && result.StartupVerified;
            StatusText.Text = MigrationSucceeded
                ? reset
                    ? Text("Software changed and a fresh world started. The previous world was deleted; existing backups remain available for manual recovery. You can return to the same plugin.", "تم تغيير البرنامج وتشغيل عالم جديد. حُذف العالم السابق؛ تبقى النسخ الاحتياطية الموجودة متاحة للاستعادة اليدوية. يمكنك العودة إلى الإضافة نفسها.")
                    : Text("Software changed and startup verified. You can return to the same plugin and continue installation.", "تم تغيير البرنامج والتحقق من تشغيله. يمكنك العودة إلى الإضافة نفسها ومتابعة تثبيتها.")
                : Text("Migration did not complete. ", "لم يكتمل التغيير. ") + result?.Code + " — " + result?.Message;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        { StatusText.Text = Text("The Agent response was interrupted. Verify server state before retrying; do not assume migration succeeded.", "انقطع رد الوكيل. تحقق من حالة الخادم قبل إعادة المحاولة ولا تفترض نجاح التغيير."); }
        finally { _busy = false; UpdateButtons(); }
    }

    private void UpdateButtons()
    {
        if (MigrateButton is null) return;
        MigrateButton.IsEnabled = !_busy && !MigrationSucceeded && OptionsList.SelectedItem is OptionView { Option.Available: true };
        RefreshButton.IsEnabled = !_busy;
        CloseButton.IsEnabled = !_busy;
        OptionsList.IsEnabled = !_busy;
    }
    private void SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => UpdateButtons();
    private async void RefreshClicked(object sender, RoutedEventArgs e) => await RefreshAsync();
    private void CloseClicked(object sender, RoutedEventArgs e) { if (!_busy) Close(); }
    private sealed record OptionView(MinecraftSoftwareOption Option, string Description) { public string Platform => Option.Platform.ToString(); }
}
