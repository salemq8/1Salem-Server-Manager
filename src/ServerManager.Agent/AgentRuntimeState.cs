using System.Reflection;
using ServerManager.Contracts;

namespace ServerManager.Agent;

public sealed class AgentRuntimeState
{
    private int _databaseReady;

    public AgentRuntimeState()
    {
        StartedAtUtc = DateTimeOffset.UtcNow;
        MachineName = Environment.MachineName;
        AgentId = $"local-{MachineName.ToLowerInvariant()}";
        Version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
    }

    public string AgentId { get; }

    public string MachineName { get; }

    public string Version { get; }

    public DateTimeOffset StartedAtUtc { get; }

    public bool DatabaseReady => Volatile.Read(ref _databaseReady) == 1;

    public void MarkDatabaseReady() => Interlocked.Exchange(ref _databaseReady, 1);

    public AgentStatusResponse ToStatus(AgentOptions options) =>
        new(
            AgentId,
            MachineName,
            Version,
            StartedAtUtc,
            DatabaseReady,
            options.LanEnabled ? "NamedPipe+HTTPS+SignalR" : "NamedPipe",
            options.LanEnabled
                ? $"{options.ApiUrl}; https://0.0.0.0:{options.LanPort}"
                : options.ApiUrl);
}
