using System.Text.Json;
using ServerManager.Core;

namespace ServerManager.Infrastructure.Games.Minecraft;

public sealed partial class MinecraftPlayerStateService
{
    private static void ReadFiles(GameServerDefinition server, Entry entry)
    {
        var root = server.RootPath;
        // Usercache expiresOn is a cache expiry, NOT a first-join or last-seen timestamp.
        var ops = ReadIdentities(root, "ops.json", entry);
        var whitelist = ReadIdentities(root, "whitelist.json", entry);
        var banned = ReadIdentities(root, "banned-players.json", entry);
        // A cache entry is more recent than names retained in ops/ban files after a rename.
        ReadIdentities(root, "usercache.json", entry);
        var world = MinecraftPlayerFiles.ResolveWorld(root);
        if (world is not null)
        {
            foreach (var directory in MinecraftPlayerFiles.PlayerDataDirectories(world).Reverse())
                foreach (var path in EnumerateSafe(root, directory, "*.dat"))
                {
                    if (!Guid.TryParse(Path.GetFileNameWithoutExtension(path), out var uuid) || uuid == Guid.Empty) continue;
                    var player = Ensure(entry, uuid);
                    try
                    {
                        var metadata = MinecraftPlayerNbt.ReadMetadata(path);
                        entry.Players[uuid] = player with { GameMode = metadata.GameMode, Dimension = metadata.Dimension };
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { }
                }
            foreach (var directory in MinecraftPlayerFiles.StatsDirectories(world).Reverse())
                foreach (var path in EnumerateSafe(root, directory, "*.json"))
                {
                    if (!Guid.TryParse(Path.GetFileNameWithoutExtension(path), out var uuid) || uuid == Guid.Empty) continue;
                    var player = Ensure(entry, uuid);
                    using var json = MinecraftPlayerFiles.ReadJson(root, path);
                    if (json?.RootElement is { ValueKind: JsonValueKind.Object } top &&
                        top.TryGetProperty("stats", out var stats) && stats.ValueKind == JsonValueKind.Object &&
                        stats.TryGetProperty("minecraft:custom", out var custom) && custom.ValueKind == JsonValueKind.Object &&
                        (custom.TryGetProperty("minecraft:play_time", out var time) || custom.TryGetProperty("minecraft:play_one_minute", out time)) &&
                        time.ValueKind == JsonValueKind.Number && time.TryGetInt64(out var ticks) && ticks >= 0 && ticks <= TimeSpan.MaxValue.Ticks / (TimeSpan.TicksPerSecond / 20))
                        entry.Players[uuid] = player with { TotalPlayTime = TimeSpan.FromTicks(ticks * (TimeSpan.TicksPerSecond / 20)) };
                }
        }
        foreach (var player in entry.Players.Values.ToArray())
            entry.Players[player.Uuid] = player with { IsOperator = ops?.Contains(player.Uuid), IsWhitelisted = whitelist?.Contains(player.Uuid), IsBanned = banned?.Contains(player.Uuid) };
    }

    private static HashSet<Guid>? ReadIdentities(string root, string name, Entry entry)
    {
        using var json = MinecraftPlayerFiles.ReadJson(root, Path.Combine(root, name));
        if (json?.RootElement is not { ValueKind: JsonValueKind.Array } array) return null;
        var ids = new HashSet<Guid>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("uuid", out var id) || id.ValueKind != JsonValueKind.String ||
                !Guid.TryParse(id.GetString(), out var uuid) || uuid == Guid.Empty) continue;
            var username = item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            Ensure(entry, uuid, username);
            ids.Add(uuid);
        }
        return ids;
    }

    private static string[] EnumerateSafe(string root, string directory, string pattern)
    {
        try
        {
            return Directory.Exists(directory) && MinecraftPlayerFiles.IsSafeChild(root, directory)
                ? Directory.GetFiles(directory, pattern, SearchOption.TopDirectoryOnly).Where(p => MinecraftPlayerFiles.IsSafeChild(root, p)).ToArray()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }
}
