using System.Diagnostics;
using ServerManager.Contracts;
using ServerManager.Core;

namespace ServerManager.Core.Tests;

/// <summary>
/// With several servers on one machine, two of them must never be set up to listen on the
/// same port: the second would simply fail to start, long after anyone was watching.
/// </summary>
public sealed class ServerPortAllocationPolicyTests
{
    private static GameServerDefinition Server(string name, int port, GameType game = GameType.Minecraft) =>
        new(
            Guid.NewGuid(),
            game,
            name,
            $@"C:\servers\{name}",
            port,
            "1.21.8",
            DateTimeOffset.UtcNow);

    [Fact]
    public void APortAlreadyUsedByAnotherServerIsReported()
    {
        var existing = new[] { Server("Survival", 25565), Server("Palworld", 8211, GameType.Palworld) };

        var conflict = ServerPortAllocationPolicy.FindConflict(existing, 25565);

        Assert.NotNull(conflict);
        Assert.Equal("Survival", conflict.ServerName);
        Assert.Null(ServerPortAllocationPolicy.FindConflict(existing, 25566));
    }

    [Fact]
    public void AServerDoesNotClashWithItself()
    {
        var survival = Server("Survival", 25565);

        Assert.Null(ServerPortAllocationPolicy.FindConflict([survival], 25565, survival.Id));
        Assert.NotNull(ServerPortAllocationPolicy.FindConflict([survival], 25565, Guid.NewGuid()));
    }

    [Fact]
    public void ClashesAreFoundAcrossGamesNotJustWithinOne()
    {
        var palworld = Server("Palworld", 25565, GameType.Palworld);

        // Two different games on one port collide just as surely as two Minecraft servers.
        Assert.NotNull(ServerPortAllocationPolicy.FindConflict([palworld], 25565));
    }

    [Fact]
    public void AFreePortIsSuggestedRatherThanATakenOne()
    {
        var existing = new[]
        {
            Server("A", 25565),
            Server("B", 25566),
            Server("C", 25567)
        };

        var suggestion = ServerPortAllocationPolicy.SuggestPort(existing, 25565);

        Assert.Equal(25568, suggestion);
        Assert.Null(ServerPortAllocationPolicy.FindConflict(existing, suggestion!.Value));
    }

    [Fact]
    public void TheSuggestionRespectsTheAllowedRange()
    {
        Assert.True(ServerPortAllocationPolicy.IsInValidRange(25565));
        Assert.False(ServerPortAllocationPolicy.IsInValidRange(80));
        Assert.False(ServerPortAllocationPolicy.IsInValidRange(70000));

        // An out-of-range preference falls back to the usual Minecraft port.
        Assert.Equal(25565, ServerPortAllocationPolicy.SuggestPort([], 80));
    }
}
