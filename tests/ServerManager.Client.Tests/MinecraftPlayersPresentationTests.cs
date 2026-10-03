using ServerManager.Client.Controls;
using ServerManager.Contracts;

namespace ServerManager.Client.Tests;

public sealed class MinecraftPlayersPresentationTests
{
    private static readonly MinecraftPlayerProfile Online = Player("Zed", true) with { IsOperator = true };
    private static readonly MinecraftPlayerProfile Offline = Player("Amy", false) with { IsWhitelisted = true };
    private static readonly MinecraftPlayerProfile Unknown = Player("Unverified", null) with { IsBanned = true };
    private static readonly MinecraftPlayerProfile[] Players = [Unknown, Offline, Online];

    [Fact]
    public void SearchesRealNameAndUuidWithoutChangingSource()
    {
        Assert.Equal(Online, Assert.Single(MinecraftPlayersPresentation.Select(Players, "zED", 0, 0)));
        Assert.Equal(Offline, Assert.Single(MinecraftPlayersPresentation.Select(Players, Offline.Uuid.ToString("N"), 0, 0)));
        Assert.Equal(3, Players.Length);
    }

    [Theory]
    [InlineData(1, "Zed")]
    [InlineData(2, "Amy")]
    [InlineData(3, "Zed")]
    [InlineData(4, "Amy")]
    [InlineData(5, "Unverified")]
    public void FiltersDoNotInventUnknownStatus(int filter, string name) =>
        Assert.Equal(name, Assert.Single(MinecraftPlayersPresentation.Select(Players, "", filter, 0)).Username);

    [Fact]
    public void OnlineFirstAndNameSortRemainStable()
    {
        Assert.Equal(Online, MinecraftPlayersPresentation.Select(Players, "", 0, 0)[0]);
        Assert.Equal(Offline, MinecraftPlayersPresentation.Select(Players, "", 0, 1)[0]);
    }

    [Fact]
    public void UnknownDatesSortLastAndKnownDatesUseTheirRealOrder()
    {
        var first = Offline with { FirstJoinedAtUtc = DateTimeOffset.UtcNow.AddDays(-3), LastSeenAtUtc = DateTimeOffset.UtcNow.AddDays(-1) };
        var recent = Online with { FirstJoinedAtUtc = DateTimeOffset.UtcNow.AddDays(-1), LastSeenAtUtc = DateTimeOffset.UtcNow };
        Assert.Equal(new[] { first, recent, Unknown }, MinecraftPlayersPresentation.Select([Unknown, recent, first], "", 0, 3));
        Assert.Equal(new[] { recent, first, Unknown }, MinecraftPlayersPresentation.Select([Unknown, recent, first], "", 0, 2));
    }

    [Fact]
    public void StaleCountPreservesNumberAndUnknownNeverBecomesZero()
    {
        Assert.StartsWith("7 / 20 · ", MinecraftPlayersPresentation.Count(7, 20, true));
        Assert.Equal("7 / 20", MinecraftPlayersPresentation.Count(7, 20, false));
        Assert.StartsWith("—", MinecraftPlayersPresentation.Count(null, 20, true));
    }

    private static MinecraftPlayerProfile Player(string name, bool? online) =>
        new(Guid.NewGuid(), name, online, null, null, null, null, null, null, null);
}
