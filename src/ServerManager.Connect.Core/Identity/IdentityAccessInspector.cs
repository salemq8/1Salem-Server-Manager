using System.Runtime.Versioning;
using System.Security.AccessControl;

namespace ServerManager.Connect.Core.Identity;

/// <summary>
/// Reads the owner and DACL that <see cref="ConnectIdentityStore"/> checks before it trusts the
/// identity folder or file. It is the store's test seam: a test cannot give a real folder another
/// account's owner, but it can hand the store the descriptor such a folder would have.
/// </summary>
internal interface IIdentityAccessInspector
{
    DirectorySecurity ReadDirectory(string path);

    /// <summary>
    /// Reads the descriptor through the open handle, so the file whose owner was checked is the
    /// file that is read, even if the path is swapped in between.
    /// </summary>
    FileSecurity ReadFile(FileStream file);
}

[SupportedOSPlatform("windows")]
internal sealed class FileSystemIdentityAccessInspector : IIdentityAccessInspector
{
    public static FileSystemIdentityAccessInspector Instance { get; } = new();

    public DirectorySecurity ReadDirectory(string path) =>
        new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);

    public FileSecurity ReadFile(FileStream file) => file.GetAccessControl();
}
