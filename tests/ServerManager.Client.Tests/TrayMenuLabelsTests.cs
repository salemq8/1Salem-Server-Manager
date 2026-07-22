using ServerManager.Client.Shell;

namespace ServerManager.Client.Tests;

public sealed class TrayMenuLabelsTests
{
    [Fact]
    public void RequiredItems_ContainsSafeExitChoices()
    {
        Assert.Contains("Exit Dashboard", TrayMenuLabels.RequiredItems);
        Assert.Contains("Stop servers and exit", TrayMenuLabels.RequiredItems);
        Assert.Contains("Open Dashboard", TrayMenuLabels.RequiredItems);
    }
}
