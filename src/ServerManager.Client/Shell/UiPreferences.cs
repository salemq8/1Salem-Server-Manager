using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace ServerManager.Client.Shell;

public enum AppTheme
{
    Dark = 0,
    Light = 1,
    FollowWindows = 2
}

public sealed record UiPreferences(string Language, AppTheme Theme)
{
    public static UiPreferences Default { get; } = new("en-US", AppTheme.Dark);
}

public sealed class UiPreferencesStore
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;

    public UiPreferencesStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "1SalemServerManager",
            "ui-preferences.json"))
    {
    }

    internal UiPreferencesStore(string path) => _path = Path.GetFullPath(path);

    public UiPreferences Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<UiPreferences>(
                    File.ReadAllText(_path),
                    SerializerOptions) ?? UiPreferences.Default
                : UiPreferences.Default;
        }
        catch (JsonException)
        {
            return UiPreferences.Default;
        }
    }

    public void Save(UiPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        _ = CultureInfo.GetCultureInfo(preferences.Language);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporaryPath = _path + ".new";
        File.WriteAllText(
            temporaryPath,
            JsonSerializer.Serialize(preferences, SerializerOptions));
        File.Move(temporaryPath, _path, true);
    }
}

public static class ThemeService
{
    private static readonly IReadOnlyDictionary<string, string> DarkColors =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["WindowBrush"] = "#08131F",
            ["PanelBrush"] = "#0E1C2B",
            ["PanelAltBrush"] = "#16283A",
            ["AccentBrush"] = "#2CB8B3",
            ["SuccessBrush"] = "#43B581",
            ["WarningBrush"] = "#E0A83B",
            ["DangerBrush"] = "#D85B63",
            ["DisabledBrush"] = "#405064",
            ["TextBrush"] = "#F1F6FA",
            ["MutedTextBrush"] = "#9FB0C2",
            ["ControlSurfaceBrush"] = "#102235",
            ["ControlHoverBrush"] = "#193149",
            ["ControlBorderBrush"] = "#3B566D",
            ["FocusBrush"] = "#72E2DE",
            ["SelectionBrush"] = "#087F7B",
            ["TabBrush"] = "#16283A",
            ["TabDisabledBrush"] = "#202F40",
            ["DangerSurfaceBrush"] = "#351D27"
        };

    private static readonly IReadOnlyDictionary<string, string> LightColors =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["WindowBrush"] = "#F4F6F9",
            ["PanelBrush"] = "#FFFFFF",
            ["PanelAltBrush"] = "#E5EAF1",
            ["AccentBrush"] = "#087F7B",
            ["SuccessBrush"] = "#188454",
            ["WarningBrush"] = "#A96F00",
            ["DangerBrush"] = "#B62D3A",
            ["DisabledBrush"] = "#C7D0DB",
            ["TextBrush"] = "#16202D",
            ["MutedTextBrush"] = "#526173",
            ["ControlSurfaceBrush"] = "#FFFFFF",
            ["ControlHoverBrush"] = "#EDF2F7",
            ["ControlBorderBrush"] = "#91A0B3",
            ["FocusBrush"] = "#087F7B",
            ["SelectionBrush"] = "#087F7B",
            ["TabBrush"] = "#E5EAF1",
            ["TabDisabledBrush"] = "#DCE2EA",
            ["DangerSurfaceBrush"] = "#FBEAEC"
        };

    private static AppTheme _requestedTheme = AppTheme.Dark;
    private static bool _listeningForWindowsChanges;

    public static void Apply(AppTheme theme)
    {
        _requestedTheme = theme;
        EnsureWindowsThemeListener();
        var application = System.Windows.Application.Current;
        if (application is null)
        {
            return;
        }

        if (SystemParameters.HighContrast)
        {
            application.Resources["WindowBrush"] = System.Windows.SystemColors.WindowBrush;
            application.Resources["PanelBrush"] = System.Windows.SystemColors.ControlBrush;
            application.Resources["PanelAltBrush"] = System.Windows.SystemColors.ControlLightBrush;
            application.Resources["AccentBrush"] = System.Windows.SystemColors.HighlightBrush;
            application.Resources["SuccessBrush"] = System.Windows.SystemColors.HighlightBrush;
            application.Resources["WarningBrush"] = System.Windows.SystemColors.HighlightBrush;
            application.Resources["DangerBrush"] = System.Windows.SystemColors.HighlightBrush;
            application.Resources["DisabledBrush"] = System.Windows.SystemColors.GrayTextBrush;
            application.Resources["TextBrush"] = System.Windows.SystemColors.WindowTextBrush;
            application.Resources["MutedTextBrush"] = System.Windows.SystemColors.GrayTextBrush;
            application.Resources["ControlSurfaceBrush"] = System.Windows.SystemColors.WindowBrush;
            application.Resources["ControlHoverBrush"] = System.Windows.SystemColors.ControlLightBrush;
            application.Resources["ControlBorderBrush"] = System.Windows.SystemColors.WindowTextBrush;
            application.Resources["FocusBrush"] = System.Windows.SystemColors.HighlightBrush;
            application.Resources["SelectionBrush"] = System.Windows.SystemColors.HighlightBrush;
            application.Resources["TabBrush"] = System.Windows.SystemColors.ControlBrush;
            application.Resources["TabDisabledBrush"] = System.Windows.SystemColors.ControlDarkBrush;
            application.Resources["DangerSurfaceBrush"] = System.Windows.SystemColors.ControlBrush;
            return;
        }

        var effectiveTheme = theme == AppTheme.FollowWindows
            ? ReadWindowsTheme()
            : theme;
        var colors = effectiveTheme == AppTheme.Light ? LightColors : DarkColors;
        foreach (var (key, value) in colors)
        {
            if (application.Resources[key] is SolidColorBrush existing &&
                !existing.IsFrozen)
            {
                existing.Color = (System.Windows.Media.Color)
                    System.Windows.Media.ColorConverter.ConvertFromString(value);
            }
            else
            {
                application.Resources[key] =
                    new SolidColorBrush(
                        (System.Windows.Media.Color)
                        System.Windows.Media.ColorConverter.ConvertFromString(value));
            }
        }
    }

    internal static AppTheme ReadWindowsTheme()
    {
        try
        {
            var value = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme",
                0);
            return Convert.ToInt32(value, CultureInfo.InvariantCulture) == 1
                ? AppTheme.Light
                : AppTheme.Dark;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
            System.Security.SecurityException or FormatException or
            InvalidCastException)
        {
            return AppTheme.Dark;
        }
    }

    private static void EnsureWindowsThemeListener()
    {
        if (_listeningForWindowsChanges)
        {
            return;
        }

        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        _listeningForWindowsChanges = true;
    }

    private static void OnUserPreferenceChanged(
        object sender,
        UserPreferenceChangedEventArgs eventArgs)
    {
        if (_requestedTheme != AppTheme.FollowWindows ||
            eventArgs.Category is not (
                UserPreferenceCategory.Color or
                UserPreferenceCategory.General or
                UserPreferenceCategory.VisualStyle))
        {
            return;
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null)
        {
            _ = dispatcher.BeginInvoke(() => Apply(AppTheme.FollowWindows));
        }
    }
}

public static class LocalizationService
{
    private static readonly IReadOnlyDictionary<string, string> English =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AppTitle"] = "1Salem Server Manager",
            ["DashboardSubtitle"] = "Native server dashboard",
            ["AgentConnection"] = "Agent connection",
            ["Refresh"] = "Refresh",
            ["AgentPc"] = "Agent PC",
            ["AgentVersion"] = "Agent version",
            ["Database"] = "Database",
            ["FoundationTitle"] = "Server management foundation",
            ["FoundationDescription"] = "Local process control, verified backups, game installers, and resource safeguards are available from their sections.",
            ["Footer"] = "Server status refreshes automatically",
            ["Home"] = "Home",
            ["Home.Description"] = "Agent status and server overview.",
            ["Minecraft"] = "Minecraft",
            ["Minecraft.Description"] = "Vanilla Minecraft server management.",
            ["Palworld"] = "Palworld",
            ["Palworld.Description"] = "Vanilla Palworld server management.",
            ["RemoteAccess"] = "Remote Access",
            ["RemoteAccess.Description"] = "Official Playit access from outside your home.",
            ["Backups"] = "Backups",
            ["Backups.Description"] = "Backup history, schedules, and restore.",
            ["Updates"] = "Updates",
            ["Updates.Description"] = "Installed versions and update status.",
            ["Resources"] = "Resources",
            ["Resources.Description"] = "CPU, memory, and priority profiles.",
            ["System"] = "System",
            ["System.Description"] = "Performance, network, files, logs, and diagnostics.",
            ["Network"] = "Network",
            ["Network.Description"] = "Local addresses, ports, and firewall status.",
            ["Files"] = "Files",
            ["Files.Description"] = "Files inside registered server roots.",
            ["Logs"] = "Logs",
            ["Logs.Description"] = "Agent and game server logs.",
            ["Settings"] = "Settings",
            ["Settings.Description"] = "Application, startup, language, and theme.",
            ["About"] = "About",
            ["About.Description"] = "Version, diagnostics, and release information.",
            ["Appearance.Title"] = "Language and Theme",
            ["Appearance.Description"] = "Changes apply immediately and are saved for the next launch.",
            ["Appearance.Language"] = "Language",
            ["Appearance.Theme"] = "Theme",
            ["Appearance.English"] = "English",
            ["Appearance.Arabic"] = "العربية",
            ["Appearance.Dark"] = "Dark",
            ["Appearance.Light"] = "Light",
            ["Appearance.FollowWindows"] = "Follow Windows",
            ["Appearance.Apply"] = "Apply",
            ["Appearance.Applied"] = "Language and theme applied.",
            ["Appearance.ScopeDescription"] = "Dashboard appearance is separate from each server's Auto Start and Auto Restart settings.",
            ["Shell.Navigation"] = "☰ Navigation",
            ["Shell.ShowNavigation"] = "☰ Show Navigation",
            ["Shell.HideNavigation"] = "☰ Hide Navigation",
            ["Shell.AdministratorTools"] = "Administrator Tools",
            ["Shell.AdministratorMode"] = "Administrator mode",
            ["Shell.NormalMode"] = "Normal mode",
            ["Shell.Connected"] = "Connected locally",
            ["Shell.AgentUnavailable"] = "Agent unavailable",
            ["Shell.Footer"] = "Server status refreshes automatically",
            ["Shell.Dismiss"] = "Dismiss",
            ["Palworld.Overview.Title"] = "Live server overview",
            ["Palworld.Overview.Loading"] = "Loading…",
            ["Palworld.Overview.LoadingLiveData"] = "Loading live server data",
            ["Palworld.Overview.Unavailable"] = "Unavailable",
            ["Palworld.Overview.Stale"] = "Stale",
            ["Palworld.Overview.ServerStopped"] = "Server stopped",
            ["Palworld.Overview.RuntimeUnavailable"] = "Runtime unavailable",
            ["Palworld.Overview.LivePlayers"] = "Live player count",
            ["Palworld.Overview.LiveRestMetric"] = "Live REST metric",
            ["Palworld.Overview.ProcessUptime"] = "Process uptime",
            ["Palworld.Overview.LogicalProcessors"] = "{0} logical processors",
            ["Palworld.Overview.WarningAt"] = "Warning at {0}",
            ["Palworld.Overview.WorkingSet"] = "Process working set",
            ["Palworld.Overview.NoBackup"] = "No backup recorded",
            ["Palworld.Overview.PercentOfWarning"] = "{0:0}% of {1} warning threshold",
            ["Palworld.Overview.WarningUnavailable"] = "Warning threshold unavailable",
            ["Palworld.Overview.DiskFree"] = "{0} free",
            ["Palworld.Overview.DaysAgo"] = "{0}d ago",
            ["Palworld.Overview.HoursAgo"] = "{0}h ago",
            ["Palworld.Overview.MinutesAgo"] = "{0}m ago",
            ["Palworld.Overview.Status"] = "Status",
            ["Palworld.Overview.QuickActions"] = "Quick Actions",
            ["Palworld.Overview.ServerActivity"] = "Server Activity",
            ["Palworld.Overview.RecentActivity"] = "Recent Activity",
            ["Palworld.Overview.ResourceSummary"] = "Resource Summary",
            ["Palworld.Overview.Connections"] = "Connections",
            ["Palworld.Overview.LocalNetwork"] = "Local Network",
            ["Palworld.Overview.InternetPlayit"] = "Internet / Playit",
            ["Palworld.Overview.NoHistory"] = "History will appear as live samples arrive.",
            ["Palworld.Overview.NoActivity"] = "No recent server activity.",
            ["Palworld.Overview.TimeRange"] = "Time range",
            ["Palworld.Overview.Badge.ProcessRunning"] = "Process Running",
            ["Palworld.Overview.Badge.PortOpen"] = "Port Open",
            ["Palworld.Overview.Badge.GameReady"] = "Game Ready",
            ["Palworld.Overview.Badge.RestConnected"] = "REST Connected",
            ["Palworld.Overview.Badge.PlayitOnline"] = "Playit Online",
            ["Palworld.Overview.Badge.PublicMappingValid"] = "Public Mapping Valid",
            ["Palworld.Overview.Metric.Players"] = "Players",
            ["Palworld.Overview.Metric.Fps"] = "Server FPS",
            ["Palworld.Overview.Metric.Uptime"] = "Uptime",
            ["Palworld.Overview.Metric.Cpu"] = "CPU",
            ["Palworld.Overview.Metric.Ram"] = "RAM",
            ["Palworld.Overview.Metric.LastBackup"] = "Last Backup",
            ["Palworld.Overview.Action.Start"] = "Start Server",
            ["Palworld.Overview.Action.Refresh"] = "Refresh",
            ["Palworld.Overview.Action.SaveWorld"] = "Save World",
            ["Palworld.Overview.Action.Announcement"] = "Announcement",
            ["Palworld.Overview.Action.GracefulStop"] = "Graceful Stop",
            ["Palworld.Overview.Action.Restart"] = "Restart",
            ["Palworld.Overview.Action.BackupNow"] = "Backup Now",
            ["Palworld.Overview.Action.CopyInternet"] = "Copy Internet Address",
            ["Palworld.Overview.Action.Copy"] = "Copy",
            ["Palworld.Overview.Action.Send"] = "Send",
            ["Palworld.Overview.Action.Cancel"] = "Cancel",
            ["Palworld.Overview.AnnouncementHint"] = "Message to connected players",
            ["Palworld.Overview.Resource.Cpu"] = "CPU usage",
            ["Palworld.Overview.Resource.Ram"] = "RAM usage",
            ["Palworld.Overview.Resource.Disk"] = "Disk usage",
            ["Palworld.Overview.Activity.Source"] = "Source: {0}",
            ["Palworld.Overview.Activity.Succeeded"] = "Completed",
            ["Palworld.Overview.Activity.Failed"] = "Failed"
        };

    private static readonly IReadOnlyDictionary<string, string> Arabic =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AppTitle"] = "مدير خوادم 1Salem",
            ["DashboardSubtitle"] = "لوحة تحكم أصلية للخوادم",
            ["AgentConnection"] = "اتصال الوكيل",
            ["Refresh"] = "تحديث",
            ["AgentPc"] = "جهاز الوكيل",
            ["AgentVersion"] = "إصدار الوكيل",
            ["Database"] = "قاعدة البيانات",
            ["FoundationTitle"] = "أساس إدارة الخوادم",
            ["FoundationDescription"] = "تتوفر إدارة العمليات والنسخ الاحتياطية الموثقة وتثبيت الألعاب وحماية الموارد من أقسامها.",
            ["Footer"] = "اتصال محلي آمن | واجهة محلية | عمليات تحافظ على البيانات",
            ["Home"] = "الرئيسية",
            ["Home.Description"] = "حالة الوكيل ونظرة عامة على الخوادم.",
            ["Minecraft"] = "ماينكرافت",
            ["Minecraft.Description"] = "إدارة خادم ماينكرافت الأصلي.",
            ["Palworld"] = "بال وورلد",
            ["Palworld.Description"] = "إدارة خادم بال وورلد الأصلي.",
            ["RemoteAccess"] = "الوصول من خارج المنزل",
            ["RemoteAccess.Description"] = "إدارة الوصول الخارجي عبر وكيل Playit الرسمي.",
            ["Backups"] = "النسخ الاحتياطية",
            ["Backups.Description"] = "السجل والجدولة والاستعادة.",
            ["Updates"] = "التحديثات",
            ["Updates.Description"] = "الإصدارات المثبتة وحالة التحديث.",
            ["Resources"] = "الموارد",
            ["Resources.Description"] = "ملفات تعريف المعالج والذاكرة والأولوية.",
            ["Network"] = "الشبكة",
            ["Network.Description"] = "العناوين والمنافذ وحالة جدار الحماية.",
            ["Files"] = "الملفات",
            ["Files.Description"] = "الملفات داخل مجلدات الخوادم المسجلة.",
            ["Logs"] = "السجلات",
            ["Logs.Description"] = "سجلات الوكيل وخوادم الألعاب.",
            ["Settings"] = "الإعدادات",
            ["Settings.Description"] = "التطبيق وبدء التشغيل واللغة والمظهر.",
            ["About"] = "حول",
            ["About.Description"] = "الإصدار والتشخيصات ومعلومات الإصدار.",
            ["Appearance.Title"] = "اللغة والمظهر",
            ["Appearance.Description"] = "تُطبَّق التغييرات فورًا وتُحفظ للتشغيل التالي.",
            ["Appearance.Language"] = "اللغة",
            ["Appearance.Theme"] = "المظهر",
            ["Appearance.English"] = "English",
            ["Appearance.Arabic"] = "العربية",
            ["Appearance.Dark"] = "داكن",
            ["Appearance.Light"] = "فاتح",
            ["Appearance.FollowWindows"] = "اتباع إعداد Windows",
            ["Appearance.Apply"] = "تطبيق",
            ["Appearance.Applied"] = "تم تطبيق اللغة والمظهر.",
            ["Appearance.ScopeDescription"] = "مظهر لوحة التحكم منفصل عن إعدادات البدء وإعادة التشغيل التلقائي لكل خادم.",
            ["Shell.Navigation"] = "☰ التنقل",
            ["Shell.ShowNavigation"] = "☰ إظهار التنقل",
            ["Shell.HideNavigation"] = "☰ إخفاء التنقل",
            ["Shell.AdministratorTools"] = "أدوات المسؤول",
            ["Shell.AdministratorMode"] = "وضع المسؤول",
            ["Shell.NormalMode"] = "الوضع العادي",
            ["Shell.Connected"] = "متصل محليًا",
            ["Shell.AgentUnavailable"] = "الوكيل غير متاح",
            ["Shell.Footer"] = "يتم تحديث حالة الخادم تلقائيًا",
            ["Shell.Dismiss"] = "إغلاق",
            ["Palworld.Overview.Title"] = "نظرة عامة مباشرة على الخادم",
            ["Palworld.Overview.Loading"] = "جارٍ التحميل…",
            ["Palworld.Overview.LoadingLiveData"] = "جارٍ تحميل بيانات الخادم المباشرة",
            ["Palworld.Overview.Unavailable"] = "غير متاح",
            ["Palworld.Overview.Stale"] = "بيانات قديمة",
            ["Palworld.Overview.ServerStopped"] = "الخادم متوقف",
            ["Palworld.Overview.RuntimeUnavailable"] = "بيئة التشغيل غير متاحة",
            ["Palworld.Overview.LivePlayers"] = "عدد اللاعبين المباشر",
            ["Palworld.Overview.LiveRestMetric"] = "مقياس REST مباشر",
            ["Palworld.Overview.ProcessUptime"] = "مدة تشغيل العملية",
            ["Palworld.Overview.LogicalProcessors"] = "{0} معالجات منطقية",
            ["Palworld.Overview.WarningAt"] = "تحذير عند {0}",
            ["Palworld.Overview.WorkingSet"] = "ذاكرة العملية المستخدمة",
            ["Palworld.Overview.NoBackup"] = "لا توجد نسخة احتياطية مسجلة",
            ["Palworld.Overview.PercentOfWarning"] = "{0:0}% من حد التحذير {1}",
            ["Palworld.Overview.WarningUnavailable"] = "حد تحذير الذاكرة غير متاح",
            ["Palworld.Overview.DiskFree"] = "{0} متاح",
            ["Palworld.Overview.DaysAgo"] = "منذ {0} يوم",
            ["Palworld.Overview.HoursAgo"] = "منذ {0} ساعة",
            ["Palworld.Overview.MinutesAgo"] = "منذ {0} دقيقة",
            ["Palworld.Overview.Status"] = "الحالة",
            ["Palworld.Overview.QuickActions"] = "إجراءات سريعة",
            ["Palworld.Overview.ServerActivity"] = "نشاط الخادم",
            ["Palworld.Overview.RecentActivity"] = "النشاط الأخير",
            ["Palworld.Overview.ResourceSummary"] = "ملخص الموارد",
            ["Palworld.Overview.Connections"] = "الاتصالات",
            ["Palworld.Overview.LocalNetwork"] = "الشبكة المحلية",
            ["Palworld.Overview.InternetPlayit"] = "الإنترنت / Playit",
            ["Palworld.Overview.NoHistory"] = "سيظهر السجل عند وصول العينات المباشرة.",
            ["Palworld.Overview.NoActivity"] = "لا يوجد نشاط حديث للخادم.",
            ["Palworld.Overview.TimeRange"] = "النطاق الزمني",
            ["Palworld.Overview.Badge.ProcessRunning"] = "العملية تعمل",
            ["Palworld.Overview.Badge.PortOpen"] = "المنفذ مفتوح",
            ["Palworld.Overview.Badge.GameReady"] = "اللعبة جاهزة",
            ["Palworld.Overview.Badge.RestConnected"] = "REST متصل",
            ["Palworld.Overview.Badge.PlayitOnline"] = "Playit متصل",
            ["Palworld.Overview.Badge.PublicMappingValid"] = "التوجيه العام صالح",
            ["Palworld.Overview.Metric.Players"] = "اللاعبون",
            ["Palworld.Overview.Metric.Fps"] = "إطارات الخادم",
            ["Palworld.Overview.Metric.Uptime"] = "مدة التشغيل",
            ["Palworld.Overview.Metric.Cpu"] = "المعالج",
            ["Palworld.Overview.Metric.Ram"] = "الذاكرة",
            ["Palworld.Overview.Metric.LastBackup"] = "آخر نسخة",
            ["Palworld.Overview.Action.Start"] = "تشغيل الخادم",
            ["Palworld.Overview.Action.Refresh"] = "تحديث",
            ["Palworld.Overview.Action.SaveWorld"] = "حفظ العالم",
            ["Palworld.Overview.Action.Announcement"] = "إعلان",
            ["Palworld.Overview.Action.GracefulStop"] = "إيقاف آمن",
            ["Palworld.Overview.Action.Restart"] = "إعادة التشغيل",
            ["Palworld.Overview.Action.BackupNow"] = "نسخ احتياطي الآن",
            ["Palworld.Overview.Action.CopyInternet"] = "نسخ عنوان الإنترنت",
            ["Palworld.Overview.Action.Copy"] = "نسخ",
            ["Palworld.Overview.Action.Send"] = "إرسال",
            ["Palworld.Overview.Action.Cancel"] = "إلغاء",
            ["Palworld.Overview.AnnouncementHint"] = "رسالة للاعبين المتصلين",
            ["Palworld.Overview.Resource.Cpu"] = "استخدام المعالج",
            ["Palworld.Overview.Resource.Ram"] = "استخدام الذاكرة",
            ["Palworld.Overview.Resource.Disk"] = "استخدام القرص",
            ["Palworld.Overview.Activity.Source"] = "المصدر: {0}",
            ["Palworld.Overview.Activity.Succeeded"] = "اكتمل",
            ["Palworld.Overview.Activity.Failed"] = "فشل"
        };

    public static string Get(string key)
    {
        var isRightToLeft =
            CultureInfo.CurrentUICulture.TextInfo.IsRightToLeft;
        if (isRightToLeft && key == "System")
        {
            return "النظام";
        }

        if (isRightToLeft && key == "System.Description")
        {
            return "الأداء والشبكة والملفات والسجلات والتشخيصات.";
        }

        var values = isRightToLeft
            ? Arabic
            : English;
        return values.TryGetValue(key, out var value) ? value : key;
    }

    public static void Apply(string language)
    {
        var culture = CultureInfo.GetCultureInfo(language);
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        var application = System.Windows.Application.Current;
        if (application is null)
        {
            return;
        }

        foreach (var key in English.Keys)
        {
            application.Resources[key] = Get(key);
        }
    }
}
