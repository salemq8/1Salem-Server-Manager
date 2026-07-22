using ServerManager.Infrastructure.Games.Palworld;

namespace ServerManager.Infrastructure.Tests;

public sealed class SteamCmdCommandBuilderTests
{
    [Fact]
    public void BuildInstallOrUpdateArguments_PinsDestinationAppAndValidation()
    {
        var destination = Path.Combine(Path.GetTempPath(), "Palworld Server");

        var arguments = SteamCmdCommandBuilder.BuildInstallOrUpdateArguments(
            destination,
            2_394_010);

        Assert.Contains($"+force_install_dir \"{Path.GetFullPath(destination)}\"", arguments);
        Assert.Contains("+login anonymous", arguments);
        Assert.Contains("+app_update 2394010 validate", arguments);
        Assert.EndsWith("+quit", arguments, StringComparison.Ordinal);
    }
}

