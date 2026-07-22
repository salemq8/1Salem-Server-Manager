using System.Diagnostics;
using ServerManager.Contracts;
using ServerManager.Core;

namespace ServerManager.Core.Tests;

public sealed class ResourcePolicyCatalogTests
{
    private const long TotalMemory = 16 * ResourcePolicyCatalog.Gibibyte;

    [Theory]
    [InlineData(
        ResourceMode.Balanced,
        ProcessPriorityClass.Normal,
        ProcessPriorityClass.Normal)]
    [InlineData(
        ResourceMode.MinecraftPriority,
        ProcessPriorityClass.AboveNormal,
        ProcessPriorityClass.BelowNormal)]
    [InlineData(
        ResourceMode.PalworldPriority,
        ProcessPriorityClass.BelowNormal,
        ProcessPriorityClass.AboveNormal)]
    [InlineData(
        ResourceMode.OneGameAtATime,
        ProcessPriorityClass.Normal,
        ProcessPriorityClass.Normal)]
    public void Create_UsesSafeProfilePriorities(
        ResourceMode mode,
        ProcessPriorityClass minecraft,
        ProcessPriorityClass palworld)
    {
        var policy = ResourcePolicyCatalog.Create(mode, TotalMemory);

        Assert.Equal(minecraft, policy.MinecraftPriority);
        Assert.Equal(palworld, policy.PalworldPriority);
        Assert.NotEqual(ProcessPriorityClass.RealTime, policy.MinecraftPriority);
        Assert.NotEqual(ProcessPriorityClass.RealTime, policy.PalworldPriority);
    }

    [Fact]
    public void Validate_RejectsMemoryOvercommit()
    {
        var policy = new ResourcePolicy(
            ResourceMode.Custom,
            ProcessPriorityClass.Normal,
            ProcessPriorityClass.Normal,
            8 * ResourcePolicyCatalog.Gibibyte,
            12 * ResourcePolicyCatalog.Gibibyte);

        Assert.Throws<ArgumentException>(
            () => ResourcePolicyCatalog.Validate(policy, TotalMemory));
    }

    [Fact]
    public void Create_RejectsRealtimeCustomPriority()
    {
        Assert.Throws<ArgumentException>(
            () => ResourcePolicyCatalog.Create(
                ResourceMode.Custom,
                TotalMemory,
                minecraftPriority: ProcessPriorityClass.RealTime));
    }

    [Fact]
    public void Validate_RejectsEmptyCpuAffinity()
    {
        var policy = ResourcePolicyCatalog.Create(ResourceMode.Balanced, TotalMemory) with
        {
            CpuAffinityMask = 0
        };

        Assert.Throws<ArgumentOutOfRangeException>(
            () => ResourcePolicyCatalog.Validate(policy, TotalMemory));
    }

    [Fact]
    public void SafePreset_ReservesWindowsMemoryAndOrdersWarningBeforeCritical()
    {
        var policy = ResourcePolicyCatalog.Create(
            ResourceMode.Safe,
            TotalMemory);

        Assert.True(policy.WindowsReserveBytes >= 4 * ResourcePolicyCatalog.Gibibyte);
        Assert.True(policy.PalworldWarningThresholdBytes >= ResourcePolicyCatalog.Gibibyte);
        Assert.True(
            policy.PalworldCriticalThresholdBytes >=
            policy.PalworldWarningThresholdBytes);
        Assert.True(
            policy.WindowsReserveBytes + policy.MaximumServerBudgetBytes <=
            TotalMemory);
    }

    [Fact]
    public void Validate_RejectsZeroWindowsReserveAndCriticalBelowWarning()
    {
        var policy = ResourcePolicyCatalog.Create(
            ResourceMode.Balanced,
            TotalMemory);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ResourcePolicyCatalog.Validate(
                policy with { WindowsReserveBytes = 0 },
                TotalMemory));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ResourcePolicyCatalog.Validate(
                policy with
                {
                    PalworldWarningThresholdBytes = 8 * ResourcePolicyCatalog.Gibibyte,
                    PalworldCriticalThresholdBytes = 7 * ResourcePolicyCatalog.Gibibyte
                },
                TotalMemory));
    }

    [Fact]
    public void HardLimit_IsOptionalAndMustRemainInsideSharedBudget()
    {
        var policy = ResourcePolicyCatalog.Create(
            ResourceMode.Balanced,
            TotalMemory,
            hardMemoryLimitBytes: 6 * ResourcePolicyCatalog.Gibibyte);
        Assert.Equal(
            6 * ResourcePolicyCatalog.Gibibyte,
            policy.HardMemoryLimitBytes);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ResourcePolicyCatalog.Create(
                ResourceMode.Balanced,
                TotalMemory,
                hardMemoryLimitBytes: 15 * ResourcePolicyCatalog.Gibibyte));
    }
}
