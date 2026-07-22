using System.Text.RegularExpressions;
using Microsoft.AspNetCore.SignalR;
using ServerManager.Core;
using ServerManager.Infrastructure.Processes;

namespace ServerManager.Agent;

public sealed partial class AgentHub(
    IGameServerStore serverStore,
    ProcessSupervisor processSupervisor) : Hub
{
    public async Task SubscribeServer(Guid serverId)
    {
        if (await serverStore.GetAsync(serverId, Context.ConnectionAborted) is null)
        {
            throw new HubException("The requested server is not registered.");
        }

        await Groups.AddToGroupAsync(
            Context.ConnectionId,
            GroupName(serverId),
            Context.ConnectionAborted);
    }

    public async Task<IReadOnlyList<LogEntry>> GetRecentLogs(Guid serverId)
    {
        if (await serverStore.GetAsync(serverId, Context.ConnectionAborted) is null)
        {
            throw new HubException("The requested server is not registered.");
        }

        return processSupervisor.GetRecentLogs(serverId)
            .TakeLast(200)
            .Select(Redact)
            .ToArray();
    }

    public static string GroupName(Guid serverId) => $"server:{serverId:N}";

    public static LogEntry Redact(LogEntry entry) =>
        entry with { Message = SecretPattern().Replace(entry.Message, "$1=[REDACTED]") };

    [GeneratedRegex(
        @"(?i)\b(password|token|secret|adminpassword|serverpassword)\s*[:=]\s*\S+",
        RegexOptions.CultureInvariant)]
    private static partial Regex SecretPattern();
}
