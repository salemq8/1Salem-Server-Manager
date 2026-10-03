namespace ServerManager.Contracts;

public enum MinecraftInventorySource { Unavailable = 0, LastSaved = 1, Live = 2 }

public sealed record MinecraftInventoryItem(
    int Slot,
    string ItemId,
    int Count,
    int? Damage,
    int? MaximumDamage,
    string? CustomName,
    IReadOnlyDictionary<string, int> Enchantments,
    string Metadata);

/// <summary>Read-only snapshot. Slots 0–8 are hotbar, 9–35 main, 100–103 boots to helmet, 150 offhand.</summary>
public sealed record MinecraftInventorySnapshot(
    Guid ServerId,
    Guid PlayerUuid,
    MinecraftInventorySource Source,
    IReadOnlyList<MinecraftInventoryItem> Items,
    DateTimeOffset CapturedAtUtc,
    DateTimeOffset? SavedAtUtc,
    string? UnavailableReason = null);
