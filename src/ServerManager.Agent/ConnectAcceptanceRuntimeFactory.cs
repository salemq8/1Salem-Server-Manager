#if DEBUG
using System.Text;
using System.Text.Json;
using ServerManager.Connect.Core.Enrollment;
using ServerManager.Connect.Core.Identity;
using ServerManager.Infrastructure.Connect;

namespace ServerManager.Agent;

/// <summary>Journals only cleanup metadata while delegating all real runtime operations.</summary>
internal sealed class ConnectAcceptanceRuntimeFactory(
    SystemConnectHostRuntimeFactory production,
    ConnectHostOptions options) : IConnectHostRuntimeFactory
{
    private readonly ConnectAcceptanceResourceJournal _journal = new(options.DataRoot);

    public IConnectOwnerBrokerClient CreateBroker(ConnectIdentity identity) => production.CreateBroker(identity);
    public IConnectProvisioner CreateProvisioner(TailscaleOAuthCredential credential) =>
        new ConnectAcceptanceProvisioner(production.CreateProvisioner(credential), _journal);
    public IConnectHostAuthorizationServer CreateAuthorizationServer(ConnectHostAuthorizationOptions authorizationOptions,
        ConnectServerCatalog catalog) => production.CreateAuthorizationServer(authorizationOptions, catalog);
    public IConnectHostTransportSupervisor CreateSupervisor(ConnectHostTransportOptions transportOptions) =>
        production.CreateSupervisor(transportOptions);
    public IConnectHostTransportControlClient CreateControlClient(ConnectHostTransportOptions transportOptions,
        IConnectHostTransportSupervisor supervisor) => production.CreateControlClient(transportOptions, supervisor);
}

internal sealed class ConnectAcceptanceResourceJournal(string dataRoot)
{
    public const string FileName = "connect-acceptance-resources.jsonl";
    private readonly string _path = Path.Combine(ConnectAcceptanceOptions.ValidateDataRoot(dataRoot), FileName);
    private readonly object _sync = new();

    public void Append(string kind, string id, string? role = null, string? tag = null)
    {
        // Never accept an auth key or any response body here: only the provider's resource ID.
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            kind, id, timestamp = DateTimeOffset.UtcNow, role, tag
        }) + "\n");
        lock (_sync)
        {
            ConnectAcceptanceOptions.RejectReparsePoints(_path);
            using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read,
                4096, FileOptions.WriteThrough);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
    }
}

internal sealed class ConnectAcceptanceProvisioner(
    IConnectProvisioner production,
    ConnectAcceptanceResourceJournal journal) : IConnectProvisioner
{
    public async Task<EnrollmentSecret> CreateHostAuthKeyAsync(CancellationToken cancellationToken) =>
        await RecordCreatedAsync(await production.CreateHostAuthKeyAsync(cancellationToken), "host", TailscaleApiProvisioner.HostTag);

    public async Task<EnrollmentSecret> CreateFriendAuthKeyAsync(string membershipId, CancellationToken cancellationToken) =>
        await RecordCreatedAsync(await production.CreateFriendAuthKeyAsync(membershipId, cancellationToken), "friend", TailscaleApiProvisioner.FriendTag);

    private async Task<EnrollmentSecret> RecordCreatedAsync(EnrollmentSecret secret, string role, string tag)
    {
        try
        {
            journal.Append("keyCreated", secret.KeyId, role, tag);
            return secret;
        }
        catch
        {
            // Do not hand out a key that the acceptance cleanup cannot identify.
            try { await production.DeleteAuthKeyAsync(secret.KeyId, CancellationToken.None); }
            catch { /* Preserve the original journal failure; the caller must stop acceptance. */ }
            throw;
        }
    }

    public async Task<bool> DeleteAuthKeyAsync(string keyId, CancellationToken cancellationToken)
    {
        var deleted = await production.DeleteAuthKeyAsync(keyId, cancellationToken);
        journal.Append("keyDeleted", keyId);
        return deleted;
    }

    public async Task<ConnectTailnetDevice?> GetDeviceAsync(string nodeId, CancellationToken cancellationToken)
    {
        var device = await production.GetDeviceAsync(nodeId, cancellationToken);
        if (device is not null)
        {
            var role = device.HasTag(TailscaleApiProvisioner.HostTag) ? "host" :
                device.HasTag(TailscaleApiProvisioner.FriendTag) ? "friend" : "other";
            var tag = role == "host" ? TailscaleApiProvisioner.HostTag :
                role == "friend" ? TailscaleApiProvisioner.FriendTag : null;
            journal.Append("deviceRead", device.NodeId, role, tag);
        }

        return device;
    }
    public Task<byte[]> GetPolicyAsync(CancellationToken cancellationToken) => production.GetPolicyAsync(cancellationToken);

    public async Task<bool> DeleteFriendDeviceAsync(string nodeId, string hostNodeId, CancellationToken cancellationToken)
    {
        var deleted = await production.DeleteFriendDeviceAsync(nodeId, hostNodeId, cancellationToken);
        journal.Append("deviceDeleted", nodeId, "friend", TailscaleApiProvisioner.FriendTag);
        return deleted;
    }

    public void Dispose() => production.Dispose();
}
#endif
