using System.IO;
using System.Windows;
using ServerManager.Contracts;
using ServerManager.Core;
using ServerManager.Infrastructure.Windows;

namespace ServerManager.Client;

public partial class AdminToolsWindow : Window
{
    private readonly WindowsServiceManager _serviceManager = new();
    private readonly WindowsFirewallService _firewallService = new();
    private readonly WindowsStartupManager _startupManager = new();
    private readonly WindowsShortcutManager _shortcutManager = new();

    public AdminToolsWindow()
    {
        InitializeComponent();
        AgentPathBox.Text = ResolveAgentExecutable();
        DataRootBox.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "1SalemServerManager");
        TaskbarText.Text = WindowsShortcutManager.TaskbarPinInstructions;
    }

    private async void InstallService_Click(object sender, RoutedEventArgs e) =>
        Render(await _serviceManager.InstallAsync(AgentPathBox.Text, DataRootBox.Text));

    private async void StartService_Click(object sender, RoutedEventArgs e) =>
        Render(await _serviceManager.StartAsync());

    private async void StopService_Click(object sender, RoutedEventArgs e) =>
        Render(await _serviceManager.StopAsync());

    private async void RemoveService_Click(object sender, RoutedEventArgs e)
    {
        if (System.Windows.MessageBox.Show(
                "Remove only the Agent service registration? Server files and ProgramData will be preserved.",
                "Confirm service removal",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) == MessageBoxResult.Yes)
        {
            Render(await _serviceManager.UninstallAsync());
        }
    }

    private async void QueryService_Click(object sender, RoutedEventArgs e) =>
        Render(await _serviceManager.QueryAsync());

    private async void AddFirewall_Click(object sender, RoutedEventArgs e) =>
        Render(await _firewallService.EnsureRuleAsync(CreateAgentFirewallRule()));

    private async void RemoveFirewall_Click(object sender, RoutedEventArgs e) =>
        Render(await _firewallService.RemoveRuleAsync("Agent HTTPS"));

    private void EnableStartup_Click(object sender, RoutedEventArgs e) =>
        Render(_startupManager.SetEnabled(ResolveClientExecutable(), true));

    private void DisableStartup_Click(object sender, RoutedEventArgs e) =>
        Render(_startupManager.SetEnabled(ResolveClientExecutable(), false));

    private void CreateShortcuts_Click(object sender, RoutedEventArgs e) =>
        Render(_shortcutManager.CreateApplicationShortcuts(ResolveClientExecutable()));

    private FirewallRuleSpec CreateAgentFirewallRule() =>
        new(
            "Agent HTTPS",
            GameType.Minecraft,
            5252,
            "TCP",
            Path.GetFullPath(AgentPathBox.Text));

    private void Render(OperationResult result) =>
        StatusText.Text = result.Success
            ? string.IsNullOrWhiteSpace(result.Message) ? "Operation completed." : result.Message
            : $"Operation failed: {result.Message}";

    private static string ResolveClientExecutable() =>
        Path.Combine(AppContext.BaseDirectory, "1Salem.ServerManager.exe");

    private static string ResolveAgentExecutable()
    {
        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "Agent",
                "1Salem.ServerManager.Agent.exe")),
            Path.Combine(
                AppContext.BaseDirectory,
                "1Salem.ServerManager.Agent.exe")
        };
        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }
}
