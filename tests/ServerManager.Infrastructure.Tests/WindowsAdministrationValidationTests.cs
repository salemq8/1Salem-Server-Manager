using ServerManager.Contracts;
using ServerManager.Core;
using ServerManager.Infrastructure.Windows;

namespace ServerManager.Infrastructure.Tests;

public sealed class WindowsAdministrationValidationTests
{
    [Fact]
    public async Task FirewallRule_RejectsRelativeExecutable()
    {
        var service = new WindowsFirewallService();
        var rule = new FirewallRuleSpec(
            "Minecraft",
            GameType.Minecraft,
            25565,
            "TCP",
            "java.exe");

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => service.EnsureRuleAsync(rule));
    }

    [Fact]
    public async Task FirewallRule_RejectsUnsafeName()
    {
        var service = new WindowsFirewallService();
        var rule = new FirewallRuleSpec(
            "Bad\r\nRule",
            GameType.Palworld,
            8211,
            "UDP",
            Path.GetFullPath("missing.exe"));

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.EnsureRuleAsync(rule));
    }

    [Fact]
    public async Task ServiceInstall_RequiresExistingAbsoluteExecutable()
    {
        var manager = new WindowsServiceManager();

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => manager.InstallAsync(
                "agent.exe",
                Path.GetFullPath("data")));
    }
}
