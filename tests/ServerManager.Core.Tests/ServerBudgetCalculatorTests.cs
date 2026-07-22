using System.Diagnostics;
using ServerManager.Contracts;
using ServerManager.Core;

namespace ServerManager.Core.Tests;

public sealed class ServerBudgetCalculatorTests
{
    private const long GiB = ResourcePolicyCatalog.Gibibyte;

    [Fact]
    public void NoRegisteredMinecraft_ConsumesZeroBudgetAndPalworldCanStart()
    {
        var palworld = Server(GameType.Palworld);
        var staleMinecraft = Process(
            Guid.NewGuid(),
            ServerState.Running,
            4 * GiB);
        var result = Evaluate(
            palworld,
            [palworld],
            [staleMinecraft]);

        Assert.True(result.IsSafe, result.Reason);
        Assert.Equal(0, result.ActiveManagedServerMemoryBytes);
        Assert.Empty(result.ActiveServers);
    }

    [Fact]
    public void StoppedMinecraft_ConsumesZeroBudget()
    {
        var palworld = Server(GameType.Palworld);
        var minecraft = Server(GameType.Minecraft);
        var result = Evaluate(
            palworld,
            [palworld, minecraft],
            [Process(minecraft.Id, ServerState.Stopped, 5 * GiB)]);

        Assert.Equal(0, result.ActiveManagedServerMemoryBytes);
        Assert.True(result.IsSafe, result.Reason);
    }

    [Fact]
    public void StaleExitedMetrics_AreIgnored()
    {
        var palworld = Server(GameType.Palworld);
        var minecraft = Server(GameType.Minecraft);
        var stale = Process(
            minecraft.Id,
            ServerState.Running,
            5 * GiB) with
        {
            ExitCode = 0
        };

        var result = Evaluate(
            palworld,
            [palworld, minecraft],
            [stale]);

        Assert.Equal(0, result.ActiveManagedServerMemoryBytes);
        Assert.True(result.IsSafe, result.Reason);
    }

    [Fact]
    public void RunningRegisteredMinecraft_UsesActualWorkingSetOnly()
    {
        var palworld = Server(GameType.Palworld);
        var minecraft = Server(GameType.Minecraft);
        var result = Evaluate(
            palworld,
            [palworld, minecraft],
            [Process(minecraft.Id, ServerState.Running, 2 * GiB)]);

        Assert.Equal(2 * GiB, result.ActiveManagedServerMemoryBytes);
        Assert.Single(result.ActiveServers);
    }

    [Fact]
    public void ValidSixGiBReserveAndSevenPointFiveGiBBudget_AllowsPalworld()
    {
        var palworld = Server(GameType.Palworld);
        var policy = ResourcePolicyCatalog.Create(
            ResourceMode.Custom,
            16 * GiB,
            6 * GiB,
            15 * GiB / 2,
            palworldWarningThresholdBytes: 5 * GiB,
            palworldCriticalThresholdBytes: 7 * GiB);

        var result = Evaluate(
            palworld,
            [palworld],
            [],
            policy);

        Assert.True(result.IsSafe, result.Reason);
        Assert.Equal(15 * GiB / 2, result.BudgetRemainingBytes);
    }

    [Fact]
    public void UnsafeOverride_IsAvailableOnlyWhenPolicyAllowsIt()
    {
        var palworld = Server(GameType.Palworld);
        var blocked = ResourcePolicyCatalog.Create(
            ResourceMode.Custom,
            16 * GiB,
            12 * GiB,
            3 * GiB,
            palworldWarningThresholdBytes: 2 * GiB,
            palworldCriticalThresholdBytes: 3 * GiB);
        var allowed = blocked with { AllowUnsafeStartupOverride = true };

        Assert.False(Evaluate(palworld, [palworld], [], blocked).CanStartAnywayOnce);
        Assert.True(Evaluate(palworld, [palworld], [], allowed).CanStartAnywayOnce);
    }

    [Fact]
    public void ProfileSummary_OnlyNamesRegisteredGames()
    {
        var policy = ResourcePolicyCatalog.Create(
            ResourceMode.Balanced,
            16 * GiB);

        Assert.Equal(
            "Active: Balanced · No managed game servers",
            ResourceProfileSummary.Build(policy, []));
        Assert.Equal(
            "Active: Balanced · Palworld Normal",
            ResourceProfileSummary.Build(policy, [GameType.Palworld]));
        Assert.Equal(
            "Active: Balanced · Palworld Normal · Minecraft Normal",
            ResourceProfileSummary.Build(
                policy,
                [GameType.Minecraft, GameType.Palworld]));
    }

    [Fact]
    public void DisplayRoundingTolerance_ClampsHarmlessOverage()
    {
        var total = (long)(15.71 * GiB);
        var reserve = 3 * GiB;
        var displayedBudget = (long)(12.75 * GiB);

        var policy = ResourcePolicyCatalog.Create(
            ResourceMode.Custom,
            total,
            reserve,
            displayedBudget,
            palworldWarningThresholdBytes: 5 * GiB,
            palworldCriticalThresholdBytes: 7 * GiB);

        Assert.Equal(total - reserve, policy.MaximumServerBudgetBytes);
        Assert.True(
            policy.WindowsReserveBytes + policy.MaximumServerBudgetBytes <=
            total);
    }

    private static ServerStartBudgetSnapshot Evaluate(
        GameServerDefinition target,
        IReadOnlyCollection<GameServerDefinition> registered,
        IReadOnlyCollection<ProcessSnapshot> processes,
        ResourcePolicy? policy = null)
    {
        policy ??= ResourcePolicyCatalog.Create(
            ResourceMode.Custom,
            16 * GiB,
            6 * GiB,
            15 * GiB / 2,
            palworldWarningThresholdBytes: 5 * GiB,
            palworldCriticalThresholdBytes: 7 * GiB);
        var system = new SystemResourceSnapshot(
            DateTimeOffset.UtcNow,
            16 * GiB,
            6 * GiB,
            0,
            100 * GiB,
            policy,
            []);
        return ServerBudgetCalculator.Evaluate(
            target,
            policy,
            system,
            registered,
            processes);
    }

    private static GameServerDefinition Server(GameType game) =>
        new(
            Guid.NewGuid(),
            game,
            game.ToString(),
            @"C:\Server",
            game == GameType.Palworld ? 8211 : 25565,
            "1",
            DateTimeOffset.UtcNow,
            MaximumMemoryMb: game == GameType.Minecraft ? 4096 : null);

    private static ProcessSnapshot Process(
        Guid serverId,
        ServerState state,
        long workingSet) =>
        new(
            serverId,
            1234,
            state,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            workingSet,
            1,
            null,
            Priority: ProcessPriorityClass.Normal);
}
