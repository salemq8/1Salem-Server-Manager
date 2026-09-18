using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using ServerManager.Contracts;

namespace ServerManager.Infrastructure.Security;

/// <summary>
/// Validates the high-entropy local bearer credential used to authenticate loopback callers
/// of the Agent's HTTP API. Loopback origin is never treated as proof of authorization on its
/// own ("127.0.0.1 is not an authentication boundary"); instead, a caller must present a
/// credential read from a file that only local Administrators/SYSTEM can open.
/// </summary>
public interface ILocalAgentCredential
{
    bool Validate(string? candidate);
}

public sealed class LocalAgentCredentialStore : ILocalAgentCredential
{
    private const int KeyLengthBytes = 32;
    private readonly string _dataRoot;
    private volatile byte[] _keyBytes;

    public LocalAgentCredentialStore(string dataRoot)
    {
        _dataRoot = dataRoot;
        _keyBytes = Encoding.UTF8.GetBytes(EnsureKeyFile(dataRoot));
    }

    public bool Validate(string? candidate)
    {
        if (string.IsNullOrEmpty(candidate))
        {
            return false;
        }

        var keyBytes = _keyBytes;
        var candidateBytes = Encoding.UTF8.GetBytes(candidate);
        var equal = candidateBytes.Length == keyBytes.Length &&
            CryptographicOperations.FixedTimeEquals(candidateBytes, keyBytes);
        CryptographicOperations.ZeroMemory(candidateBytes);
        return equal;
    }

    /// <summary>
    /// Replaces this instance's own credential immediately, invalidating the previous key for
    /// every caller of this running Agent process -- not just future instances. Any Client
    /// holding the old value must re-read the key file before its next call.
    /// </summary>
    public string Rotate()
    {
        var key = WriteNewKey(_dataRoot);
        _keyBytes = Encoding.UTF8.GetBytes(key);
        return key;
    }

    /// <summary>
    /// Replaces the on-disk credential without requiring a running instance (e.g., from Setup
    /// repair tooling). A separately running Agent process will keep validating against its own
    /// already-loaded key until it calls the instance <see cref="Rotate"/> method or restarts.
    /// </summary>
    public static string Rotate(string dataRoot) => WriteNewKey(dataRoot);

    private static string EnsureKeyFile(string dataRoot)
    {
        var path = AgentTransportDefaults.ResolveLocalAgentKeyPath(dataRoot);
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (IsWellFormedKey(existing))
            {
                return existing;
            }
        }

        return WriteNewKey(dataRoot);
    }

    private static string WriteNewKey(string dataRoot)
    {
        var path = AgentTransportDefaults.ResolveLocalAgentKeyPath(dataRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(KeyLengthBytes))
            .ToLowerInvariant();
        var temporary = $"{path}.new";
        File.WriteAllText(temporary, key);
        if (OperatingSystem.IsWindows())
        {
            RestrictToAdministrators(temporary);
        }

        File.Move(temporary, path, true);
        return key;
    }

    private static bool IsWellFormedKey(string value) =>
        value.Length == KeyLengthBytes * 2 && value.All(Uri.IsHexDigit);

    [SupportedOSPlatform("windows")]
    private static void RestrictToAdministrators(string path)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl,
            AccessControlType.Allow));

        // The Agent normally runs as LocalSystem (already covered above), but grant the
        // *current* process identity explicitly too: LocalSystem is not subject to UAC token
        // filtering, but this keeps the file usable immediately by whatever account actually
        // created it (e.g., a differently-configured service account, or this method running
        // under test) without depending on the Administrators group being enabled in a
        // filtered token.
        var currentUser = WindowsIdentity.GetCurrent().User;
        if (currentUser is not null)
        {
            security.AddAccessRule(new FileSystemAccessRule(
                currentUser,
                FileSystemRights.FullControl,
                AccessControlType.Allow));
        }

        new FileInfo(path).SetAccessControl(security);
    }
}
