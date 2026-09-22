using ServerManager.Contracts;
using ServerManager.Core.Content;

namespace ServerManager.Core.Tests;

public sealed class ContentPolicyTests
{
    private static ServerContentProfile Profile(
        ServerPlatform platform = ServerPlatform.Paper,
        string? minecraftVersion = "1.21.8") =>
        new(
            Guid.NewGuid(),
            GameType.Minecraft,
            platform,
            minecraftVersion,
            null,
            @"C:\servers\mc",
            @"C:\servers\mc\plugins",
            PluginPlatformPolicy.SupportsPlugins(platform) && minecraftVersion is not null,
            IsRunning: false);

    private static ContentVersion Version(
        string id,
        string[] platforms,
        string[] gameVersions,
        ContentReleaseChannel channel = ContentReleaseChannel.Release,
        DateTimeOffset? published = null,
        ContentProviderId provider = ContentProviderId.Modrinth,
        bool withFile = true) =>
        new(
            provider,
            "project",
            id,
            id,
            channel,
            published ?? DateTimeOffset.UtcNow,
            platforms,
            gameVersions,
            withFile
                ? new ContentFile("plugin.jar", new Uri("https://cdn.modrinth.com/a.jar"), 10)
                : null,
            []);

    [Fact]
    public void Vanilla_SupportsNoPlugins()
    {
        Assert.False(PluginPlatformPolicy.SupportsPlugins(ServerPlatform.Vanilla));
        Assert.False(PluginPlatformPolicy.SupportsPlugins(ServerPlatform.Unknown));
        Assert.True(PluginPlatformPolicy.SupportsPlugins(ServerPlatform.Paper));
        Assert.True(PluginPlatformPolicy.SupportsPlugins(ServerPlatform.Purpur));
        Assert.True(PluginPlatformPolicy.SupportsPlugins(ServerPlatform.Spigot));
        Assert.True(PluginPlatformPolicy.SupportsPlugins(ServerPlatform.Bukkit));
    }

    [Fact]
    public void PaperAndPurpur_RunBukkitAndSpigotPlugins()
    {
        Assert.Contains("bukkit", PluginPlatformPolicy.ModrinthLoaders(ServerPlatform.Paper));
        Assert.Contains("spigot", PluginPlatformPolicy.ModrinthLoaders(ServerPlatform.Paper));
        Assert.Contains("purpur", PluginPlatformPolicy.ModrinthLoaders(ServerPlatform.Purpur));

        // A Spigot server must not be offered Paper-only plugins.
        Assert.DoesNotContain("paper", PluginPlatformPolicy.ModrinthLoaders(ServerPlatform.Spigot));
    }

    [Fact]
    public void Hangar_ServesPaperFamilyOnly()
    {
        Assert.Equal("PAPER", PluginPlatformPolicy.HangarPlatform(ServerPlatform.Paper));
        Assert.Equal("PAPER", PluginPlatformPolicy.HangarPlatform(ServerPlatform.Purpur));

        // Hangar has no Spigot or Bukkit platform, so those servers are served by Modrinth.
        Assert.Null(PluginPlatformPolicy.HangarPlatform(ServerPlatform.Spigot));
        Assert.Null(PluginPlatformPolicy.HangarPlatform(ServerPlatform.Bukkit));
        Assert.False(PluginPlatformPolicy.IsServedBy(ContentProviderId.Hangar, ServerPlatform.Bukkit));
        Assert.True(PluginPlatformPolicy.IsServedBy(ContentProviderId.Modrinth, ServerPlatform.Bukkit));
    }

    [Fact]
    public void SelectBest_PrefersNewestCompatibleNotNewestOverall()
    {
        var profile = Profile();
        var versions = new[]
        {
            // Newest overall, but it dropped this server's Minecraft version.
            Version("v3", ["paper"], ["1.21.11"], published: DateTimeOffset.UtcNow),
            Version("v2", ["paper"], ["1.21.8"], published: DateTimeOffset.UtcNow.AddDays(-10)),
            Version("v1", ["paper"], ["1.21.8"], published: DateTimeOffset.UtcNow.AddDays(-40))
        };

        var selected = PluginCompatibilityPolicy.SelectBest(versions, profile);

        Assert.Equal("v2", selected?.VersionId);
    }

    [Fact]
    public void SelectBest_PrefersStableOverNewerPrerelease()
    {
        var profile = Profile();
        var versions = new[]
        {
            Version("beta", ["paper"], ["1.21.8"], ContentReleaseChannel.Beta, DateTimeOffset.UtcNow),
            Version("stable", ["paper"], ["1.21.8"], published: DateTimeOffset.UtcNow.AddDays(-5))
        };

        // Stable wins even when a pre-release is newer, and even when pre-releases are allowed.
        Assert.Equal("stable", PluginCompatibilityPolicy.SelectBest(versions, profile)?.VersionId);
        Assert.Equal(
            "stable",
            PluginCompatibilityPolicy.SelectBest(versions, profile, allowPrerelease: true)?.VersionId);

        // Allowing pre-releases only widens the field when no stable release fits.
        var betaOnly = new[]
        {
            Version("beta", ["paper"], ["1.21.8"], ContentReleaseChannel.Beta, DateTimeOffset.UtcNow)
        };
        Assert.Null(PluginCompatibilityPolicy.SelectBest(betaOnly, profile));
        Assert.Equal(
            "beta",
            PluginCompatibilityPolicy.SelectBest(betaOnly, profile, allowPrerelease: true)?.VersionId);
    }

    [Fact]
    public void SelectBest_RefusesWhenNothingMatchesTheServer()
    {
        var profile = Profile(ServerPlatform.Spigot);
        var versions = new[] { Version("v1", ["paper"], ["1.21.8"]) };

        // Paper-only on a Spigot server is not a match, so nothing is offered.
        Assert.Null(PluginCompatibilityPolicy.SelectBest(versions, profile));
    }

    [Fact]
    public void IsCompatible_RefusesExternallyHostedAndUnknownVersions()
    {
        var profile = Profile();
        Assert.False(PluginCompatibilityPolicy.IsCompatible(
            Version("x", ["paper"], ["1.21.8"], withFile: false),
            profile));
        Assert.False(PluginCompatibilityPolicy.IsCompatible(
            Version("x", ["paper"], ["1.21.8"]),
            Profile(minecraftVersion: null)));
    }

    [Theory]
    [InlineData("../../evil.jar", "evil.jar")]
    [InlineData(@"..\..\evil.jar", "evil.jar")]
    [InlineData("C:\\windows\\system32\\evil.jar", "evil.jar")]
    [InlineData("/etc/passwd", "passwd.jar")]
    [InlineData("plugin.jar", "plugin.jar")]
    [InlineData("", "fallback.jar")]
    public void SanitizeFileName_KeepsOnlyAFileName(string provided, string expected)
    {
        Assert.Equal(expected, ContentPathPolicy.SanitizeFileName(provided, "fallback"));
    }

    [Fact]
    public void SanitizeFileName_RejectsReservedWindowsNames()
    {
        Assert.Equal("fallback.jar", ContentPathPolicy.SanitizeFileName("CON.jar", "fallback"));
    }

    [Fact]
    public void ResolveInstallPath_RefusesAnythingOutsideThePluginsFolder()
    {
        var plugins = Path.Combine(Path.GetTempPath(), "1SalemPlugins");

        Assert.Throws<UnauthorizedAccessException>(
            () => ContentPathPolicy.ResolveInstallPath(plugins, @"..\escaped.jar"));
        Assert.Throws<UnauthorizedAccessException>(
            () => ContentPathPolicy.ResolveInstallPath(plugins, @"nested\plugin.jar"));
        Assert.Throws<UnauthorizedAccessException>(
            () => ContentPathPolicy.ResolveInstallPath(plugins, @"C:\windows\evil.jar"));

        var resolved = ContentPathPolicy.ResolveInstallPath(plugins, "plugin.jar");
        Assert.True(SafePathPolicy.IsWithinRoot(resolved, plugins));
    }

    [Fact]
    public void DownloadPolicy_AcceptsOnlyProviderHostsOverHttps()
    {
        Assert.True(ContentDownloadPolicy.IsAllowedDownloadUrl(
            ContentProviderId.Modrinth,
            new Uri("https://cdn.modrinth.com/data/x/versions/y/plugin.jar")));
        Assert.True(ContentDownloadPolicy.IsAllowedDownloadUrl(
            ContentProviderId.Hangar,
            new Uri("https://hangarcdn.papermc.io/plugins/a/b/versions/1/PAPER/p.jar")));

        // Plain HTTP, another site, a look-alike host, and a provider mix-up are all refused.
        Assert.False(ContentDownloadPolicy.IsAllowedDownloadUrl(
            ContentProviderId.Modrinth,
            new Uri("http://cdn.modrinth.com/plugin.jar")));
        Assert.False(ContentDownloadPolicy.IsAllowedDownloadUrl(
            ContentProviderId.Modrinth,
            new Uri("https://evil.test/plugin.jar")));
        Assert.False(ContentDownloadPolicy.IsAllowedDownloadUrl(
            ContentProviderId.Modrinth,
            new Uri("https://cdn.modrinth.com.evil.test/plugin.jar")));
        Assert.False(ContentDownloadPolicy.IsAllowedDownloadUrl(
            ContentProviderId.Modrinth,
            new Uri("https://hangarcdn.papermc.io/plugin.jar")));
    }

    [Fact]
    public void DownloadPolicy_RefusesOversizedFiles()
    {
        Assert.True(ContentDownloadPolicy.IsAcceptableSize(1024));
        Assert.False(ContentDownloadPolicy.IsAcceptableSize(ContentDownloadPolicy.MaximumFileBytes + 1));
    }
}
