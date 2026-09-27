using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using ServerManager.Connect.Core.Identity;
using ServerManager.Connect.Core.Tickets;
using ServerManager.Core;

namespace ServerManager.Infrastructure.Connect;

public sealed class ConnectHostOptions
{
    public ConnectHostOptions(
        string dataRoot,
        string transportExecutablePath,
        Uri brokerOrigin,
        IEnumerable<int> agentPorts,
        bool brokerDevelopmentMode = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(transportExecutablePath);
        DataRoot = Path.GetFullPath(dataRoot);
        TransportExecutablePath = Path.GetFullPath(transportExecutablePath);
        BrokerOrigin = brokerOrigin ?? throw new ArgumentNullException(nameof(brokerOrigin));
        AgentPorts = (agentPorts ?? throw new ArgumentNullException(nameof(agentPorts))).Distinct().ToArray();
        if (AgentPorts.Any(port => port is < 1 or > 65535))
        {
            throw new ArgumentOutOfRangeException(nameof(agentPorts));
        }

        BrokerDevelopmentMode = brokerDevelopmentMode;
    }

    public string DataRoot { get; }
    public string TransportExecutablePath { get; }
    public Uri BrokerOrigin { get; }
    public IReadOnlyList<int> AgentPorts { get; }
    public bool BrokerDevelopmentMode { get; }
    public TimeSpan ReconcileInterval { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan ControlStartupTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

public interface IConnectHostRuntimeFactory
{
    IConnectOwnerBrokerClient CreateBroker(ConnectIdentity identity);
    IConnectProvisioner CreateProvisioner(TailscaleOAuthCredential credential);
    IConnectHostAuthorizationServer CreateAuthorizationServer(
        ConnectHostAuthorizationOptions options,
        ConnectServerCatalog catalog);
    IConnectHostTransportSupervisor CreateSupervisor(ConnectHostTransportOptions options);
    IConnectHostTransportControlClient CreateControlClient(
        ConnectHostTransportOptions options,
        IConnectHostTransportSupervisor supervisor);
}

/// <summary>The production Windows and HTTP dependencies of <see cref="ConnectHost"/>.</summary>
[SupportedOSPlatform("windows")]
public sealed class SystemConnectHostRuntimeFactory(
    ConnectHostOptions options,
    TimeProvider clock,
    ILoggerFactory loggerFactory) : IConnectHostRuntimeFactory
{
    private readonly ConnectHostOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly ILoggerFactory _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));

    public IConnectOwnerBrokerClient CreateBroker(ConnectIdentity identity) =>
        new ConnectOwnerBrokerClient(
            _options.BrokerOrigin,
            _options.BrokerDevelopmentMode,
            identity,
            _clock,
            new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            });

    public IConnectProvisioner CreateProvisioner(TailscaleOAuthCredential credential) =>
        new TailscaleApiProvisioner(
            new HttpClient(new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            }) { Timeout = TimeSpan.FromSeconds(20) },
            credential,
            _clock,
            _loggerFactory.CreateLogger<TailscaleApiProvisioner>());

    public IConnectHostAuthorizationServer CreateAuthorizationServer(
        ConnectHostAuthorizationOptions authorizationOptions,
        ConnectServerCatalog catalog) =>
        new ConnectHostAuthorizationPipeServer(
            authorizationOptions,
            catalog,
            _clock,
            _loggerFactory.CreateLogger<ConnectHostAuthorizationPipeServer>());

    public IConnectHostTransportSupervisor CreateSupervisor(ConnectHostTransportOptions transportOptions) =>
        new ConnectHostTransportSupervisor(
            transportOptions,
            new SystemConnectSidecarProcessRunner(),
            _clock,
            _loggerFactory.CreateLogger<ConnectHostTransportSupervisor>());

    public IConnectHostTransportControlClient CreateControlClient(
        ConnectHostTransportOptions transportOptions,
        IConnectHostTransportSupervisor supervisor) =>
        new ConnectHostTransportControlClient(transportOptions, supervisor);
}
