namespace ServerManager.Core;

public static class MinecraftMemoryPolicy
{
    public const long Mebibyte = 1024L * 1024;
    public const int MinimumAllocationMb = 512;
    public const int MinimumWindowsReserveMb = 2048;

    public static MinecraftMemoryRecommendation Evaluate(
        long totalMemoryBytes,
        long availableMemoryBytes,
        int minimumMemoryMb,
        int maximumMemoryMb)
    {
        if (totalMemoryBytes < 2 * ResourcePolicyCatalog.Gibibyte)
        {
            throw new ArgumentOutOfRangeException(
                nameof(totalMemoryBytes),
                "At least 2 GiB of system memory must be reported.");
        }

        if (availableMemoryBytes < 0 || availableMemoryBytes > totalMemoryBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(availableMemoryBytes),
                "Available memory must be between zero and total memory.");
        }

        var totalMb = checked((int)Math.Min(int.MaxValue, totalMemoryBytes / Mebibyte));
        var availableMb = checked((int)Math.Min(
            int.MaxValue,
            availableMemoryBytes / Mebibyte));
        var reserveMb = Math.Max(
            MinimumWindowsReserveMb,
            (int)Math.Ceiling(totalMb * 0.20 / 256d) * 256);
        reserveMb = Math.Min(reserveMb, Math.Max(MinimumWindowsReserveMb, totalMb - 1024));
        var maximumSafeMb = Math.Max(
            MinimumAllocationMb,
            Math.Min(totalMb - reserveMb, Math.Max(MinimumAllocationMb, availableMb - 1024)));

        var recommendedMaximumMb = totalMb switch
        {
            <= 8 * 1024 => Math.Min(4 * 1024, maximumSafeMb),
            <= 16 * 1024 => Math.Min(8 * 1024, Math.Max(6 * 1024, maximumSafeMb * 3 / 4)),
            <= 32 * 1024 => Math.Min(12 * 1024, maximumSafeMb * 2 / 3),
            _ => Math.Min(16 * 1024, maximumSafeMb * 2 / 3)
        };
        recommendedMaximumMb = Math.Max(
            MinimumAllocationMb,
            Math.Min(recommendedMaximumMb, maximumSafeMb));
        var recommendedMinimumMb = Math.Max(
            1024,
            Math.Min(recommendedMaximumMb, recommendedMaximumMb / 2));

        var warnings = new List<string>();
        if (minimumMemoryMb <= 0)
        {
            warnings.Add("Minecraft Xms must be greater than zero.");
        }

        if (maximumMemoryMb < minimumMemoryMb)
        {
            warnings.Add("Minecraft Xmx must be greater than or equal to Xms.");
        }

        if (maximumMemoryMb > maximumSafeMb)
        {
            warnings.Add(
                $"Xmx must not exceed the current safe limit of {maximumSafeMb} MB; " +
                "Windows and the Agent need reserved memory.");
        }
        else if (maximumMemoryMb > recommendedMaximumMb)
        {
            warnings.Add(
                $"Xmx is above the current recommendation of {recommendedMaximumMb} MB.");
        }

        if (totalMb <= 8 * 1024 && maximumMemoryMb > 4 * 1024)
        {
            warnings.Add("On an 8 GB system, run one game server at a time.");
        }

        var safe = minimumMemoryMb > 0 &&
                   maximumMemoryMb >= minimumMemoryMb &&
                   maximumMemoryMb <= maximumSafeMb;
        return new MinecraftMemoryRecommendation(
            recommendedMinimumMb,
            recommendedMaximumMb,
            reserveMb,
            maximumSafeMb,
            safe,
            warnings);
    }

    public static void Validate(
        long totalMemoryBytes,
        long availableMemoryBytes,
        int minimumMemoryMb,
        int maximumMemoryMb)
    {
        var evaluation = Evaluate(
            totalMemoryBytes,
            availableMemoryBytes,
            minimumMemoryMb,
            maximumMemoryMb);
        if (!evaluation.IsSafe)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumMemoryMb),
                string.Join(" ", evaluation.Warnings));
        }
    }

    public static bool ShouldApplyRestart(
        bool applyAndRestartRequested,
        bool processIsRunning) =>
        applyAndRestartRequested && processIsRunning;
}
