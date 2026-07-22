using System.Globalization;
using System.Text;
using ServerManager.Infrastructure.Games.Palworld;

namespace ServerManager.Infrastructure.Tests;

public sealed class PalworldV130ConfigurationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "1SalemServerManager.V130.Configuration",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task LiveConfiguration_DiscoveryBackupAndAtomicWritePreserveEncodingAndUnknownValues()
    {
        var livePath = PalworldConfigurationFile.ResolvePath(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(livePath)!);
        const string original =
            "[/Script/Pal.PalGameWorldSettings]\r\n" +
            "OptionSettings=(ServerName=\"Salem, World\",FutureSetting=\"keep,me\",CrossplayPlatforms=(Steam,Xbox),RESTAPIEnabled=false,RESTAPIPort=8212)\r\n";
        await File.WriteAllTextAsync(
            livePath,
            original,
            new UnicodeEncoding(false, true, true));

        var document = await PalworldConfigurationFile.ReadAsync(_root);
        var safetyCopy = await PalworldConfigurationFile.CreateSafetyBackupAsync(
            _root,
            "management-enable");
        var merged = PalworldSettingsSerializer.MergeValues(
            document.Content,
            new Dictionary<string, string>
            {
                ["RESTAPIEnabled"] = "true",
                ["RESTAPIPort"] = "8312"
            });
        await PalworldConfigurationFile.WriteAtomicAsync(document, merged);

        var written = await PalworldConfigurationFile.ReadAsync(_root);
        var values = PalworldSettingsSerializer.ParseValues(written.Content);
        var bytes = await File.ReadAllBytesAsync(livePath);
        Assert.Equal(livePath, document.Path);
        Assert.Equal(0xFF, bytes[0]);
        Assert.Equal(0xFE, bytes[1]);
        Assert.Equal("\"keep,me\"", values["FutureSetting"]);
        Assert.Equal("(Steam,Xbox)", values["CrossplayPlatforms"]);
        Assert.Equal("true", values["RESTAPIEnabled"]);
        Assert.Equal("8312", values["RESTAPIPort"]);
        Assert.True(File.Exists(safetyCopy));
        Assert.Contains(
            safetyCopy,
            PalworldConfigurationFile.ListHistory(_root));
        Assert.DoesNotContain(
            Directory.EnumerateFiles(Path.GetDirectoryName(livePath)!),
            path => path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Serializer_RejectsDuplicateOptionSettingsAndPreservesQuotedCommas()
    {
        const string valid =
            "[/Script/Pal.PalGameWorldSettings]\n" +
            "OptionSettings=(ServerName=\"A, B\",CrossplayPlatforms=(Steam,Xbox),bHardcore=false)";
        var parsed = PalworldSettingsSerializer.ParseValues(valid);

        Assert.Equal("\"A, B\"", parsed["ServerName"]);
        Assert.Equal("(Steam,Xbox)", parsed["CrossplayPlatforms"]);
        Assert.Throws<InvalidDataException>(() =>
            PalworldSettingsSerializer.ParseValues(
                $"{valid}\nOptionSettings=(ExpRate=1.0)"));
    }

    [Fact]
    public void WorldSettings_CoverAllCategoriesAndEveryPresetValidates()
    {
        var expectedCategories = new[]
        {
            "Progression",
            "Time",
            "Players",
            "Pals",
            "Resources and Farming",
            "Bases and Guilds",
            "Server and Multiplayer"
        };
        Assert.Equal(expectedCategories, PalworldWorldSettingsCatalog.Categories);

        foreach (var preset in PalworldWorldSettingsCatalog.Presets)
        {
            var values = PalworldWorldSettingsCatalog.GetPreset(preset);
            var rendered =
                PalworldWorldSettingsCatalog.ValidateAndRender(values);
            Assert.Equal(values.Count, rendered.Count);
        }
    }

    [Fact]
    public void WorldSettings_UseInvariantNumbersAndValidateTypesRangesAndLists()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            var rendered = PalworldWorldSettingsCatalog.ValidateAndRender(
                new Dictionary<string, string>
                {
                    ["ExpRate"] = "1.25",
                    ["bHardcore"] = "TRUE",
                    ["DeathPenalty"] = "itemandequipment",
                    ["CrossplayPlatforms"] = "(steam,PS5)",
                    ["ServerDescription"] = "Friends, \"weekend\""
                });

            Assert.Equal("1.25", rendered["ExpRate"]);
            Assert.Equal("true", rendered["bHardcore"]);
            Assert.Equal("ItemAndEquipment", rendered["DeathPenalty"]);
            Assert.Equal("(Steam,PS5)", rendered["CrossplayPlatforms"]);
            Assert.Equal(
                "\"Friends, \\\"weekend\\\"\"",
                rendered["ServerDescription"]);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PalworldWorldSettingsCatalog.ValidateAndRender(
                new Dictionary<string, string> { ["PalSpawnNumRate"] = "9" }));
        Assert.Throws<ArgumentException>(() =>
            PalworldWorldSettingsCatalog.ValidateAndRender(
                new Dictionary<string, string> { ["DeathPenalty"] = "Unknown" }));
        Assert.Throws<ArgumentException>(() =>
            PalworldWorldSettingsCatalog.ValidateAndRender(
                new Dictionary<string, string>
                {
                    ["CrossplayPlatforms"] = "(Steam,Steam)"
                }));
        Assert.Throws<ArgumentException>(() =>
            PalworldWorldSettingsCatalog.ValidateAndRender(
                new Dictionary<string, string> { ["InventedSetting"] = "1" }));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
