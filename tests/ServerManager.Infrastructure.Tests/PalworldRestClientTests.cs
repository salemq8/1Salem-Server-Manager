using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ServerManager.Contracts;
using ServerManager.Core;
using ServerManager.Infrastructure.Games.Palworld;

namespace ServerManager.Infrastructure.Tests;

public sealed class PalworldRestClientTests : IDisposable
{
    private const string AdminPassword = "not-for-logs";
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "1SalemServerManager.Rest.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Snapshot_ParsesOfficialInfoMetricsAndPlayersOverLoopback()
    {
        var server = await CreateServerAsync(true);
        var handler = new OfficialApiHandler();
        var client = new PalworldRestClient(
            new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) },
            new PlainSecretStore());

        var snapshot = await client.GetSnapshotAsync(server);

        Assert.Equal(PalworldManagementState.Online, snapshot.State);
        Assert.Equal("1Salem Palworld", snapshot.ServerName);
        Assert.Equal("Live test", snapshot.Description);
        Assert.Equal(58.7, snapshot.ServerFps);
        Assert.Equal(1, snapshot.PlayersOnline);
        Assert.Equal(32, snapshot.MaximumPlayers);
        Assert.Equal(3661, snapshot.UptimeSeconds);
        var player = Assert.Single(snapshot.Players);
        Assert.Equal("Salem", player.Name);
        Assert.Equal(42, player.Level);
        Assert.Equal(37.5, player.PingMilliseconds);
        Assert.Equal(3, handler.Requests.Count);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal("127.0.0.1", request.Uri.Host);
            Assert.Equal("Basic", request.Authorization?.Scheme);
            Assert.Equal(
                Convert.ToBase64String(
                    Encoding.UTF8.GetBytes($"admin:{AdminPassword}")),
                request.Authorization?.Parameter);
        });
        Assert.DoesNotContain(
            AdminPassword,
            JsonSerializer.Serialize(snapshot),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Snapshot_WhenDisabled_DoesNotMakeNetworkRequest()
    {
        var server = await CreateServerAsync(false);
        var handler = new OfficialApiHandler();
        var client = new PalworldRestClient(
            new HttpClient(handler),
            new PlainSecretStore());

        var snapshot = await client.GetSnapshotAsync(server);

        Assert.Equal(PalworldManagementState.Disabled, snapshot.State);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Snapshot_WhenCredentialsAreRejected_IsExplicitlyUnavailable()
    {
        var server = await CreateServerAsync(true);
        var client = new PalworldRestClient(
            new HttpClient(new UnauthorizedHandler()),
            new PlainSecretStore());

        var snapshot = await client.GetSnapshotAsync(server);

        Assert.Equal(PalworldManagementState.Unavailable, snapshot.State);
        Assert.Contains(
            "rejected",
            snapshot.StatusMessage,
            StringComparison.OrdinalIgnoreCase);
        Assert.Empty(snapshot.Players);
    }

    [Fact]
    public async Task SaveWorld_UsesOfficialLocalPostAndReportsSuccess()
    {
        var server = await CreateServerAsync(true);
        var handler = new OfficialApiHandler();
        var client = new PalworldRestClient(
            new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) },
            new PlainSecretStore());

        var result = await client.SaveWorldAsync(server);

        Assert.True(result.Success);
        Assert.Equal("WorldSaved", result.Code);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("127.0.0.1", request.Uri.Host);
        Assert.EndsWith("/save", request.Uri.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveWorld_WhenUnauthorizedReturnsExactFailure()
    {
        var server = await CreateServerAsync(true);
        var client = new PalworldRestClient(
            new HttpClient(new UnauthorizedHandler()),
            new PlainSecretStore());

        var result = await client.SaveWorldAsync(server);

        Assert.False(result.Success);
        Assert.Equal("Unauthorized", result.Code);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private async Task<GameServerDefinition> CreateServerAsync(bool restEnabled)
    {
        Directory.CreateDirectory(Path.Combine(_root, ".1salem"));
        var metadata = new PalworldServerMetadata(
            "PalworldVanilla",
            2_394_010,
            "build",
            "join",
            AdminPassword,
            new PalworldServerSettingsTemplate(
                "1Salem Palworld",
                "Live test",
                32,
                8211,
                false,
                false,
                25575,
                restEnabled,
                8212),
            DateTimeOffset.UtcNow);
        await File.WriteAllTextAsync(
            Path.Combine(_root, ".1salem", "metadata.json"),
            JsonSerializer.Serialize(
                metadata,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return new GameServerDefinition(
            Guid.NewGuid(),
            GameType.Palworld,
            "1Salem Palworld",
            _root,
            8211,
            "build",
            DateTimeOffset.UtcNow);
    }

    private sealed class PlainSecretStore : ISecretStore
    {
        public string Protect(string plaintext) => plaintext;

        public string Unprotect(string protectedValue) => protectedValue;
    }

    private sealed class OfficialApiHandler : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new CapturedRequest(
                request.RequestUri!,
                request.Headers.Authorization,
                request.Method));
            var json = request.RequestUri!.AbsolutePath.EndsWith("/info")
                ? """
                  {"version":"0.6.7","servername":"1Salem Palworld","description":"Live test","worldguid":"world-1"}
                  """
                : request.RequestUri.AbsolutePath.EndsWith("/metrics")
                    ? """
                      {"serverfps":58.7,"currentplayernum":1,"serverframetime":16.4,"maxplayernum":32,"uptime":3661}
                      """
                    : """
                      {"players":[{"name":"Salem","accountName":"SalemAccount","playerId":"p1","userId":"u1","ip":"127.0.0.1","ping":37.5,"level":42,"location":{"x":10.5,"y":20.5}}]}
                      """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    json,
                    Encoding.UTF8,
                    "application/json")
            });
        }
    }

    private sealed class UnauthorizedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
    }

    private sealed record CapturedRequest(
        Uri Uri,
        AuthenticationHeaderValue? Authorization,
        HttpMethod Method);
}
