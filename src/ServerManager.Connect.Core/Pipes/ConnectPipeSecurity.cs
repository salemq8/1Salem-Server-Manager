using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using ServerManager.Connect.Core.Identity;

namespace ServerManager.Connect.Core.Pipes;

/// <summary>
/// Security for the Connect named pipes (contract §11).
/// <list type="bullet">
/// <item>The DACL is protected (nothing inherited) and grants the server's own account and
/// LocalSystem only what a pipe needs. NETWORK (NU) is denied everything, because
/// <see cref="NamedPipeServerStream"/> never sets <c>PIPE_REJECT_REMOTE_CLIENTS</c> and a remote
/// SMB client always carries the NETWORK SID.</item>
/// <item>The owner is set explicitly to the server's account instead of the token's default
/// owner, which is BUILTIN\Administrators in an elevated process. Clients check this owner, so it
/// has to be predictable.</item>
/// <item>The first instance is created with <see cref="PipeOptions.FirstPipeInstance"/>. If any
/// other process already holds the name, creation fails and the server does not start, instead of
/// silently joining a pipe someone else controls.</item>
/// </list>
/// First-instance creation only protects the server. A client cannot tell a squatter from the
/// real server by the name alone, so <see cref="VerifyServerOwner"/> must pass before a client
/// sends an auth key, a session key or anything else sensitive.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ConnectPipeSecurity
{
    /// <summary>
    /// What a client needs: read and write data, read attributes, read the security descriptor
    /// (for the owner check) and wait on the handle. Not <see cref="PipeAccessRights.CreateNewInstance"/>:
    /// a client that could create instances could answer other clients as if it were the server.
    /// </summary>
    internal const PipeAccessRights ClientRights = PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize;

    /// <summary>The server's account also creates the further instances that serve concurrent clients.</summary>
    internal const PipeAccessRights ServerRights = ClientRights | PipeAccessRights.CreateNewInstance;

    private const int BufferSize = 64 * 1024;

    private static readonly SecurityIdentifier Network = new(WellKnownSidType.NetworkSid, null);

    public static SecurityIdentifier LocalSystem => CurrentUserOnlyAccess.LocalSystem;

    /// <summary>The SID of the account this process runs as.</summary>
    public static SecurityIdentifier CurrentUser => CurrentUserOnlyAccess.CurrentUser;

    /// <summary>
    /// Owner = the current account. Allow: the current account (<see cref="ServerRights"/>) and
    /// LocalSystem (<see cref="ClientRights"/>). Deny: NETWORK, everything.
    /// </summary>
    public static PipeSecurity CreateSecurity()
    {
        var owner = CurrentUser;
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(owner);
        security.AddAccessRule(new PipeAccessRule(Network, PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(owner, ServerRights, AccessControlType.Allow));
        if (owner != LocalSystem)
        {
            security.AddAccessRule(new PipeAccessRule(LocalSystem, ClientRights, AccessControlType.Allow));
        }

        return security;
    }

    /// <summary>
    /// Creates the first instance of <paramref name="pipeName"/> (the bare name, without
    /// <c>\\.\pipe\</c>). Throws <see cref="ConnectPipeNameInUseException"/> when the name already
    /// exists, whoever holds it. The server should create its next instance
    /// (<see cref="CreateNextInstance"/>) as soon as this one has a client, so at least one
    /// instance always exists and the name is never released while the server runs.
    /// </summary>
    public static NamedPipeServerStream CreateFirstInstance(string pipeName, int maxInstances = NamedPipeServerStream.MaxAllowedServerInstances)
    {
        try
        {
            return Create(pipeName, maxInstances, PipeOptions.FirstPipeInstance | PipeOptions.Asynchronous);
        }
        catch (UnauthorizedAccessException exception)
        {
            // CreateNamedPipe reports ERROR_ACCESS_DENIED when FILE_FLAG_FIRST_PIPE_INSTANCE is set
            // and the pipe already exists.
            throw new ConnectPipeNameInUseException(pipeName, exception);
        }
    }

    /// <summary>
    /// Creates a further instance of a pipe this process already created with
    /// <see cref="CreateFirstInstance"/>. The access check runs against the existing pipe's
    /// DACL, so only the owning account (<see cref="ServerRights"/>) can do this.
    /// </summary>
    public static NamedPipeServerStream CreateNextInstance(string pipeName, int maxInstances = NamedPipeServerStream.MaxAllowedServerInstances) =>
        Create(pipeName, maxInstances, PipeOptions.Asynchronous);

    /// <summary>
    /// Client side: confirms that the connected pipe is owned by <paramref name="expectedOwner"/>
    /// (the same user for the friend transport pipe; the Agent's account for the host pipe).
    /// Throws <see cref="ConnectPipeUntrustedException"/> otherwise. Call it after
    /// <c>Connect</c> and before writing anything.
    /// </summary>
    public static void VerifyServerOwner(NamedPipeClientStream pipe, SecurityIdentifier expectedOwner)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        ArgumentNullException.ThrowIfNull(expectedOwner);
        if (!pipe.IsConnected)
        {
            throw new InvalidOperationException("The pipe must be connected before its owner can be checked.");
        }

        SecurityIdentifier? owner;
        try
        {
            owner = pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or InvalidOperationException)
        {
            // A pipe whose security descriptor we may not read is not one we can trust.
            throw new ConnectPipeUntrustedException(exception);
        }

        if (owner is null || owner != expectedOwner)
        {
            throw new ConnectPipeUntrustedException();
        }
    }

    private static NamedPipeServerStream Create(string pipeName, int maxInstances, PipeOptions options)
    {
        ConnectPipeNames.Validate(pipeName);
        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            maxInstances,
            PipeTransmissionMode.Byte,
            options,
            BufferSize,
            BufferSize,
            CreateSecurity());
    }
}

/// <summary>
/// Another process already holds the pipe name. The server must not start: whoever holds the
/// name would receive the clients' requests.
/// </summary>
public sealed class ConnectPipeNameInUseException : Exception
{
    public ConnectPipeNameInUseException(string pipeName, Exception innerException)
        : base($"The pipe name '{pipeName}' is already in use by another process.", innerException)
    {
    }
}

/// <summary>
/// The pipe is not owned by the expected account, or its owner could not be read. Nothing may
/// be sent to it. The message is generic and safe to log.
/// </summary>
public sealed class ConnectPipeUntrustedException : Exception
{
    private const string DefaultMessage = "The pipe is not owned by the expected account.";

    public ConnectPipeUntrustedException()
        : base(DefaultMessage)
    {
    }

    public ConnectPipeUntrustedException(Exception innerException)
        : base(DefaultMessage, innerException)
    {
    }
}
