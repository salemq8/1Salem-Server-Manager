using System.Runtime.Versioning;
using ServerManager.Connect.Core.Identity;

namespace ServerManager.Infrastructure.Connect;

/// <summary>The complete protected on-disk layout for the owner's Connect host.</summary>
[SupportedOSPlatform("windows")]
public sealed class ConnectOwnerPaths
{
    public ConnectOwnerPaths(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        DataRoot = Path.GetFullPath(dataRoot);
        Root = Path.Combine(DataRoot, "connect");
        IdentityDirectory = Path.Combine(Root, "identity");
        HostTransportDirectory = Path.Combine(Root, "host-transport");
        StateFile = Path.Combine(Root, "state.json");
        RevocationsFile = Path.Combine(Root, "revocations.json");
        TicketKeysFile = Path.Combine(Root, "ticket-keys.json");
        OAuthCredentialFile = Path.Combine(Root, "oauth-client.dpapi");
    }

    public string DataRoot { get; }
    public string Root { get; }
    public string IdentityDirectory { get; }
    public string HostTransportDirectory { get; }
    public string StateFile { get; }
    public string RevocationsFile { get; }
    public string TicketKeysFile { get; }
    public string OAuthCredentialFile { get; }

    /// <summary>Creates or tightens every directory before any Connect state is opened.</summary>
    public void EnsureDirectories()
    {
        ConnectProtectedDirectory.Ensure(Root);
        ConnectProtectedDirectory.Ensure(IdentityDirectory);
        ConnectProtectedDirectory.Ensure(HostTransportDirectory);
    }
}
