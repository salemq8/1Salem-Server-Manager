using ServerManager.Contracts;
using ServerManager.Core.Content;

namespace ServerManager.Core.Tests;

/// <summary>
/// The rules that keep the new content types honest and contained: who serves what, where it
/// may be written, and what a modpack index has to prove before a single file is fetched.
/// </summary>
public sealed class ContentPhase2PolicyTests
{
    private static ServerContentProfile Profile(
        ServerPlatform platform = ServerPlatform.Paper,
        string? minecraftVersion = "1.21.8",
        GameType game = GameType.Minecraft) =>
        new(
            Guid.NewGuid(),
            game,
            platform,
            minecraftVersion,
            null,
            @"C:\servers\mc",
            @"C:\servers\mc\plugins",
            PluginPlatformPolicy.SupportsPlugins(platform) && minecraftVersion is not null,
            IsRunning: false);

    // ---- which provider serves which type --------------------------------------------------

    [Fact]
    public void OnlyModrinthCarriesPacks()
    {
        Assert.True(ContentTypePolicy.IsServedBy(ContentKind.Plugin, ContentProviderId.Hangar));
        Assert.True(ContentTypePolicy.IsServedBy(ContentKind.Plugin, ContentProviderId.Modrinth));

        // Hangar is a plugin repository; offering a Hangar tab for packs would be an empty promise.
        foreach (var kind in new[] { ContentKind.Modpack, ContentKind.DataPack, ContentKind.ResourcePack })
        {
            Assert.False(ContentTypePolicy.IsServedBy(kind, ContentProviderId.Hangar));
            Assert.True(ContentTypePolicy.IsServedBy(kind, ContentProviderId.Modrinth));
        }
    }

    [Fact]
    public void AVanillaServerTakesPacksButNotPlugins()
    {
        var vanilla = Profile(ServerPlatform.Vanilla);

        Assert.False(ContentTypePolicy.IsSupportedBy(ContentKind.Plugin, vanilla));
        Assert.True(ContentTypePolicy.IsSupportedBy(ContentKind.DataPack, vanilla));
        Assert.True(ContentTypePolicy.IsSupportedBy(ContentKind.ResourcePack, vanilla));
    }

    [Fact]
    public void PalworldTakesNoneOfIt()
    {
        var palworld = Profile(ServerPlatform.Unknown, "1.21.8", GameType.Palworld);

        foreach (var kind in Enum.GetValues<ContentKind>())
        {
            Assert.False(ContentTypePolicy.IsSupportedBy(kind, palworld));
        }
    }

    [Fact]
    public void AnUnknownMinecraftVersionBlocksPacks()
    {
        var unknown = Profile(ServerPlatform.Paper, null);

        Assert.False(ContentTypePolicy.IsSupportedBy(ContentKind.DataPack, unknown));
        Assert.False(ContentTypePolicy.IsSupportedBy(ContentKind.ResourcePack, unknown));
    }

    [Fact]
    public void EachTypeAsksModrinthForItsOwnLoaders()
    {
        Assert.Equal(["datapack"], ContentTypePolicy.ModrinthLoaders(ContentKind.DataPack, ServerPlatform.Paper));
        Assert.Equal(["minecraft"], ContentTypePolicy.ModrinthLoaders(ContentKind.ResourcePack, ServerPlatform.Paper));
        Assert.Contains("fabric", ContentTypePolicy.ModrinthLoaders(ContentKind.Modpack, ServerPlatform.Paper));
        Assert.Contains("paper", ContentTypePolicy.ModrinthLoaders(ContentKind.Plugin, ServerPlatform.Paper));
    }

    [Fact]
    public void ADataPackAsksForAReloadWhileAPluginAsksForARestart()
    {
        Assert.Equal(
            InstalledContentState.ReloadRequired,
            ContentTypePolicy.PendingState(ContentKind.DataPack, serverRunning: true));
        Assert.Equal(
            InstalledContentState.RestartRequired,
            ContentTypePolicy.PendingState(ContentKind.Plugin, serverRunning: true));

        // A resource pack is not live until someone chooses to send it to players.
        Assert.Equal(
            InstalledContentState.NotDistributed,
            ContentTypePolicy.PendingState(ContentKind.ResourcePack, serverRunning: true));

        // A stopped server needs nothing: the content loads on next start.
        Assert.Equal(
            InstalledContentState.UpToDate,
            ContentTypePolicy.PendingState(ContentKind.DataPack, serverRunning: false));
    }

    [Fact]
    public void OnlyFabricCanBeSetUpWithoutSomeoneElsesInstaller()
    {
        Assert.True(ContentTypePolicy.CanInstallLoader("fabric"));
        Assert.False(ContentTypePolicy.CanInstallLoader("forge"));
        Assert.False(ContentTypePolicy.CanInstallLoader("neoforge"));
        Assert.False(ContentTypePolicy.CanInstallLoader("quilt"));
        Assert.False(ContentTypePolicy.CanInstallLoader(null));
    }

    // ---- where content may be written -------------------------------------------------------

    [Fact]
    public void DataPacksGoIntoTheWorldTheServerActuallyRuns()
    {
        var root = Path.Combine(Path.GetTempPath(), "1salem-srv");

        var resolved = ContentPathPolicy.ResolveDataPackDirectory(root, "survival");

        Assert.EndsWith(Path.Combine("survival", "datapacks"), resolved, StringComparison.OrdinalIgnoreCase);
        Assert.True(SafePathPolicy.IsWithinRoot(resolved, root));
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData(@"..\evil")]
    [InlineData("nested/world")]
    [InlineData(@"C:\windows")]
    public void AWorldNameIsNeverAllowedToBeAPath(string levelName)
    {
        var root = Path.Combine(Path.GetTempPath(), "1salem-srv");

        Assert.Throws<UnauthorizedAccessException>(
            () => ContentPathPolicy.ResolveDataPackDirectory(root, levelName));
    }

    [Fact]
    public void EachTypeKeepsItsOwnFolderAndExtension()
    {
        var root = Path.Combine(Path.GetTempPath(), "1salem-srv");
        var plugins = ContentPathPolicy.ResolvePluginsDirectory(root);
        var packs = ContentPathPolicy.ResolveResourcePackDirectory(root);
        var dataPacks = ContentPathPolicy.ResolveDataPackDirectory(root, "world");

        Assert.NotEqual(plugins, packs);
        Assert.NotEqual(plugins, dataPacks);
        Assert.All([plugins, packs, dataPacks], path => Assert.True(SafePathPolicy.IsWithinRoot(path, root)));

        Assert.Equal(".jar", ContentTypePolicy.FileExtension(ContentKind.Plugin));
        Assert.Equal(".zip", ContentTypePolicy.FileExtension(ContentKind.DataPack));
        Assert.Equal(".mrpack", ContentTypePolicy.FileExtension(ContentKind.Modpack));

        // A pack keeps its .zip rather than being renamed into a plugin.
        Assert.Equal(
            "VanillaTweaks.zip",
            ContentPathPolicy.SanitizeFileName("VanillaTweaks.zip", "pack", ".zip"));
        Assert.Equal(
            "evil.zip",
            ContentPathPolicy.SanitizeFileName("../../evil.zip", "pack", ".zip"));
    }

    // ---- archive safety ---------------------------------------------------------------------

    [Theory]
    [InlineData("../escape.txt", "path traversal")]
    [InlineData("a/../../escape.txt", "path traversal")]
    [InlineData("/etc/passwd", "absolute path")]
    [InlineData(@"C:\windows\system32\evil.dll", "drive-qualified path")]
    [InlineData("//server/share/evil.txt", "UNC path")]
    [InlineData("CON.txt", "reserved device name")]
    [InlineData("data/COM1.json", "reserved device name")]
    [InlineData("", "empty path")]
    public void UnsafeArchiveEntriesAreRefusedWithAReason(string entry, string expectedReason)
    {
        var verdict = ArchiveSafetyPolicy.CheckEntryPath(entry);

        Assert.False(verdict.IsSafe);
        Assert.Equal(expectedReason, verdict.Reason);
    }

    [Theory]
    [InlineData("data/minecraft/tags/blocks/mineable.json")]
    [InlineData("pack.mcmeta")]
    [InlineData("assets/minecraft/textures/block/stone.png")]
    public void OrdinaryPackEntriesAreAccepted(string entry) =>
        Assert.True(ArchiveSafetyPolicy.CheckEntryPath(entry).IsSafe);

    [Fact]
    public void AnEntryThatEscapesTheDestinationIsRefusedEvenAfterNormalizing()
    {
        var root = Path.Combine(Path.GetTempPath(), "1salem-extract");

        Assert.Throws<UnauthorizedAccessException>(
            () => ArchiveSafetyPolicy.ResolveEntryDestination(root, "../outside.txt"));

        var inside = ArchiveSafetyPolicy.ResolveEntryDestination(root, "data/thing.json");
        Assert.True(SafePathPolicy.IsWithinRoot(inside, root));
    }

    [Fact]
    public void CompressionBombsAreSpottedByRatioNotByCompressedSizeAlone()
    {
        Assert.True(ArchiveSafetyPolicy.IsSuspiciousRatio(1_000, 100_000_000));
        Assert.False(ArchiveSafetyPolicy.IsSuspiciousRatio(1_000_000, 4_000_000));

        // Limits differ by type: a resource pack may legitimately be far bigger than a modpack index.
        Assert.True(
            ArchiveSafetyPolicy.MaximumExtractedBytes(ContentKind.ResourcePack) >
            ArchiveSafetyPolicy.MaximumExtractedBytes(ContentKind.Modpack));
    }

    // ---- modpack index ----------------------------------------------------------------------

    private static ModpackIndex Index(
        IReadOnlyList<ModpackFile>? files = null,
        string loader = "fabric-loader",
        string game = "minecraft",
        int formatVersion = 1) =>
        new(
            formatVersion,
            game,
            "abc",
            "Test pack",
            null,
            files ?? [File()],
            new Dictionary<string, string> { ["minecraft"] = "1.21.8", [loader] = "0.16.9" });

    private static ModpackFile File(
        string path = "mods/thing.jar",
        string? host = "cdn.modrinth.com",
        string? sha512 = "aa",
        long size = 1024) =>
        new(
            path,
            host is null ? [] : [new Uri($"https://{host}/data/x/thing.jar")],
            sha512,
            "bb",
            size);

    [Fact]
    public void AWellFormedFabricPackIsAccepted()
    {
        var validation = ModpackIndexPolicy.Validate(Index());

        Assert.True(validation.IsValid);
        Assert.Null(validation.ErrorCode);
    }

    [Fact]
    public void APackForALoaderWeCannotSetUpIsRefusedByName()
    {
        var validation = ModpackIndexPolicy.Validate(Index(loader: "neoforge"));

        Assert.False(validation.IsValid);
        Assert.Equal("LoaderNotSupported:neoforge", validation.ErrorCode);
    }

    [Fact]
    public void APackThatWritesOutsideTheServerIsRefused()
    {
        var validation = ModpackIndexPolicy.Validate(Index([File(path: "../../evil.jar")]));

        Assert.False(validation.IsValid);
        Assert.Equal("UnsafePath", validation.ErrorCode);
    }

    [Fact]
    public void APackPointingAtAnUnapprovedSiteIsRefused()
    {
        var validation = ModpackIndexPolicy.Validate(Index([File(host: "evil.test")]));

        Assert.False(validation.IsValid);
        Assert.Equal("UntrustedHost", validation.ErrorCode);
    }

    [Fact]
    public void OnlyTheProvidersOwnAllowedHostsCount()
    {
        Assert.True(ModpackIndexPolicy.IsAllowedDownloadHost(new Uri("https://cdn.modrinth.com/a.jar")));
        Assert.True(ModpackIndexPolicy.IsAllowedDownloadHost(new Uri("https://github.com/a/b.jar")));
        Assert.False(ModpackIndexPolicy.IsAllowedDownloadHost(new Uri("http://cdn.modrinth.com/a.jar")));
        Assert.False(ModpackIndexPolicy.IsAllowedDownloadHost(new Uri("https://cdn.modrinth.com.evil.test/a.jar")));
        Assert.False(ModpackIndexPolicy.IsAllowedDownloadHost(null));
    }

    [Fact]
    public void APackFileWithNoChecksumIsRefused()
    {
        var validation = ModpackIndexPolicy.Validate(Index([new ModpackFile(
            "mods/thing.jar",
            [new Uri("https://cdn.modrinth.com/thing.jar")],
            null,
            null,
            10)]));

        Assert.False(validation.IsValid);
        Assert.Equal("NoHash", validation.ErrorCode);
    }

    [Fact]
    public void APackLargerThanTheCeilingIsRefused()
    {
        var huge = Enumerable.Range(0, 40)
            .Select(index => File(path: $"mods/{index}.jar", size: 512L * 1024 * 1024))
            .ToArray();

        var validation = ModpackIndexPolicy.Validate(Index(huge));

        Assert.False(validation.IsValid);
        Assert.Equal("PackTooLarge", validation.ErrorCode);
    }

    [Fact]
    public void AnUnknownIndexFormatOrGameIsRefused()
    {
        Assert.Equal("UnsupportedFormat", ModpackIndexPolicy.Validate(Index(formatVersion: 2)).ErrorCode);
        Assert.Equal("UnsupportedGame", ModpackIndexPolicy.Validate(Index(game: "other")).ErrorCode);
        Assert.Equal("MalformedIndex", ModpackIndexPolicy.Validate(null).ErrorCode);
    }

    [Fact]
    public void ClientOnlyFilesAreSkippedRatherThanInstalled()
    {
        var index = Index(
        [
            File(),
            new ModpackFile(
                "mods/client-only.jar",
                [new Uri("https://cdn.modrinth.com/c.jar")],
                "aa",
                "bb",
                10,
                ClientEnvironment: "required",
                ServerEnvironment: "unsupported")
        ]);

        var validation = ModpackIndexPolicy.Validate(index);

        Assert.True(validation.IsValid);
        Assert.Contains("ClientOnlyFilesSkipped:1", validation.Warnings);
        Assert.False(index.Files[1].IsRequiredOnServer);
    }

    [Fact]
    public void TheIndexReportsWhatTheServerWillBe()
    {
        var index = Index();

        Assert.Equal("1.21.8", index.MinecraftVersion);
        Assert.Equal("fabric", index.Loader);
        Assert.Equal("0.16.9", index.LoaderVersion);
    }
}
