using ServerManager.Contracts;
using ServerManager.Infrastructure.Games.Palworld;

namespace ServerManager.Infrastructure.Tests;

public sealed class PalworldSettingsSerializerTests
{
    [Fact]
    public void Serialize_WritesOfficialSectionAndManagedValues()
    {
        var content = PalworldSettingsSerializer.Serialize(
            new PalworldServerSettings(
                "1Salem",
                "Dedicated server",
                "join-secret",
                "admin-secret",
                24,
                8211,
                true));

        Assert.StartsWith("[/Script/Pal.PalGameWorldSettings]", content, StringComparison.Ordinal);
        Assert.Contains("ServerName=\"1Salem\"", content, StringComparison.Ordinal);
        Assert.Contains("ServerPlayerMaxNum=24", content, StringComparison.Ordinal);
        Assert.Contains("PublicPort=8211", content, StringComparison.Ordinal);
        Assert.Contains("AdminPassword=\"admin-secret\"", content, StringComparison.Ordinal);
        Assert.Contains("RESTAPIEnabled=false", content, StringComparison.Ordinal);
        Assert.Contains("RESTAPIPort=8212", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Merge_PreservesUnknownOfficialSettings()
    {
        const string existing =
            "[/Script/Pal.PalGameWorldSettings]\n" +
            "OptionSettings=(ServerName=\"Old\",CustomSetting=KeepMe,Difficulty=Hard)";
        var merged = PalworldSettingsSerializer.Merge(
            existing,
            new PalworldServerSettings(
                "New",
                "Description",
                "join",
                "admin",
                32,
                8211,
                false,
                RestApiEnabled: true,
                RestApiPort: 8212));

        Assert.Contains("ServerName=\"New\"", merged, StringComparison.Ordinal);
        Assert.Contains("CustomSetting=KeepMe", merged, StringComparison.Ordinal);
        Assert.Contains("Difficulty=Hard", merged, StringComparison.Ordinal);
        Assert.Contains("RESTAPIEnabled=true", merged, StringComparison.Ordinal);
        Assert.Contains("RESTAPIPort=8212", merged, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_RejectsMoreThanThirtyTwoPlayers()
    {
        var settings = new PalworldServerSettings(
            "Server",
            string.Empty,
            string.Empty,
            string.Empty,
            33,
            8211,
            false);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => PalworldSettingsSerializer.Validate(settings));
    }
}
