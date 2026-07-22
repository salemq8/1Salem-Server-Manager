using ServerManager.Contracts;
using ServerManager.Infrastructure.Games.Minecraft;

namespace ServerManager.Infrastructure.Tests;

public sealed class MinecraftExpandedSettingsTests
{
    [Fact]
    public void Merge_PersistsExpandedSettingsAndPreservesUnknownKeys()
    {
        const string existing =
            "# user comment\n" +
            "custom-newer-key=preserve-me\n" +
            "spawn-protection=8\n" +
            "level-name=old-world\n";
        var settings = new MinecraftServerSettings(
            "Managed server",
            25,
            "hard",
            "survival",
            true,
            12,
            10,
            true,
            Hardcore: false,
            Pvp: true,
            SpawnProtection: 24,
            EnableCommandBlocks: true,
            AllowFlight: true,
            SpawnAnimals: true,
            SpawnMonsters: false,
            SpawnNpcs: true,
            LevelName: "new-world",
            LevelSeed: "example-seed",
            LevelType: "minecraft:large_biomes",
            GenerateStructures: false);

        var merged = MinecraftPropertiesSerializer.Merge(
            existing,
            settings,
            25570);
        var parsed = MinecraftPropertiesSerializer.Parse(merged);

        Assert.Equal("preserve-me", parsed["custom-newer-key"]);
        Assert.Equal("24", parsed["spawn-protection"]);
        Assert.Equal("true", parsed["enable-command-block"]);
        Assert.Equal("true", parsed["allow-flight"]);
        Assert.Equal("false", parsed["spawn-monsters"]);
        Assert.Equal("new-world", parsed["level-name"]);
        Assert.Equal("example-seed", parsed["level-seed"]);
        Assert.Equal("minecraft:large_biomes", parsed["level-type"]);
        Assert.Equal("false", parsed["generate-structures"]);
    }

    [Fact]
    public void Serialize_RejectsInvalidSpawnProtectionAndLevelName()
    {
        var invalidProtection = CreateSettings() with
        {
            SpawnProtection = 65
        };
        var invalidName = CreateSettings() with
        {
            LevelName = "bad/name"
        };

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MinecraftPropertiesSerializer.Serialize(
                invalidProtection,
                25565));
        Assert.Throws<ArgumentException>(() =>
            MinecraftPropertiesSerializer.Serialize(
                invalidName,
                25565));
    }

    private static MinecraftServerSettings CreateSettings() =>
        new(
            "Test",
            20,
            "normal",
            "survival",
            true,
            10,
            10,
            false);
}
