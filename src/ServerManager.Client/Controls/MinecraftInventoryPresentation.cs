using System.Globalization;
using ServerManager.Contracts;

namespace ServerManager.Client.Controls;

/// <summary>Text-only presentation: no Mojang textures and no assumed durability tables.</summary>
public static class MinecraftInventoryPresentation
{
    public static IReadOnlyList<int> MainSlots { get; } = Enumerable.Range(9, 27).ToArray();
    public static IReadOnlyList<int> HotbarSlots { get; } = Enumerable.Range(0, 9).ToArray();
    public static IReadOnlyList<int> ArmorSlots { get; } = [103, 102, 101, 100];

    public static string Text(string english, string arabic) => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar" ? arabic : english;

    public static string SlotName(int slot) => slot switch
    {
        103 => Text("Helmet", "الخوذة"), 102 => Text("Chestplate", "الدرع"),
        101 => Text("Leggings", "السروال"), 100 => Text("Boots", "الحذاء"),
        150 => Text("Offhand", "اليد الثانوية"),
        < 9 => Text("Hotbar", "الشريط السريع") + $" {slot + 1}",
        _ => Text("Slot", "خانة") + $" {slot - 8}"
    };

    public static string ItemName(MinecraftInventoryItem item) => !string.IsNullOrWhiteSpace(item.CustomName)
        ? item.CustomName : item.ItemId[(item.ItemId.IndexOf(':') + 1)..].Replace('_', ' ');

    public static string Details(MinecraftInventoryItem item)
    {
        var parts = new List<string>
        {
            ItemName(item), item.ItemId,
            Text("Count: ", "العدد: ") + item.Count.ToString(CultureInfo.CurrentUICulture)
        };
        if (item.Damage is { } damage)
            parts.Add(item.MaximumDamage is > 0
                ? Text("Durability: ", "المتانة: ") + $"{Math.Max(0, item.MaximumDamage.Value - damage)} / {item.MaximumDamage}"
                : Text("Damage used: ", "المتانة المستهلكة: ") + damage + Text(" (maximum unavailable)", " (الحد الأقصى غير متاح)"));
        if (item.Enchantments.Count > 0)
            parts.Add(Text("Enchantments: ", "التعويذات: ") + string.Join(", ", item.Enchantments.Select(entry => $"{entry.Key} {entry.Value}")));
        if (!string.IsNullOrWhiteSpace(item.Metadata)) parts.Add(Text("Saved components / metadata:\n", "المكونات / البيانات:\n") + item.Metadata);
        return string.Join(Environment.NewLine, parts);
    }

    public static string SourceLabel(MinecraftInventorySource source) => source switch
    {
        MinecraftInventorySource.Live => Text("Live inventory", "المخزون المباشر"),
        MinecraftInventorySource.LastSaved => Text("Last saved inventory", "آخر مخزون محفوظ"),
        _ => Text("Inventory unavailable", "المخزون غير متاح")
    };
}
