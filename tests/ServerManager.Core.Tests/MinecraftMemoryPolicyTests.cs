using ServerManager.Core;

namespace ServerManager.Core.Tests;

public sealed class MinecraftMemoryPolicyTests
{
    private const long Gibibyte = 1024L * 1024 * 1024;

    [Fact]
    public void Evaluate_EightGigabytes_RecommendsAtMostFourGigabytes()
    {
        var result = MinecraftMemoryPolicy.Evaluate(
            8 * Gibibyte,
            6 * Gibibyte,
            2048,
            4096);

        Assert.True(result.IsSafe);
        Assert.InRange(result.RecommendedMaximumMemoryMb, 3072, 4096);
        Assert.True(result.WindowsReserveMemoryMb >= 2048);
    }

    [Fact]
    public void Evaluate_SixteenGigabytes_UsesAvailableMemory()
    {
        var constrained = MinecraftMemoryPolicy.Evaluate(
            16 * Gibibyte,
            5 * Gibibyte,
            1024,
            3072);
        var available = MinecraftMemoryPolicy.Evaluate(
            16 * Gibibyte,
            14 * Gibibyte,
            2048,
            6144);

        Assert.True(constrained.MaximumSafeMemoryMb < available.MaximumSafeMemoryMb);
        Assert.InRange(available.RecommendedMaximumMemoryMb, 6144, 8192);
    }

    [Fact]
    public void Evaluate_RejectsXmxBelowXms()
    {
        var result = MinecraftMemoryPolicy.Evaluate(
            16 * Gibibyte,
            12 * Gibibyte,
            4096,
            2048);

        Assert.False(result.IsSafe);
        Assert.Contains(result.Warnings, warning => warning.Contains("Xmx", StringComparison.Ordinal));
    }

    [Fact]
    public void Evaluate_RejectsAllocatingWindowsReserve()
    {
        var result = MinecraftMemoryPolicy.Evaluate(
            8 * Gibibyte,
            7 * Gibibyte,
            2048,
            7168);

        Assert.False(result.IsSafe);
        Assert.Contains(
            result.Warnings,
            warning => warning.Contains("Windows", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void ShouldApplyRestart_RequiresRequestAndRunningProcess(
        bool requested,
        bool running,
        bool expected) =>
        Assert.Equal(
            expected,
            MinecraftMemoryPolicy.ShouldApplyRestart(requested, running));
}
