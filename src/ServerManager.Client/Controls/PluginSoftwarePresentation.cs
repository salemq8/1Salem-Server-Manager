using System.Globalization;
using ServerManager.Contracts;
using ServerManager.Core.Content;

namespace ServerManager.Client.Controls;

public sealed record PluginSoftwareChoice(MinecraftSoftwareOption Software, ContentVersion PluginVersion);
public sealed record ContentProjectChoice(bool Install = false, ServerPlatform? ChangeSoftware = null);

/// <summary>Browsing evidence is not install compatibility. Both the exact plugin release and runtime must fit.</summary>
public static class PluginSoftwarePresentation
{
    public static bool RequiresSoftware(ContentProject project, ServerContentProfile? profile) =>
        project.Kind == ContentKind.Plugin && profile is { SupportsPlugins: false };

    public static bool CanChooseSoftware(ContentProject project, ServerContentProfile? profile) =>
        RequiresSoftware(project, profile) && profile!.Platform == ServerPlatform.Vanilla;

    public static bool DirectInstall(ContentProject project, ServerContentProfile? profile) =>
        project.IsCompatible && !RequiresSoftware(project, profile);

    public static IReadOnlyList<PluginSoftwareChoice> Choices(ContentProjectDetail detail, ServerContentProfile profile, MinecraftSoftwareStatus status)
    {
        if (!CanChooseSoftware(detail.Project, profile) || status.ServerId != profile.ServerId ||
            status.CurrentPlatform != profile.Platform || string.IsNullOrWhiteSpace(profile.MinecraftVersion) ||
            !string.Equals(status.MinecraftVersion, profile.MinecraftVersion, StringComparison.OrdinalIgnoreCase)) return [];
        var choices = new List<PluginSoftwareChoice>();
        foreach (var platform in new[] { ServerPlatform.Paper, ServerPlatform.Purpur })
        {
            var runtime = status.Options.FirstOrDefault(option => option.Platform == platform &&
                string.Equals(option.MinecraftVersion, profile.MinecraftVersion, StringComparison.OrdinalIgnoreCase));
            if (runtime is null) continue;
            var hypothetical = profile with { Platform = platform, SupportsPlugins = true, UnsupportedReason = null };
            var version = PluginCompatibilityPolicy.SelectBest(detail.Versions.Where(v =>
                v.Provider == detail.Project.Provider && string.Equals(v.ProjectId, detail.Project.ProjectId, StringComparison.Ordinal)), hypothetical);
            if (version is not null) choices.Add(new(runtime, version));
        }
        return choices;
    }

    public static string Text(string english, string arabic) => CultureInfo.CurrentUICulture.TextInfo.IsRightToLeft ? arabic : english;
    public static string RequiresLabel => Text("Requires plugin server software", "يتطلب برنامج خادم يدعم الإضافات");
    public static string BrowseOnlyLabel => Text("Browse plugins — requires a software change", "تصفح الإضافات — يتطلب تغيير برنامج الخادم");
    public static string ChangeLabel => Text("Choose server software", "اختيار برنامج الخادم");
    public static string ChangeTo(ServerPlatform platform) => Text($"Change to {platform}", $"التغيير إلى {platform}");
    public static string FreshWorldWarning => Text(
        "Requires a fresh world reset. The current world and player progress will not carry over; no conversion is performed. Server Software will stop the server safely and ask for confirmation before the reset.",
        "يتطلب إعادة ضبط العالم وإنشاء عالم جديد. لن ينتقل العالم الحالي أو تقدم اللاعبين، ولن يتم تحويل العالم. ستوقف صفحة برنامج الخادم الخادم بأمان وتطلب التأكيد قبل إعادة الضبط.");

    public static string ChoiceExplanation(PluginSoftwareChoice choice, string projectName)
    {
        var evidence = choice.Software.Available
            ? $"{projectName} {choice.PluginVersion.VersionNumber} · Minecraft {choice.Software.MinecraftVersion}"
            : BlockedReason(choice.Software);
        return choice.Software.RequiresWorldReset ? $"{evidence}\n{FreshWorldWarning}" : evidence;
    }

    public static string BlockedReason(MinecraftSoftwareOption option) => option.Code switch
    {
        "VersionUnavailable" => Text("This software is unavailable for the server's exact Minecraft version.", "هذا البرنامج غير متاح لإصدار Minecraft الحالي نفسه."),
        "AlreadyCurrent" => Text("This software is already installed.", "هذا البرنامج مثبت بالفعل."),
        _ => Text("This migration is unavailable. Server Software explains the safety or availability restriction.", "هذا التغيير غير متاح. توضح صفحة برنامج الخادم قيد الأمان أو التوفر.")
    };
}
