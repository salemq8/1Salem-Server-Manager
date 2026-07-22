using ServerManager.Client.Shell;

namespace ServerManager.Client.Tests;

public sealed class ClientLaunchModeParserTests
{
    [Fact]
    public void Parse_DefaultsToNormalMode()
    {
        Assert.Equal(ClientLaunchMode.Normal, ClientLaunchModeParser.Parse([]));
    }

    [Fact]
    public void Parse_DetectsAdminFlagCaseInsensitively()
    {
        Assert.Equal(
            ClientLaunchMode.Administrator,
            ClientLaunchModeParser.Parse(["--ADMIN"]));
    }
}

