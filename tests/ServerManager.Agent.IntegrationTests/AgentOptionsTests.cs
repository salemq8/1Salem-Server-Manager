using ServerManager.Agent;
using ServerManager.Contracts;

namespace ServerManager.Agent.IntegrationTests;

public sealed class AgentOptionsTests
{
    [Fact]
    public void Parse_UsesExplicitDataRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "1Salem Data Root");

        var options = AgentOptions.Parse(["--data-root", path]);

        Assert.Equal(Path.GetFullPath(path), options.DataRoot);
        Assert.Equal(AgentTransportDefaults.PipeName, options.PipeName);
    }
}

