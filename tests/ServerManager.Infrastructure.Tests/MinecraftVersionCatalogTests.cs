using System.Net;
using System.Text;
using ServerManager.Infrastructure.Games.Minecraft;

namespace ServerManager.Infrastructure.Tests;

public sealed class MinecraftVersionCatalogTests
{
    [Fact]
    public async Task GetVersionAsync_ResolvesOfficialServerArtifactAndJavaVersion()
    {
        const string manifest = """
            {
              "latest": { "release": "1.21.8", "snapshot": "25w01a" },
              "versions": [
                {
                  "id": "1.21.8",
                  "type": "release",
                  "url": "https://example.test/1.21.8.json",
                  "sha1": "metadata"
                }
              ]
            }
            """;
        const string metadata = """
            {
              "downloads": {
                "server": {
                  "sha1": "abcdef",
                  "size": 12345,
                  "url": "https://example.test/server.jar"
                }
              },
              "javaVersion": { "majorVersion": 21 }
            }
            """;
        var client = new HttpClient(new RouteHandler(manifest, metadata));
        var catalog = new MinecraftVersionCatalog(client);

        var version = await catalog.GetVersionAsync("1.21.8");

        Assert.Equal("1.21.8", version.Id);
        Assert.Equal(21, version.RequiredJavaMajor);
        Assert.Equal("abcdef", version.Sha1);
        Assert.Equal(12345, version.SizeBytes);
        Assert.Equal(
            new Uri("https://example.test/server.jar"),
            version.ServerDownloadUrl);
    }

    private sealed class RouteHandler(string manifest, string metadata) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var json = request.RequestUri == MinecraftVersionCatalog.ManifestUri
                ? manifest
                : metadata;
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                });
        }
    }
}
