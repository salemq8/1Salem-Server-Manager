using ServerManager.Core;

namespace ServerManager.Core.Tests;

public sealed class GameVersionComparerTests
{
    [Theory]
    [InlineData("1.21.8", "1.21.7", 1)]
    [InlineData("1.21.0", "1.21", 1)]
    [InlineData("1.20.6", "1.21.0", -1)]
    [InlineData("123456", "123455", 1)]
    public void Compare_OrdersNumericVersionParts(string left, string right, int expectedSign)
    {
        Assert.Equal(expectedSign, Math.Sign(GameVersionComparer.Compare(left, right)));
    }
}
