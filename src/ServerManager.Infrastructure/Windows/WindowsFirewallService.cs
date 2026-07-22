using ServerManager.Contracts;
using ServerManager.Core;

namespace ServerManager.Infrastructure.Windows;

public sealed class WindowsFirewallService : IFirewallService
{
    private const string RulePrefix = "1Salem Server Manager - ";

    public Task<OperationResult> EnsureRuleAsync(
        FirewallRuleSpec rule,
        CancellationToken cancellationToken = default)
    {
        Validate(rule);
        return WindowsCommandRunner.RunAsync(
            "netsh.exe",
            [
                "advfirewall",
                "firewall",
                "add",
                "rule",
                $"name={RulePrefix}{rule.Name}",
                "dir=in",
                "action=allow",
                $"program={Path.GetFullPath(rule.ExecutablePath)}",
                $"protocol={rule.Protocol.ToUpperInvariant()}",
                $"localport={rule.Port}",
                "profile=private",
                "enable=yes"
            ],
            cancellationToken);
    }

    public Task<OperationResult> RemoveRuleAsync(
        string ruleName,
        CancellationToken cancellationToken = default)
    {
        ValidateRuleName(ruleName);
        return WindowsCommandRunner.RunAsync(
            "netsh.exe",
            [
                "advfirewall",
                "firewall",
                "delete",
                "rule",
                $"name={RulePrefix}{ruleName}"
            ],
            cancellationToken);
    }

    private static void Validate(FirewallRuleSpec rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ValidateRuleName(rule.Name);
        if (rule.Port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(rule), "Port must be 1 to 65535.");
        }

        if (rule.Protocol is not ("TCP" or "UDP") &&
            !rule.Protocol.Equals("TCP", StringComparison.OrdinalIgnoreCase) &&
            !rule.Protocol.Equals("UDP", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Firewall protocol must be TCP or UDP.", nameof(rule));
        }

        if (!Path.IsPathFullyQualified(rule.ExecutablePath) ||
            !File.Exists(rule.ExecutablePath))
        {
            throw new FileNotFoundException(
                "Firewall rules require an existing absolute executable path.",
                rule.ExecutablePath);
        }
    }

    private static void ValidateRuleName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            name.Length > 80 ||
            name.IndexOfAny(['\r', '\n', '"']) >= 0)
        {
            throw new ArgumentException(
                "Firewall rule names must contain 1 to 80 safe characters.",
                nameof(name));
        }
    }
}
