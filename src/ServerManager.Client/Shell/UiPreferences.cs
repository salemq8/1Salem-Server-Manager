using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace ServerManager.Client.Shell;

public enum AppTheme
{
    Dark = 0,
    Light = 1
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
            ["MutedTextBrush"] = "#9FB0C2"
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
            ["MutedTextBrush"] = "#526173"
        };

    public static void Apply(AppTheme theme)
    {
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
            return;
        }

        var colors = theme == AppTheme.Light ? LightColors : DarkColors;
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
            ["Footer"] = "Local named-pipe transport | Loopback API | Safety-first operations",
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
            ["About.Description"] = "Version, diagnostics, and release information."
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
            ["About.Description"] = "الإصدار والتشخيصات ومعلومات الإصدار."
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
