using System.Globalization;
using ServerManager.Client.Controls;
using ServerManager.Client.Shell;
using ServerManager.Contracts;

namespace ServerManager.Client.Tests;

/// <summary>
/// What the Content Hub puts on screen. The rules that matter: the provider is always named,
/// nothing a provider did not return is invented, and every label is a real sentence in both
/// languages rather than a key or a model's ToString.
/// </summary>
public sealed class ContentHubUiTests : IDisposable
{
    private readonly CultureInfo _original = CultureInfo.CurrentUICulture;

    public void Dispose() => LocalizationService.Apply(_original.Name);

    private static ContentProject Project(
        ContentProviderId provider = ContentProviderId.Modrinth,
        string? author = "lucko",
        long? downloads = 2_700_000,
        bool compatible = true) =>
        new(
            provider,
            "Vebnzrzj",
            "luckperms",
            "LuckPerms",
            "Permissions management",
            author,
            null,
            new Uri("https://modrinth.com/plugin/luckperms"),
            downloads,
            ["paper"],
            ["1.21.8"],
            "MIT",
            ContentKind.Plugin,
            null,
            compatible,
            "Paper · Minecraft 1.21.8");

    private static InstalledContent Installed(
        InstalledContentState state,
        ContentProviderId? provider = ContentProviderId.Modrinth,
        string? version = "5.5.71",
        string? previousFile = null) =>
        new(
            Guid.NewGuid(),
            "LuckPerms-Bukkit-5.5.71.jar",
            state,
            ContentKind.Plugin,
            provider,
            "Vebnzrzj",
            "abc",
            "LuckPerms",
            version,
            "1.21.8",
            ServerPlatform.Paper,
            new Uri("https://modrinth.com/plugin/luckperms"),
            PreviousFileName: previousFile,
            AvailableVersionNumber: state == InstalledContentState.UpdateAvailable ? "5.6.0" : null);

    [Fact]
    public void EveryCardNamesTheSiteItCameFrom()
    {
        LocalizationService.Apply("en-US");

        Assert.Equal("Source: Modrinth", new ContentItemViewModel(Project()).SourceLine);
        Assert.Equal(
            "Source: Hangar",
            new ContentItemViewModel(Project(ContentProviderId.Hangar)).SourceLine);
    }

    [Fact]
    public void ACardShowsOnlyWhatTheProviderReturned()
    {
        LocalizationService.Apply("en-US");
        var withoutExtras = new ContentItemViewModel(Project(author: null, downloads: null));

        // No author and no download count means those lines are simply absent, not "0" or "null".
        Assert.Equal(string.Empty, withoutExtras.AuthorLine);
        Assert.Equal(string.Empty, withoutExtras.DownloadsLine);
        Assert.False(withoutExtras.HasDownloads);

        var complete = new ContentItemViewModel(Project());
        Assert.Equal("by lucko", complete.AuthorLine);
        Assert.Contains("2.7M", complete.DownloadsLine, StringComparison.Ordinal);
    }

    [Fact]
    public void AnIncompatiblePluginCannotBeInstalled()
    {
        LocalizationService.Apply("en-US");
        Assert.False(new ContentItemViewModel(Project(compatible: false)).CanInstall);

        var item = new ContentItemViewModel(Project());
        Assert.True(item.CanInstall);

        item.IsInstalled = true;
        Assert.False(item.CanInstall);
        Assert.Equal("Installed", item.ActionLabel);

        item.IsInstalled = false;
        item.IsBusy = true;
        Assert.False(item.CanInstall);
    }

    [Fact]
    public void ActionsAreNamedWithTheirPluginForScreenReaders()
    {
        LocalizationService.Apply("en-US");

        Assert.Equal("Install LuckPerms", new ContentItemViewModel(Project()).AutomationName);

        var installed = new InstalledItemViewModel(Installed(InstalledContentState.UpToDate));
        Assert.Equal("Update LuckPerms", installed.UpdateAutomationName);
        Assert.Equal("Remove LuckPerms", installed.UninstallAutomationName);
    }

    [Fact]
    public void EveryInstalledStateReadsAsASentenceInBothLanguages()
    {
        var states = Enum.GetValues<InstalledContentState>();

        foreach (var language in new[] { "en-US", "ar-SA" })
        {
            LocalizationService.Apply(language);
            foreach (var state in states)
            {
                var label = new InstalledItemViewModel(Installed(state)).StatusLabel;
                Assert.False(string.IsNullOrWhiteSpace(label));

                // A missing key echoes back as "Content.State.…", which would be a leak.
                Assert.DoesNotContain("Content.", label, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void ArabicKeepsPluginNamesAndVersionsUntranslated()
    {
        LocalizationService.Apply("ar-SA");
        var item = new InstalledItemViewModel(Installed(InstalledContentState.UpdateAvailable));

        Assert.Equal("LuckPerms", item.DisplayName);
        Assert.Contains("5.5.71", item.VersionLine, StringComparison.Ordinal);
        Assert.Contains("Modrinth", item.SourceLine, StringComparison.Ordinal);
        Assert.Contains("5.6.0", item.UpdateLine, StringComparison.Ordinal);

        // The wording around them is Arabic, not an untranslated key.
        Assert.DoesNotContain("Content.", item.SourceLine, StringComparison.Ordinal);
        Assert.DoesNotContain("Version", item.VersionLine, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownVersionSaysSoRatherThanShowingNothing()
    {
        LocalizationService.Apply("en-US");
        var item = new InstalledItemViewModel(
            Installed(InstalledContentState.InstalledManually, provider: null, version: null));

        Assert.Equal("Version unknown", item.VersionLine);
        Assert.Equal("Source: unknown", item.SourceLine);
        Assert.Equal("Added by you", item.StatusLabel);
    }

    [Fact]
    public void RollbackIsOfferedOnlyWhenAnEarlierFileWasKept()
    {
        LocalizationService.Apply("en-US");

        Assert.False(new InstalledItemViewModel(Installed(InstalledContentState.UpToDate)).CanRollback);
        Assert.True(
            new InstalledItemViewModel(
                    Installed(InstalledContentState.UpToDate, previousFile: "LuckPerms-5.5.53.jar"))
                .CanRollback);
    }

    [Fact]
    public void EveryContentTypeHasItsOwnWordsInBothLanguages()
    {
        foreach (var language in new[] { "en-US", "ar-SA" })
        {
            LocalizationService.Apply(language);
            var labels = Enum.GetValues<ContentKind>().Select(ContentLabels.Kind).ToArray();

            Assert.All(labels, label => Assert.False(string.IsNullOrWhiteSpace(label)));
            Assert.DoesNotContain(labels, label => label.Contains("Content.", StringComparison.Ordinal));

            // A data pack must never be shown as a plugin.
            Assert.Equal(labels.Length, labels.Distinct(StringComparer.Ordinal).Count());
        }
    }

    [Fact]
    public void ARowSaysWhichTypeItIs()
    {
        LocalizationService.Apply("en-US");

        Assert.Equal(
            "Data Pack",
            new InstalledItemViewModel(
                Installed(InstalledContentState.ReloadRequired) with { Kind = ContentKind.DataPack })
                .KindLabel);
        Assert.Equal("Plugin", new InstalledItemViewModel(Installed(InstalledContentState.UpToDate)).KindLabel);
    }

    [Fact]
    public void AResourcePackIsOfferedToPlayersOnlyWhenTheProviderHostsIt()
    {
        LocalizationService.Apply("en-US");
        var hosted = Installed(InstalledContentState.NotDistributed) with
        {
            Kind = ContentKind.ResourcePack,
            DownloadUrl = new Uri("https://cdn.modrinth.com/data/x/faithful.zip")
        };

        Assert.True(new InstalledItemViewModel(hosted).CanDistribute);

        // Nothing to point clients at, so the option is not offered at all.
        Assert.False(new InstalledItemViewModel(hosted with { DownloadUrl = null }).CanDistribute);

        // Already being sent: the option becomes "stop sending".
        var sending = new InstalledItemViewModel(hosted with
        {
            IsDistributed = true,
            State = InstalledContentState.Distributed
        });
        Assert.False(sending.CanDistribute);
        Assert.True(sending.CanWithdraw);
        Assert.Equal("Sent to players", sending.StatusLabel);
    }

    [Fact]
    public void ADataPackAsksForAReloadAndNotARestart()
    {
        LocalizationService.Apply("en-US");

        Assert.Equal(
            "Reload required",
            new InstalledItemViewModel(
                Installed(InstalledContentState.ReloadRequired) with { Kind = ContentKind.DataPack })
                .StatusLabel);
        Assert.Equal(
            "Made for another version",
            new InstalledItemViewModel(Installed(InstalledContentState.IncompatibleWithServer)).StatusLabel);
    }

    [Fact]
    public void UpdateIsOfferedOnlyWhenOneIsActuallyAvailable()
    {
        LocalizationService.Apply("en-US");

        Assert.True(new InstalledItemViewModel(Installed(InstalledContentState.UpdateAvailable)).CanUpdate);
        Assert.False(new InstalledItemViewModel(Installed(InstalledContentState.UpToDate)).CanUpdate);

        // A file that is gone offers neither an update nor a removal, but the button stays on
        // screen, greyed, because for a plugin removing is a thing that exists.
        var missing = new InstalledItemViewModel(Installed(InstalledContentState.MissingFile));
        Assert.False(missing.CanUpdate);
        Assert.False(missing.CanUninstall);
        Assert.True(missing.ShowUninstall);
    }

    [Fact]
    public void AModpackIsTheServerItBuiltAndIsNotRemovedFromHere()
    {
        LocalizationService.Apply("en-US");
        var pack = Installed(InstalledContentState.UpToDate) with
        {
            Kind = ContentKind.Modpack,
            ProjectName = "Fabulously Optimized",
            InstalledVersion = "14.0.0",
            Loader = "fabric",
            LoaderVersion = "0.19.5",
            MinecraftVersionAtInstall = "26.2"
        };
        var view = new InstalledItemViewModel(pack);

        // Removing or rolling back a modpack would mean the server itself, which is not done
        // from a content list, so neither is offered.
        Assert.False(view.CanUninstall);
        Assert.False(view.ShowUninstall);
        Assert.False(view.CanRollback);
        Assert.Equal("Modpack", view.KindLabel);
        Assert.Equal("Minecraft 26.2 · Fabric 0.19.5", view.EnvironmentLine);

        // A newer release for the same environment is an ordinary update.
        var updatable = new InstalledItemViewModel(pack with
        {
            State = InstalledContentState.UpdateAvailable,
            AvailableVersionNumber = "14.1.0"
        });
        Assert.True(updatable.CanUpdate);

        // One that changes the Minecraft version or loader is not offered as an update at all,
        // and says why instead.
        var migration = new InstalledItemViewModel(pack with
        {
            State = InstalledContentState.RequiresServerMigration,
            AvailableVersionNumber = "15.0.0"
        });
        Assert.False(migration.CanUpdate);
        Assert.Equal("Requires server migration", migration.StatusLabel);
        Assert.Contains("15.0.0", migration.UpdateLine);
        Assert.Contains("cannot be applied as an update", migration.UpdateLine);
    }
}
