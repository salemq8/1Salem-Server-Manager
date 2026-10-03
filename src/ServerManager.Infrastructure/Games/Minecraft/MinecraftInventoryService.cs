using System.Buffers.Binary;
using System.Text.Json;
using ServerManager.Contracts;
using ServerManager.Core;
using ServerManager.Core.Minecraft;

namespace ServerManager.Infrastructure.Games.Minecraft;

/// <summary>Only reads the registered server's player save or issues a read-only data-get command.</summary>
public sealed class MinecraftInventoryService(IGameServerStore servers, IMinecraftConsoleChannel console)
{
    private const string ReplyMarker = " has the following entity data: ";

    public async Task<MinecraftInventorySnapshot> GetAsync(Guid serverId, Guid playerUuid, CancellationToken cancellationToken = default)
    {
        if (playerUuid == Guid.Empty) throw new ArgumentException("A player UUID is required.", nameof(playerUuid));
        var server = await servers.GetAsync(serverId, cancellationToken) ?? throw new KeyNotFoundException("Server not found.");
        if (server.Game != GameType.Minecraft) throw new ArgumentException("This server is not Minecraft.", nameof(serverId));

        // Never use a name, selector or caller-provided command. UUID is formatted from a parsed Guid.
        if (console.GetState(serverId) == MinecraftConsoleState.Ready)
        {
            var exchange = await console.ExchangeAsync(serverId, $"data get entity {playerUuid:D}",
                line => TryLive(line, playerUuid, out _), TimeSpan.FromSeconds(4), cancellationToken);
            if (exchange.Result.Success && TryLive(exchange.Answer, playerUuid, out var items))
                return new(serverId, playerUuid, MinecraftInventorySource.Live, items, DateTimeOffset.UtcNow, null);
        }

        var world = MinecraftPlayerFiles.ResolveWorld(server.RootPath);
        if (world is null) return Unavailable("UnsafeWorldPath");
        foreach (var directory in MinecraftPlayerFiles.PlayerDataDirectories(world))
        {
            var path = Path.Combine(directory, $"{playerUuid:D}.dat");
            if (!MinecraftPlayerFiles.IsSafeChild(server.RootPath, path)) return Unavailable("UnsafePlayerPath");
            if (!File.Exists(path)) continue;
            try
            {
                var saved = File.GetLastWriteTimeUtc(path);
                var root = MinecraftPlayerNbt.ReadFile(path);
                // A mid-save replacement/torn read is not a verified saved snapshot.
                if (saved != File.GetLastWriteTimeUtc(path)) return Unavailable("SaveChangedDuringRead");
                if (!TryItems(root, out var items)) return Unavailable("InventoryUnavailable");
                return new(serverId, playerUuid, MinecraftInventorySource.LastSaved, items, DateTimeOffset.UtcNow, new DateTimeOffset(saved));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or OverflowException)
            {
                return Unavailable("SavedInventoryUnreadable");
            }
        }
        return Unavailable("NoSavedInventory");

        MinecraftInventorySnapshot Unavailable(string reason) => new(serverId, playerUuid, MinecraftInventorySource.Unavailable, [], DateTimeOffset.UtcNow, null, reason);
    }

    public static bool TryLive(string? line, Guid expectedUuid, out IReadOnlyList<MinecraftInventoryItem> items)
    {
        items = [];
        if (line is null || line.Length > MinecraftPlayerNbt.MaximumBytes) return false;
        line = MinecraftConsoleReplies.Message(line);
        var marker = line.IndexOf(ReplyMarker, StringComparison.Ordinal);
        if (marker < 0) return false;
        if (!MinecraftPlayerCommandPolicy.IsValidName(line[..marker])) return false;
        try
        {
            var root = MinecraftPlayerNbt.ReadSnbt(line[(marker + ReplyMarker.Length)..]);
            // Guard against unrelated/delayed command output or player chat impersonating the reply.
            if (!MatchesUuid(root, expectedUuid)) return false;
            return TryItems(root, out items);
        }
        catch (Exception exception) when (exception is InvalidDataException or FormatException or OverflowException) { return false; }
    }

    private static bool MatchesUuid(IReadOnlyDictionary<string, object?> root, Guid uuid)
    {
        if (root.GetValueOrDefault("UUID") is not List<object?> { Count: 4 } values) return false;
        Span<byte> bytes = stackalloc byte[16];
        for (var i = 0; i < 4; i++)
        {
            if (MinecraftPlayerNbt.Integer(values[i]) is not { } value) return false;
            BinaryPrimitives.WriteInt32BigEndian(bytes.Slice(i * 4, 4), value);
        }
        return new Guid(bytes, bigEndian: true) == uuid;
    }

    public static bool TryItems(IReadOnlyDictionary<string, object?> root, out IReadOnlyList<MinecraftInventoryItem> items)
    {
        items = [];
        var result = new Dictionary<int, MinecraftInventoryItem>();
        var inventory = root.GetValueOrDefault("Inventory") as List<object?>;
        var equipment = root.GetValueOrDefault("equipment") as IReadOnlyDictionary<string, object?>;
        if (inventory is null && equipment is null) return false;
        if (inventory is not null)
        {
            foreach (var entry in inventory)
            {
                if (entry is not IReadOnlyDictionary<string, object?> compound) return false;
                var slot = MinecraftPlayerNbt.Integer(compound.GetValueOrDefault("Slot"));
                if (slot == -106) slot = 150;
                if (slot is null) return false;
                if (slot is not (>= 0 and <= 35) and not (>= 100 and <= 103) and not 150) continue;
                if (Item(compound, slot.Value) is { } item && !result.TryAdd(slot.Value, item)) return false;
            }
        }
        if (equipment is not null)
        {
            foreach (var (key, slot) in new[] { ("feet", 100), ("legs", 101), ("chest", 102), ("head", 103), ("offhand", 150) })
            {
                if (!equipment.TryGetValue(key, out var value)) continue;
                result.Remove(slot);
                if (value is IReadOnlyDictionary<string, object?> compound && Item(compound, slot) is { } item) result[slot] = item;
            }
        }
        items = result.Values.OrderBy(item => item.Slot).ToArray();
        return true;
    }

    private static MinecraftInventoryItem? Item(IReadOnlyDictionary<string, object?> item, int slot)
    {
        if (item.GetValueOrDefault("id") is not string { Length: > 0 and <= 256 } id || id is "minecraft:air" or "air") return null;
        var count = MinecraftPlayerNbt.Integer(item.GetValueOrDefault("count")) ?? MinecraftPlayerNbt.Integer(item.GetValueOrDefault("Count")) ?? 1;
        if (count <= 0) return null;
        var components = item.GetValueOrDefault("components") as IReadOnlyDictionary<string, object?>;
        var legacy = item.GetValueOrDefault("tag") as IReadOnlyDictionary<string, object?>;
        object? Component(string name) => components?.GetValueOrDefault("minecraft:" + name) ?? components?.GetValueOrDefault(name);
        var damage = MinecraftPlayerNbt.Integer(Component("damage")) ?? MinecraftPlayerNbt.Integer(legacy?.GetValueOrDefault("Damage"));
        var max = MinecraftPlayerNbt.Integer(Component("max_damage"));
        var custom = Component("custom_name") ?? (legacy?.GetValueOrDefault("display") as IReadOnlyDictionary<string, object?>)?.GetValueOrDefault("Name");
        var enchants = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var field in new[] { "enchantments", "stored_enchantments" })
        {
            if (Component(field) is not IReadOnlyDictionary<string, object?> map) continue;
            if (map.GetValueOrDefault("levels") is IReadOnlyDictionary<string, object?> levels) map = levels;
            foreach (var (key, value) in map)
                if (MinecraftPlayerNbt.Integer(value) is { } level) enchants[key] = level;
        }
        foreach (var field in new[] { "Enchantments", "StoredEnchantments" })
        {
            if (legacy?.GetValueOrDefault(field) is not List<object?> list) continue;
            foreach (var value in list.OfType<IReadOnlyDictionary<string, object?>>())
                if (value.GetValueOrDefault("id") is string enchant && MinecraftPlayerNbt.Integer(value.GetValueOrDefault("lvl")) is { } level) enchants[enchant] = level;
        }
        return new(slot, id, count, damage, max, TextComponent(custom), enchants, Preview(components ?? legacy));
    }

    private static string? TextComponent(object? value)
    {
        if (value is null) return null;
        if (value is string text)
        {
            // Legacy custom names are JSON text components. Display their literal text, never execute styles/click events.
            try
            {
                using var document = JsonDocument.Parse(text);
                if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("text", out var literal) && literal.ValueKind == JsonValueKind.String) return literal.GetString();
            }
            catch (JsonException) { }
            return text.Length <= 4096 ? text : text[..4096] + "…";
        }
        if (value is IReadOnlyDictionary<string, object?> map && map.GetValueOrDefault("text") is string literalText) return literalText;
        return Preview(value);
    }

    private static string Preview(object? value)
    {
        if (value is null) return string.Empty;
        // Metadata is inert text; a bounded preview avoids very large tooltips/HTTP payloads.
        var json = JsonSerializer.Serialize(value, new JsonSerializerOptions { MaxDepth = 64, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals });
        return json.Length <= 32768 ? json : json[..32768] + "…";
    }
}
