using System.Runtime.Versioning;

namespace ServerManager.Connect.Core.Identity;

/// <summary>
/// Creates and verifies a directory that only the current Windows account and LocalSystem can
/// use. The Agent runs this as LocalSystem, making owner-side Connect state SYSTEM-only.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ConnectProtectedDirectory
{
    public static void Ensure(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        CurrentUserOnlyAccess.EnsureDirectory(
            Path.GetFullPath(path),
            CurrentUserOnlyAccess.CurrentUser,
            FileSystemIdentityAccessInspector.Instance);
    }

    /// <summary>Throws when the directory is absent, has an untrusted owner, or has a wider ACL.</summary>
    public static void Verify(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        try
        {
            if (!Directory.Exists(fullPath) ||
                !CurrentUserOnlyAccess.IsRestrictedDirectory(
                    FileSystemIdentityAccessInspector.Instance.ReadDirectory(fullPath),
                    CurrentUserOnlyAccess.CurrentUser))
            {
                throw new ConnectIdentityUnavailableException();
            }
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new ConnectIdentityUnavailableException(exception);
        }
        catch (IOException exception)
        {
            throw new ConnectIdentityUnavailableException(exception);
        }
    }
}
