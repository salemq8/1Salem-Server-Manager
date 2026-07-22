using ServerManager.Contracts;

namespace ServerManager.Core;

public static class ResourceProfileSummary
{
    public static string Build(
        ResourcePolicy policy,
        IEnumerable<GameType> registeredGames)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(registeredGames);

        var games = registeredGames.Distinct().ToHashSet();
        var details = new List<string>();
        if (games.Contains(GameType.Palworld))
        {
            details.Add($"Palworld {policy.PalworldPriority}");
        }

        if (games.Contains(GameType.Minecraft))
        {
            details.Add($"Minecraft {policy.MinecraftPriority}");
        }

        return details.Count == 0
            ? $"Active: {policy.Mode} · No managed game servers"
            : $"Active: {policy.Mode} · {string.Join(" · ", details)}";
    }
}
