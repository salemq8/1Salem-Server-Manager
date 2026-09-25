using ServerManager.Connect.Core.Diagnostics;

namespace ServerManager.Connect.Tests;

public sealed class NetworkConfigSnapshotTests
{
    [Fact]
    public async Task TwoReads_OfAnUnchangedSystem_AreIdentical()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var first = await NetworkConfigSnapshot.CaptureAsync(cancellation.Token);
        var second = await NetworkConfigSnapshot.CaptureAsync(cancellation.Token);

        Assert.Equal(first.Text, second.Text);
        Assert.Contains("[adapter ", first.Text, StringComparison.Ordinal);
        Assert.Contains("[wininet HKCU]", first.Text, StringComparison.Ordinal);
        Assert.Contains("[winhttp", first.Text, StringComparison.Ordinal);
        Assert.Contains("[environment]", first.Text, StringComparison.Ordinal);
        Assert.Contains("Process:HTTPS_PROXY=", first.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void WinHttpQueries_OnlyEverShow()
    {
        Assert.NotEmpty(NetworkConfigSnapshot.WinHttpQueries);
        Assert.All(NetworkConfigSnapshot.WinHttpQueries, arguments =>
        {
            Assert.Equal("winhttp", arguments[0]);
            Assert.Equal("show", arguments[1]);
            Assert.DoesNotContain(arguments, argument =>
                argument.Contains("set", StringComparison.OrdinalIgnoreCase) ||
                argument.Contains("import", StringComparison.OrdinalIgnoreCase) ||
                argument.Contains("reset", StringComparison.OrdinalIgnoreCase));
        });
    }

    [Theory]
    [InlineData("http://alice:hunter2@proxy.corp:8080", "http://[REDACTED]@proxy.corp:8080")]
    [InlineData("socks5://token@10.0.0.1:1080", "socks5://[REDACTED]@10.0.0.1:1080")]
    [InlineData("proxy.corp:8080", "proxy.corp:8080")]
    [InlineData("http=proxy:80;https=proxy:443", "http=proxy:80;https=proxy:443")]
    public void ProxyCredentials_AreMasked(string value, string expected) =>
        Assert.Equal(expected, NetworkConfigSnapshot.Mask(value));
}
