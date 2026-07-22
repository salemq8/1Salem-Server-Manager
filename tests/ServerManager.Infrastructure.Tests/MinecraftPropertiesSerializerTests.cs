using ServerManager.Contracts;
using ServerManager.Infrastructure.Games.Minecraft;

namespace ServerManager.Infrastructure.Tests;

public sealed class MinecraftPropertiesSerializerTests
{
    [Fact]
    public void SerializeAndParse_PreservesManagedSettings()
    {
        var settings = new MinecraftServerSettings(
            "1Salem = Server",
            20,
            "hard",
            "survival",
            true,
            12,
            10,
            true);

        var content = MinecraftPropertiesSerializer.Serialize(settings, 25565);
        var parsed = MinecraftPropertiesSerializer.Parse(content);

        Assert.Equal("25565", parsed["server-port"]);
        Assert.Equal("20", parsed["max-players"]);
        Assert.Equal("1Salem = Server", parsed["motd"]);
        Assert.Equal("true", parsed["white-list"]);
    }

    [Fact]
    public void Serialize_RejectsInvalidPort()
    {
        var settings = new MinecraftServerSettings(
            "Server",
            20,
            "normal",
            "survival",
            true,
            10,
            10,
            false);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => MinecraftPropertiesSerializer.Serialize(settings, 0));
    }
}

