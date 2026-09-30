using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using ServerManager.Contracts;
using ServerManager.Core.Content;
using ServerManager.Infrastructure.Content;

namespace ServerManager.Infrastructure.Tests;

/// <summary>
/// The Discover platform filter (Build 10): it narrows what the providers are asked for, and
/// never changes what a card says about fitting this server.
/// </summary>
public sealed class ContentPlatformFilterTests
{
    [Fact]
    public void Options_FollowTheContentType()
    {
        Assert.Equal(["auto", "paper", "purpur", "spigot", "bukkit", "folia", "all"], ContentPlatformFilter.Options(ContentKind.Plugin));
        Assert.Equal(["all", "fabric", "forge", "neoforge", "quilt"], ContentPlatformFilter.Options(ContentKind.Modpack));
        Assert.Empty(ContentPlatformFilter.Options(ContentKind.DataPack));
        Assert.Empty(ContentPlatformFilter.Options(ContentKind.ResourcePack));
    }

    [Theory]
    [InlineData(ContentKind.Plugin, null, "auto")]
    [InlineData(ContentKind.Plugin, "PAPER", "paper")]
    [InlineData(ContentKind.Plugin, "fabric", "auto")]
    [InlineData(ContentKind.Modpack, null, "all")]
    [InlineData(ContentKind.Modpack, "spigot", "all")]
    [InlineData(ContentKind.Modpack, "NeoForge", "neoforge")]
    public void Normalize_KeepsKnownChoices_AndDefaultsTheRest(ContentKind kind, string? filter, string expected) =>
        Assert.Equal(expected, ContentPlatformFilter.Normalize(kind, filter));

    [Theory]
    [InlineData(ServerPlatform.Paper, "auto", "paper,spigot,bukkit")]
    [InlineData(ServerPlatform.Purpur, "auto", "purpur,paper,spigot,bukkit")]
    [InlineData(ServerPlatform.Paper, "paper", "paper")]
    [InlineData(ServerPlatform.Paper, "purpur", "purpur")]
    [InlineData(ServerPlatform.Paper, "spigot", "spigot")]
    [InlineData(ServerPlatform.Paper, "bukkit", "bukkit")]
    [InlineData(ServerPlatform.Paper, "folia", "folia")]
    [InlineData(ServerPlatform.Spigot, "all", "paper,purpur,spigot,bukkit,folia")]
    public void PluginLoaders_FollowTheFilter(ServerPlatform server, string filter, string expected) =>
        Assert.Equal(expected, string.Join(',', ContentPlatformFilter.ModrinthLoaders(ContentKind.Plugin, server, filter)));

    [Theory]
    [InlineData(ServerPlatform.Paper, "auto", true)]
    [InlineData(ServerPlatform.Paper, "all", true)]
    [InlineData(ServerPlatform.Paper, "paper", true)]
    [InlineData(ServerPlatform.Purpur, "purpur", true)]
    [InlineData(ServerPlatform.Paper, "spigot", false)]
    [InlineData(ServerPlatform.Paper, "bukkit", false)]
    [InlineData(ServerPlatform.Paper, "folia", false)]
    [InlineData(ServerPlatform.Folia, "auto", false)]
    [InlineData(ServerPlatform.Folia, "paper", false)]
    public void Hangar_IsAskedOnlyWhereItsPaperFilesApply(ServerPlatform server, string filter, bool expected) =>
        Assert.Equal(expected, ContentPlatformFilter.IncludesHangar(ContentKind.Plugin, filter, server));

    [Theory]
    [InlineData(ServerPlatform.Paper, "all", "paper,spigot,bukkit")]
    [InlineData(ServerPlatform.Paper, "spigot", "spigot")]
    [InlineData(ServerPlatform.Paper, "folia", "")]
    [InlineData(ServerPlatform.Spigot, "paper", "")]
    public void WorksWithThisServer_NarrowsThePlatformToWhatTheServerRuns(ServerPlatform server, string filter, string expected) =>
        Assert.Equal(expected, string.Join(',', ContentPlatformFilter.ModrinthLoaders(ContentKind.Plugin, server, filter, compatibleOnly: true)));

    [Fact]
    public async Task Modrinth_SearchesOnlyTheChosenPlatform()
    {
        var handler = new CapturingHandler(ModrinthEmpty);
        var provider = new ModrinthContentProvider(Client(handler, ModrinthContentProvider.BaseAddress));

        await provider.SearchAsync(new ContentSearchRequest("luck", Platform: "spigot"), Profile(ServerPlatform.Paper));
        await provider.SearchAsync(new ContentSearchRequest("luck"), Profile(ServerPlatform.Paper));
        await provider.SearchAsync(new ContentSearchRequest("pack", Kind: ContentKind.Modpack, Platform: "fabric"), Profile(ServerPlatform.Paper));

        var spigot = Uri.UnescapeDataString(handler.Requests[0]);
        Assert.Contains("\"categories:spigot\"", spigot, StringComparison.Ordinal);
        Assert.DoesNotContain("categories:paper", spigot, StringComparison.Ordinal);
        Assert.DoesNotContain("categories:bukkit", spigot, StringComparison.Ordinal);
        Assert.Contains("query=luck", handler.Requests[0], StringComparison.Ordinal);

        var automatic = Uri.UnescapeDataString(handler.Requests[1]);
        Assert.Contains("\"categories:paper\",\"categories:spigot\",\"categories:bukkit\"", automatic, StringComparison.Ordinal);

        var modpack = Uri.UnescapeDataString(handler.Requests[2]);
        Assert.Contains("\"categories:fabric\"", modpack, StringComparison.Ordinal);
        Assert.DoesNotContain("categories:forge", modpack, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChoosingFolia_OnAPaperServer_ShowsFoliaPlugins_AsNotFitting()
    {
        const string hit = """
            { "hits": [{ "project_id": "f1", "slug": "folia-only", "title": "Folia Only",
                         "categories": ["folia"], "versions": ["1.21.8"] }],
              "offset": 0, "limit": 20, "total_hits": 1 }
            """;
        var handler = new CapturingHandler(hit);
        var provider = new ModrinthContentProvider(Client(handler, ModrinthContentProvider.BaseAddress));

        var browsing = await provider.SearchAsync(new ContentSearchRequest(CompatibleOnly: false, Platform: "folia"), Profile(ServerPlatform.Paper));
        var worksHere = await provider.SearchAsync(new ContentSearchRequest(CompatibleOnly: true, Platform: "folia"), Profile(ServerPlatform.Paper));

        // Browsing everything shows them honestly marked; "Works with this server" does not ask at all.
        Assert.False(Assert.Single(browsing.Projects).IsCompatible);
        Assert.Empty(worksHere.Projects);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(ServerPlatform.Paper, "spigot", false)]
    [InlineData(ServerPlatform.Paper, "folia", false)]
    [InlineData(ServerPlatform.Paper, "paper", true)]
    [InlineData(ServerPlatform.Paper, null, true)]
    [InlineData(ServerPlatform.Folia, null, false)]
    public async Task Catalog_AsksHangar_OnlyForPaperFamilyFilters(ServerPlatform server, string? filter, bool hangarAsked)
    {
        var modrinth = new CapturingHandler(ModrinthEmpty);
        var hangar = new CapturingHandler("""{ "result": [], "pagination": { "count": 0 } }""");
        var catalog = new ContentCatalogService(
            [
                new ModrinthContentProvider(Client(modrinth, ModrinthContentProvider.BaseAddress)),
                new HangarContentProvider(Client(hangar, "https://hangar.papermc.io/api/v1/"))
            ],
            NullLogger<ContentCatalogService>.Instance);

        var result = await catalog.SearchAsync(new ContentSearchRequest("x", CompatibleOnly: false, Platform: filter), Profile(server));

        Assert.Empty(result.ProviderErrors);
        Assert.Single(modrinth.Requests);
        Assert.Equal(hangarAsked ? 1 : 0, hangar.Requests.Count);
    }

    private const string ModrinthEmpty = """{ "hits": [], "offset": 0, "limit": 20, "total_hits": 0 }""";

    private static ServerContentProfile Profile(ServerPlatform platform) =>
        new(
            Guid.NewGuid(),
            GameType.Minecraft,
            platform,
            "1.21.8",
            null,
            @"C:\servers\mc",
            @"C:\servers\mc\plugins",
            SupportsPlugins: true,
            IsRunning: false);

    private static HttpClient Client(HttpMessageHandler handler, string baseAddress) =>
        new(handler) { BaseAddress = new Uri(baseAddress) };

    private sealed class CapturingHandler(string json) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.PathAndQuery);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }
}
