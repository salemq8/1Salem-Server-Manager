using System.Globalization;
using ServerManager.Client.Controls;
using ServerManager.Contracts;

namespace ServerManager.Client.Tests;

public sealed class MinecraftInventoryPresentationTests
{
    [Fact]
    public void ArrangementIs27Main9HotbarFourArmorAndDoesNotReuseSlots()
    {
        Assert.Equal(Enumerable.Range(9, 27), MinecraftInventoryPresentation.MainSlots);
        Assert.Equal(Enumerable.Range(0, 9), MinecraftInventoryPresentation.HotbarSlots);
        Assert.Equal(new[] { 103, 102, 101, 100 }, MinecraftInventoryPresentation.ArmorSlots);
    }

    [Fact]
    public void NoMaximumDurabilityIsInvented()
    {
        var item = new MinecraftInventoryItem(0, "minecraft:diamond_sword", 1, 7, null, null, new Dictionary<string, int>(), "");
        var details = MinecraftInventoryPresentation.Details(item);
        Assert.DoesNotContain("1561", details);
        Assert.Contains("7", details);
    }

    [Fact]
    public void SourceLabelsSeparateSavedLiveAndUnavailableInBothLanguages()
    {
        var before = CultureInfo.CurrentUICulture;
        try
        {
            foreach (var culture in new[] { "en", "ar" })
            {
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
                var labels = Enum.GetValues<MinecraftInventorySource>().Select(MinecraftInventoryPresentation.SourceLabel).ToArray();
                Assert.Equal(3, labels.Distinct().Count());
                Assert.All(labels, label => Assert.False(string.IsNullOrWhiteSpace(label)));
            }
        }
        finally { CultureInfo.CurrentUICulture = before; }
    }
}
