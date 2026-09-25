using ServerManager.Infrastructure.Connect;

namespace ServerManager.Infrastructure.Tests;

/// <summary>
/// The host transport's configuration is checked by the same rules the sidecar applies to its
/// flags, so a bad value fails once, clearly, instead of in a restart loop.
/// </summary>
public sealed class ConnectHostTransportOptionsTests
{
    private static readonly string Executable = Path.Combine(Path.GetTempPath(), "1Salem.Connect.Host.Transport.exe");
    private static readonly string StateDirectory = Path.Combine(Path.GetTempPath(), "1salem-connect-state");

    [Theory]
    [InlineData("127.0.0.1:17780")]
    [InlineData("[::1]:17780")]
    public void FakeMode_AcceptsALoopbackAddress(string bridgeListen) =>
        Assert.Equal(bridgeListen, new ConnectHostTransportOptions(Executable, ConnectTransportMode.Fake, bridgeListen).BridgeListen);

    [Theory]
    [InlineData("0.0.0.0:17780")]
    [InlineData("192.168.1.20:17780")]
    [InlineData("100.64.0.7:7780")]
    [InlineData("127.0.0.1:0")]
    [InlineData("127.0.0.1")]
    [InlineData("127.1:17780")]
    [InlineData(":17780")]
    [InlineData("localhost:17780")]
    [InlineData("")]
    public void FakeMode_RefusesAnythingButACanonicalLoopbackIpAndPort(string bridgeListen) =>
        Assert.ThrowsAny<ArgumentException>(() =>
            new ConnectHostTransportOptions(Executable, ConnectTransportMode.Fake, bridgeListen));

    [Fact]
    public void FakeMode_KeepsNoNodeState() =>
        Assert.Throws<ArgumentException>(() =>
            new ConnectHostTransportOptions(Executable, ConnectTransportMode.Fake, "127.0.0.1:17780", StateDirectory));

    [Theory]
    [InlineData(":7780")]
    [InlineData("100.64.0.7:7780")]
    [InlineData("100.127.255.254:7780")]
    [InlineData("[fd7a:115c:a1e0::7]:7780")]
    public void TsnetMode_AcceptsEveryTailnetAddressOrATailscaleIp(string bridgeListen) =>
        Assert.Equal(
            bridgeListen,
            new ConnectHostTransportOptions(Executable, ConnectTransportMode.Tsnet, bridgeListen, StateDirectory).BridgeListen);

    [Theory]
    [InlineData("0.0.0.0:7780")]
    [InlineData("127.0.0.1:7780")]
    [InlineData("100.128.0.1:7780")]
    [InlineData("192.168.1.20:7780")]
    [InlineData("[fd7a:115c:a1e1::7]:7780")]
    [InlineData(":0")]
    [InlineData(":70000")]
    public void TsnetMode_RefusesWildcardsLoopbackAndOtherNetworks(string bridgeListen) =>
        Assert.Throws<ArgumentException>(() =>
            new ConnectHostTransportOptions(Executable, ConnectTransportMode.Tsnet, bridgeListen, StateDirectory));

    [Theory]
    [InlineData(null)]
    [InlineData("relative\\state")]
    public void TsnetMode_NeedsAFullyQualifiedStateDirectory(string? stateDirectory) =>
        Assert.Throws<ArgumentException>(() =>
            new ConnectHostTransportOptions(Executable, ConnectTransportMode.Tsnet, ":7780", stateDirectory));

    [Theory]
    [InlineData("1Salem.Connect.Host.Transport.exe")]
    [InlineData("")]
    public void TheExecutable_IsNeverResolvedThroughPath(string executable) =>
        Assert.Throws<ArgumentException>(() =>
            new ConnectHostTransportOptions(executable, ConnectTransportMode.Fake, "127.0.0.1:17780"));

    [Theory]
    [InlineData(@"\\.\pipe\1Salem.Connect.HostAuthz.v1")]
    [InlineData("1Salem Connect")]
    [InlineData("name/with/slashes")]
    [InlineData("")]
    public void TheAuthorizationPipe_MustBeANameTheSidecarAccepts(string pipeName) =>
        Assert.Throws<ArgumentException>(() =>
            new ConnectHostTransportOptions(Executable, ConnectTransportMode.Fake, "127.0.0.1:17780", null, pipeName));
}
