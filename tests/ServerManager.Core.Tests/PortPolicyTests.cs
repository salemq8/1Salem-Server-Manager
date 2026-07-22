using ServerManager.Core;

namespace ServerManager.Core.Tests;

public sealed class PortPolicyTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(25565)]
    [InlineData(65535)]
    public void Validate_AcceptsTcpUdpRange(int port) =>
        Assert.Equal(port, PortPolicy.Validate(port));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void Validate_RejectsOutOfRange(int port) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => PortPolicy.Validate(port));

    [Fact]
    public void ValidateDistinct_RejectsCollision() =>
        Assert.Throws<ArgumentException>(
            () => PortPolicy.ValidateDistinct(25565, 25565));
}
