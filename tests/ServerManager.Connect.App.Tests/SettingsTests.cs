using ServerManager.Connect.App.Configuration;

namespace ServerManager.Connect.App.Tests;

public sealed class SettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "1salem-connect-app-tests", Guid.NewGuid().ToString("N"));

    public SettingsTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void Missing_file_means_not_configured_without_a_problem()
    {
        var result = ConnectAppSettingsLoader.Load(Path.Combine(_directory, ConnectAppSettingsLoader.FileName));

        Assert.False(result.Settings.IsConfigured);
        Assert.Null(result.Problem);
    }

    [Fact]
    public void Https_broker_configures_tsnet_with_the_transport_next_to_the_app()
    {
        var result = Load("""{ "brokerUrl": "https://broker.example/" }""");

        Assert.Null(result.Problem);
        Assert.True(result.Settings.IsConfigured);
        Assert.Equal(TransportMode.Tsnet, result.Settings.TransportMode);
        Assert.Equal(Path.Combine(_directory, "1Salem.Connect.Transport.exe"), result.Settings.TransportExecutablePath);
    }

    [Fact]
    public void Local_development_broker_with_the_fake_transport_is_accepted()
    {
        var result = Load("""{ "brokerUrl": "http://127.0.0.1:8787/", "developmentMode": true, "transportMode": "fake", "fakeNodeId": "fake-friend-1" }""");

        Assert.Null(result.Problem);
        Assert.Equal(TransportMode.Fake, result.Settings.TransportMode);
        Assert.Equal("fake-friend-1", result.Settings.FakeNodeId);
    }

    [Theory]
    [InlineData("""{ "brokerUrl": "http://broker.example/", "developmentMode": true }""")]
    [InlineData("""{ "brokerUrl": "http://127.0.0.1:8787/" }""")]
    [InlineData("""{ "brokerUrl": "https://broker.example/", "transportMode": "fake", "fakeNodeId": "f" }""")]
    [InlineData("""{ "brokerUrl": "https://broker.example/", "developmentMode": true, "transportMode": "fake" }""")]
    [InlineData("""{ "brokerUrl": "https://broker.example/", "developmentMod": true }""")]
    [InlineData("""{ "brokerUrl": "https://broker.example/", "brokerUrl": "https://other.example/" }""")]
    [InlineData("""{ "brokerUrl": 5 }""")]
    [InlineData("""not json""")]
    public void Anything_doubtful_leaves_the_app_unconfigured_with_a_reason(string json)
    {
        var result = Load(json);

        Assert.False(result.Settings.IsConfigured);
        Assert.NotNull(result.Problem);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private SettingsLoadResult Load(string json)
    {
        var path = Path.Combine(_directory, ConnectAppSettingsLoader.FileName);
        File.WriteAllText(path, json);
        return ConnectAppSettingsLoader.Load(path);
    }
}
