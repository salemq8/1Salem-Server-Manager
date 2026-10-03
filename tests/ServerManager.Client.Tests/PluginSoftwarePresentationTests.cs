using System.Globalization;
using ServerManager.Client.Controls;
using ServerManager.Client.Shell;
using ServerManager.Contracts;

namespace ServerManager.Client.Tests;

public sealed class PluginSoftwarePresentationTests : IDisposable
{
    private readonly CultureInfo _original = CultureInfo.CurrentUICulture;
    private static readonly Guid Server = Guid.Parse("ec60e9c7-3c22-46ce-aaed-18c03124294e");
    private static ServerContentProfile Profile(ServerPlatform platform = ServerPlatform.Vanilla) =>
        new(Server, GameType.Minecraft, platform, "26.3", null, "C:\\Fixture", "C:\\Fixture\\plugins", platform != ServerPlatform.Vanilla, false);
    private static ContentProject Project() => new(ContentProviderId.Modrinth, "plugin", "plugin", "Real Plugin", null, null, null,
        null, null, ["paper"], ["26.3"], IsCompatible: true);
    private static ContentVersion Version(string[]? platforms = null, string version = "26.3") => new(ContentProviderId.Modrinth, "plugin", "release", "1.2",
        ContentReleaseChannel.Release, DateTimeOffset.UtcNow, platforms ?? ["paper"], [version],
        new ContentFile("plugin.jar", new Uri("https://cdn.modrinth.com/data/plugin.jar"), 100), []);
    private static MinecraftSoftwareStatus Status(bool available = true, string code = "Available", bool requiresWorldReset = false) => new(Server, ServerPlatform.Vanilla, "26.3", "world",
        [new(ServerPlatform.Paper, "26.3", available, code, "Status", RequiresWorldReset: requiresWorldReset),
            new(ServerPlatform.Purpur, "26.3", available, code, "Status", RequiresWorldReset: requiresWorldReset)]);

    [Fact]
    public void VanillaCanBrowseButCannotDirectInstall_EvenIfSearchMetadataClaimsCompatible()
    {
        LocalizationService.Apply("en-US");
        var item = new ContentItemViewModel(Project(), Profile());
        Assert.True(item.RequiresSoftware); Assert.False(item.CanInstall); Assert.False(item.IsCompatible);
        Assert.True(item.CanAct);
        Assert.Equal("Requires plugin server software", item.CompatibilityLine);
        Assert.Equal("Choose server software", item.ActionLabel);
        Assert.DoesNotContain("Install", item.AutomationName);
    }

    [Fact]
    public void MigrationChoiceNeedsAnActualCompatibleRelease_NotProjectLevelTags()
    {
        Assert.Empty(PluginSoftwarePresentation.Choices(new(Project(), null, []), Profile(), Status()));
        Assert.Empty(PluginSoftwarePresentation.Choices(new(Project(), null, [Version(version: "1.21.8")]), Profile(), Status()));
        Assert.Empty(PluginSoftwarePresentation.Choices(new(Project(), null, [Version() with { File = null }]), Profile(), Status()));
        Assert.Empty(PluginSoftwarePresentation.Choices(new(Project(), null, [Version() with { ProjectId = "other" }]), Profile(), Status()));
    }

    [Fact]
    public void PaperReleaseSupportsPaperAndPurpur_ButPurpurOnlyReleaseNeverOffersPaper()
    {
        var common = PluginSoftwarePresentation.Choices(new(Project(), null, [Version()]), Profile(), Status());
        Assert.Equal(new[] { ServerPlatform.Paper, ServerPlatform.Purpur }, common.Select(c => c.Software.Platform));
        var purpur = PluginSoftwarePresentation.Choices(new(Project(), null, [Version(["purpur"])]), Profile(), Status());
        Assert.Equal(ServerPlatform.Purpur, Assert.Single(purpur).Software.Platform);
        Assert.Empty(PluginSoftwarePresentation.Choices(new(Project(), null, [Version(["folia"])]), Profile(), Status()));
    }

    [Fact]
    public void FreshWorldResetChoiceStaysAvailableWithExplicitDestructiveWarning()
    {
        var choices = PluginSoftwarePresentation.Choices(new(Project(), null, [Version()]), Profile(), Status(requiresWorldReset: true));
        Assert.Equal(2, choices.Count);
        Assert.All(choices, choice => { Assert.True(choice.Software.Available); Assert.True(choice.Software.RequiresWorldReset); });
        LocalizationService.Apply("en-US");
        var explanation = PluginSoftwarePresentation.ChoiceExplanation(choices[0], "Real Plugin");
        Assert.Contains("fresh world reset", explanation);
        Assert.Contains("player progress will not carry over", explanation);
        Assert.Contains("no conversion", explanation);
        Assert.Contains("ask for confirmation", explanation);
        Assert.Contains("Real Plugin 1.2", explanation);
    }

    [Fact]
    public void FreshWorldResetDoesNotBypassRuntimeAvailabilityOrInventAPluginVersion()
    {
        var status = Status(false, "VersionUnavailable", requiresWorldReset: true);
        var choice = PluginSoftwarePresentation.Choices(new(Project(), null, [Version()]), Profile(), status)[0];
        Assert.False(choice.Software.Available);
        Assert.True(choice.Software.RequiresWorldReset);
        Assert.Empty(PluginSoftwarePresentation.Choices(new(Project(), null, [Version(version: "26.2")]), Profile(), status));
        LocalizationService.Apply("en-US");
        Assert.Contains("unavailable", PluginSoftwarePresentation.ChoiceExplanation(choice, "Real Plugin"));
        Assert.Contains("fresh world reset", PluginSoftwarePresentation.ChoiceExplanation(choice, "Real Plugin"));
    }

    [Fact]
    public void MissingRuntimeAvailabilityAndStaleServerOrVersionAreNotGuessed()
    {
        var detail = new ContentProjectDetail(Project(), null, [Version()]);
        Assert.Empty(PluginSoftwarePresentation.Choices(detail, Profile(), Status() with { Options = [] }));
        Assert.Empty(PluginSoftwarePresentation.Choices(detail, Profile(), Status() with { ServerId = Guid.NewGuid() }));
        Assert.Empty(PluginSoftwarePresentation.Choices(detail, Profile(), Status() with { MinecraftVersion = "26.2" }));
        Assert.Empty(PluginSoftwarePresentation.Choices(detail, Profile(), Status() with { CurrentPlatform = ServerPlatform.Paper }));
        Assert.Empty(PluginSoftwarePresentation.Choices(detail, Profile() with { MinecraftVersion = null }, Status()));
    }

    [Fact]
    public void SuccessfulProfileRefreshAllowsExplicitInstallOfTheSamePlugin()
    {
        var item = new ContentItemViewModel(Project(), Profile());
        item.Update(Project(), Profile(ServerPlatform.Paper));
        Assert.False(item.RequiresSoftware); Assert.True(item.CanInstall);
        Assert.Equal("plugin", item.ProjectId);
        item.IsBusy = true; Assert.False(item.CanAct);
        item.IsBusy = false; item.IsInstalled = true; Assert.False(item.CanAct);
    }

    [Fact]
    public void UnknownPlatformDoesNotOfferAnUnprovenMigration_AndDataPacksRemainNormal()
    {
        var unknown = Profile() with { Platform = ServerPlatform.Unknown };
        Assert.False(new ContentItemViewModel(Project(), unknown).CanAct);
        var pack = new ContentItemViewModel(Project() with { Kind = ContentKind.DataPack }, Profile());
        Assert.False(pack.RequiresSoftware); Assert.True(pack.CanInstall);
    }

    [Fact]
    public void FreshWorldResetWarningIsAvailableInArabic()
    {
        LocalizationService.Apply("ar-SA");
        Assert.Equal("يتطلب برنامج خادم يدعم الإضافات", PluginSoftwarePresentation.RequiresLabel);
        Assert.Contains("Paper", PluginSoftwarePresentation.ChangeTo(ServerPlatform.Paper));
        Assert.Contains("إعادة ضبط العالم", PluginSoftwarePresentation.FreshWorldWarning);
        Assert.Contains("لن ينتقل العالم الحالي أو تقدم اللاعبين", PluginSoftwarePresentation.FreshWorldWarning);
        Assert.Contains("تطلب التأكيد", PluginSoftwarePresentation.FreshWorldWarning);
    }

    public void Dispose() => LocalizationService.Apply(_original.Name);
}
