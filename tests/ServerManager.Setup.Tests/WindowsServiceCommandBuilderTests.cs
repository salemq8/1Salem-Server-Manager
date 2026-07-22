using ServerManager.Contracts;

namespace ServerManager.Setup.Tests;

public sealed class WindowsServiceCommandBuilderTests
{
    [Fact]
    public void AutomaticService_UsesSeparateStartTokens()
    {
        var arguments = WindowsServiceCommandBuilder.CreateArguments(
            "1SalemServerManagerAgent",
            "1Salem Server Manager Agent",
            "\"C:\\Agent.exe\" --service");

        var index = Array.IndexOf(arguments.ToArray(), "start=");
        Assert.True(index >= 0);
        Assert.Equal("auto", arguments[index + 1]);
        Assert.DoesNotContain("start= auto", arguments);
        Assert.DoesNotContain("start=auto", arguments);
    }

    [Fact]
    public void BinaryPath_PreservesProgramFilesAndServiceModeArgument()
    {
        var binaryPath = WindowsServiceCommandBuilder.BuildBinaryPath(
            @"C:\Program Files\1Salem Server Manager\Agent\1Salem.ServerManager.Agent.exe",
            @"C:\ProgramData\1SalemServerManager");

        Assert.Contains(
            "\"C:\\Program Files\\1Salem Server Manager\\Agent\\" +
            "1Salem.ServerManager.Agent.exe\"",
            binaryPath,
            StringComparison.Ordinal);
        Assert.Contains(" --service ", binaryPath, StringComparison.Ordinal);
        Assert.EndsWith(
            "--data-root \"C:\\ProgramData\\1SalemServerManager\"",
            binaryPath,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DisplayNameWithSpaces_IsOneValueAfterDisplayNameToken()
    {
        const string displayName = "1Salem Server Manager Agent";
        var arguments = WindowsServiceCommandBuilder.CreateArguments(
            "1SalemServerManagerAgent",
            displayName,
            "\"C:\\Agent.exe\"");

        var index = Array.IndexOf(arguments.ToArray(), "DisplayName=");
        Assert.True(index >= 0);
        Assert.Equal(displayName, arguments[index + 1]);
    }

    [Fact]
    public void ExistingServiceUpdate_UsesValidConfigTokenSequence()
    {
        var arguments = WindowsServiceCommandBuilder.ConfigureArguments(
            "1SalemServerManagerAgent",
            "\"C:\\Program Files\\1Salem Server Manager\\Agent.exe\" --service");

        Assert.Equal(
            [
                "config",
                "1SalemServerManagerAgent",
                "binPath=",
                "\"C:\\Program Files\\1Salem Server Manager\\Agent.exe\" --service",
                "start=",
                "auto"
            ],
            arguments);
    }

    [Fact]
    public async Task RealScParser_AcceptsSeparateOptionAndValueArguments()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var log = new InstallerLog();
        var runner = new InstallerProcessRunner(log);
        var serviceName = $"1SalemSyntaxProbe_{Guid.NewGuid():N}";
        var result = await runner.RunAsync(
            "Validate sc.exe argument syntax",
            "sc.exe",
            WindowsServiceCommandBuilder.ConfigureArguments(
                serviceName,
                "\"C:\\Program Files\\1Salem Server Manager\\Agent\\" +
                "1Salem.ServerManager.Agent.exe\" --service"),
            CancellationToken.None);

        Assert.True(
            ScServiceControlBackend.IsServiceMissing(result),
            result.CombinedOutput);
        Assert.DoesNotContain(
            "Invalid start",
            result.CombinedOutput,
            StringComparison.OrdinalIgnoreCase);
    }
}
