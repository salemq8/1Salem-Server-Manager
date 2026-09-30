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
    [InlineData("auto", true)]
    [InlineData("all", true)]
    [InlineData("paper", true)]
    [InlineData("purpur", true)]
    [InlineData("spigot", false)]
    [InlineData("bukkit", false)]
    [InlineData("folia", false)]
    public void Hangar_IsAskedOnlyWhereItsPaperFilesApply(string filter, bool expected) =>
        Assert.Equal(expected, ContentPlatformFilter.IncludesHangar(ContentKind.Plugin, filter));

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
        var provider = new ModrinthContentProvider(Client(new CapturingHandler(hit), ModrinthContentProvider.BaseAddress));

        var result = await provider.SearchAsync(new ContentSearchRequest(Platform: "folia"), Profile(ServerPlatform.Paper));

        var project = Assert.Single(result.Projects);
        Assert.False(project.IsCompatible);
    }

    [Theory]
    [InlineData("spigot", false)]
    [InlineData("folia", false)]
    [InlineData("paper", true)]
    [InlineData(null, true)]
    public async Task Catalog_AsksHangar_OnlyForPaperFamilyFilters(string? filter, bool hangarAsked)
    {
        var modrinth = new CapturingHandler(ModrinthEmpty);
        var hangar = new CapturingHandler("""{ "result": [], "pagination": { "count": 0 } }""");
        var catalog = new ContentCatalogService(
            [
                new ModrinthContentProvider(Client(modrinth, ModrinthContentProvider.BaseAddress)),
                new HangarContentProvider(Client(hangar, "https://hangar.papermc.io/api/v1/"))
            ],
            NullLogger<ContentCatalogService>.Instance);

        var result = await catalog.SearchAsync(new ContentSearchRequest("x", Platform: filter), Profile(ServerPlatform.Paper));

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
