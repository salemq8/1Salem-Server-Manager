using System.Text;
using System.Text.Json;
using ServerManager.Contracts;
using ServerManager.Core;
using ServerManager.Infrastructure.Games.Palworld;

namespace ServerManager.Infrastructure.Tests;

public sealed class PalworldServerProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "1SalemServerManager.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task PrepareAndCleanup_PreservesManagedConfiguration()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Pal", "Saved"));
        Directory.CreateDirectory(Path.Combine(_root, ".1salem"));
        await File.WriteAllTextAsync(Path.Combine(_root, "PalServer.exe"), "exe");
        var secrets = new EncodingSecretStore();
        var metadata = new PalworldServerMetadata(
            "PalworldVanilla",
            2_394_010,
            "123",
            secrets.Protect("join-secret"),
            secrets.Protect("admin-secret"),
            new PalworldServerSettingsTemplate(
                "1Salem",
                "Test",
                32,
                8211,
                true,
                false,
                25575),
            DateTimeOffset.UtcNow);
        await File.WriteAllTextAsync(
            Path.Combine(_root, ".1salem", "metadata.json"),
            JsonSerializer.Serialize(metadata, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var provider = new PalworldServerProvider(secrets);
        var server = new GameServerDefinition(
            Guid.NewGuid(),
            GameType.Palworld,
            "1Salem",
            _root,
            8211,
            "123",
            DateTimeOffset.UtcNow);

        await provider.PrepareForStartAsync(server);
        var config = Path.Combine(
            _root,
            "Pal",
            "Saved",
            "Config",
            "WindowsServer",
            "PalWorldSettings.ini");
        Assert.Contains("admin-secret", await File.ReadAllTextAsync(config), StringComparison.Ordinal);
        var spec = provider.CreateLaunchSpec(server);
        Assert.Contains("-port=8211", spec.Arguments, StringComparison.Ordinal);
        Assert.Contains("-publiclobby", spec.Arguments, StringComparison.Ordinal);

        await provider.CleanupAfterStopAsync(server);
        Assert.True(File.Exists(config));
        Assert.Contains(
            "admin-secret",
            await File.ReadAllTextAsync(config),
            StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private sealed class EncodingSecretStore : ISecretStore
    {
        public string Protect(string plaintext) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext));

        public string Unprotect(string protectedValue) =>
            Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue));
    }
}
