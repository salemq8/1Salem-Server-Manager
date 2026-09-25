using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace ServerManager.Connect.Core.Identity;

/// <summary>
/// Protected (non-inheriting) ACLs that grant full control to the current account and
/// LocalSystem, and to nobody else: no Users, Authenticated Users, Everyone or Administrators.
/// DPAPI already makes the key file useless to other accounts; the ACL also keeps them from
/// reading it, replacing it, or planting a file of their own.
/// The ACL is only half of it. A folder's owner can rewrite its ACL at any time, and DPAPI alone
/// cannot tell whose file it is reading, because a machine-scope blob opens for every account on
/// the PC. So new folders and files name the current account as owner explicitly (the token's
/// default owner is BUILTIN\Administrators in an elevated process), and nothing in the folder is
/// trusted unless the folder and the file have a trusted owner (<see cref="IsTrustedOwner"/>) and
/// the folder has this ACL (<see cref="IsRestrictedDirectory"/>). Otherwise another local account
/// that created the folder first, say under ProgramData, could plant a key it also holds.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class CurrentUserOnlyAccess
{
    public static SecurityIdentifier CurrentUser
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User ?? throw new InvalidOperationException("The current Windows account has no user SID.");
        }
    }

    public static SecurityIdentifier LocalSystem { get; } = new(WellKnownSidType.LocalSystemSid, null);

    public static SecurityIdentifier Administrators { get; } = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    public static DirectorySecurity CreateDirectorySecurity(SecurityIdentifier account)
    {
        var security = RestrictedDirectoryAcl(account);
        security.SetOwner(account);
        return security;
    }

    public static FileSecurity CreateFileSecurity(SecurityIdentifier account)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(account);
        foreach (var grantee in Grantees(account))
        {
            security.AddAccessRule(new FileSystemAccessRule(
                grantee,
                FileSystemRights.FullControl,
                AccessControlType.Allow));
        }

        return security;
    }

    /// <summary>
    /// The owner must be <paramref name="account"/> itself. Only a service running as SYSTEM also
    /// accepts BUILTIN\Administrators, the default owner of what a SYSTEM token creates without
    /// naming one. Only an administrator or SYSTEM can give an object either owner, so neither
    /// is weaker than SYSTEM.
    /// </summary>
    public static bool IsTrustedOwner(FileSystemSecurity security, SecurityIdentifier account)
    {
        ArgumentNullException.ThrowIfNull(security);
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        return owner is not null &&
               (owner == account || (account == LocalSystem && owner == Administrators));
    }

    /// <summary>
    /// A trusted owner, a protected DACL (nothing inherited), and no access granted to anyone but
    /// the accounts <see cref="CreateDirectorySecurity"/> grants. Deny entries grant nothing and
    /// are allowed.
    /// </summary>
    public static bool IsRestrictedDirectory(DirectorySecurity security, SecurityIdentifier account)
    {
        if (!IsTrustedOwner(security, account) || !security.AreAccessRulesProtected)
        {
            return false;
        }

        var grantees = Grantees(account).ToHashSet();
        return security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Where(rule => rule.AccessControlType == AccessControlType.Allow)
            .All(rule => !rule.IsInherited && grantees.Contains((SecurityIdentifier)rule.IdentityReference));
    }

    /// <summary>
    /// Creates <paramref name="path"/> with the restricted ACL and owner in one step, or re-applies
    /// the ACL if the directory already exists and this account owns it, so an older or hand-made
    /// directory is tightened too. A directory another account owns is refused, never adopted:
    /// its owner could loosen the ACL again whenever it liked. Parent directories keep their
    /// normal ACLs.
    /// </summary>
    /// <exception cref="ConnectIdentityUnavailableException">
    /// The directory is not, or cannot be shown to be, this account's.
    /// </exception>
    public static void EnsureDirectory(string path, SecurityIdentifier account, IIdentityAccessInspector inspector)
    {
        var directory = new DirectoryInfo(path);
        if (directory.Exists)
        {
            if (!IsTrustedOwner(Read(inspector, path), account))
            {
                throw new ConnectIdentityUnavailableException();
            }

            directory.SetAccessControl(RestrictedDirectoryAcl(account));
        }
        else
        {
            if (directory.Parent is not null)
            {
                Directory.CreateDirectory(directory.Parent.FullName);
            }

            directory.Create(CreateDirectorySecurity(account));
        }

        // Checked again after the change. Creating a directory that appeared in the meantime
        // succeeds without applying our descriptor, so this is also what catches a directory
        // another account made between the existence check and the create.
        if (!IsRestrictedDirectory(Read(inspector, path), account))
        {
            throw new ConnectIdentityUnavailableException();
        }
    }

    private static DirectorySecurity Read(IIdentityAccessInspector inspector, string path)
    {
        try
        {
            return inspector.ReadDirectory(path);
        }
        catch (UnauthorizedAccessException exception)
        {
            // A directory whose owner we may not even read is not one we can trust.
            throw new ConnectIdentityUnavailableException(exception);
        }
    }

    private static DirectorySecurity RestrictedDirectoryAcl(SecurityIdentifier account)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var grantee in Grantees(account))
        {
            security.AddAccessRule(new FileSystemAccessRule(
                grantee,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

        return security;
    }

    private static IEnumerable<SecurityIdentifier> Grantees(SecurityIdentifier account)
    {
        yield return account;
        if (account != LocalSystem)
        {
            yield return LocalSystem;
        }
    }
}
