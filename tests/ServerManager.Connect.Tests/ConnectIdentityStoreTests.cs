using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using ServerManager.Connect.Core.Crypto;
using ServerManager.Connect.Core.Identity;

namespace ServerManager.Connect.Tests;

public sealed class ConnectIdentityStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "1salem-connect-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Identity_SurvivesAReload()
    {
        var directory = Path.Combine(_root, "identity");
        using var created = new ConnectIdentityStore(directory, ConnectIdentityKind.Device).Create();

        using var loaded = new ConnectIdentityStore(directory, ConnectIdentityKind.Device).TryLoad();

        Assert.NotNull(loaded);
        Assert.Equal(created.KeyId, loaded.KeyId);
        Assert.True(ConnectKeyIds.IsDeviceId(loaded.KeyId));
        Assert.Equal(created.PublicKeySpki.ToArray(), loaded.PublicKeySpki.ToArray());
        var data = "1SALEM-REQ-V1\nGET\n/v1/keys"u8.ToArray();
        Assert.True(Es256.Verify(created.PublicKeySpki, data, loaded.Sign(data)));
    }

    [Fact]
    public void LoadOrCreate_KeepsTheFirstIdentity()
    {
        var store = new ConnectIdentityStore(Path.Combine(_root, "identity"), ConnectIdentityKind.Owner);

        Assert.Null(store.TryLoad());
        using var first = store.LoadOrCreate();
        using var second = store.LoadOrCreate();

        Assert.Equal(first.KeyId, second.KeyId);
        Assert.True(ConnectKeyIds.IsOwnerId(first.KeyId));
        Assert.ThrowsAny<IOException>(() => store.Create());
    }

    [Fact]
    public void StoredFile_DoesNotContainThePrivateKeyInAnyPlainForm()
    {
        var store = new ConnectIdentityStore(Path.Combine(_root, "identity"), ConnectIdentityKind.Device);
        using var identity = store.Create();
        var pkcs8 = identity.UsePrivateKey(key => key.ExportPkcs8PrivateKey());
        var scalar = identity.UsePrivateKey(key => key.ExportParameters(includePrivateParameters: true).D!);

        var bytes = File.ReadAllBytes(store.IdentityPath);
        var text = Encoding.UTF8.GetString(bytes);

        Assert.Equal(-1, bytes.AsSpan().IndexOf(pkcs8));
        Assert.Equal(-1, bytes.AsSpan().IndexOf(scalar));
        Assert.DoesNotContain(Convert.ToBase64String(pkcs8), text, StringComparison.Ordinal);
        Assert.DoesNotContain(Base64Url.Encode(pkcs8), text, StringComparison.Ordinal);
        Assert.DoesNotContain(Base64Url.Encode(scalar), text, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToHexString(scalar), text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PRIVATE KEY", text, StringComparison.Ordinal);
        Assert.Contains("\"protectedKey\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void DirectoryAndFile_AdmitOnlyTheCurrentUserAndSystem_AndAreOwnedByTheCurrentUser()
    {
        var store = new ConnectIdentityStore(Path.Combine(_root, "identity"), ConnectIdentityKind.Device);
        using var identity = store.Create();

        var directory = new DirectoryInfo(store.DirectoryPath).GetAccessControl();
        var file = new FileInfo(store.IdentityPath).GetAccessControl();
        AssertOnlyUserAndSystem(directory);
        AssertOnlyUserAndSystem(file);
        Assert.Equal(CurrentUser, directory.GetOwner(typeof(SecurityIdentifier)));
        Assert.Equal(CurrentUser, file.GetOwner(typeof(SecurityIdentifier)));
    }

    [Fact]
    public void ExistingLooseDirectory_IsTightened()
    {
        var directory = Directory.CreateDirectory(Path.Combine(_root, "identity"));
        // Loose means an inherited DACL, not a foreign owner. In an elevated test run a new folder is
        // owned by Administrators, which the store rightly refuses, so name the owner explicitly.
        var loose = directory.GetAccessControl();
        loose.SetOwner(CurrentUser);
        directory.SetAccessControl(loose);

        using var identity = new ConnectIdentityStore(directory.FullName, ConnectIdentityKind.Device).Create();

        AssertOnlyUserAndSystem(directory.GetAccessControl());
    }

    [Fact]
    public void AFileOwnedByAnotherAccount_IsRefused_AndKept()
    {
        var directory = Path.Combine(_root, "identity");
        using (new ConnectIdentityStore(directory, ConnectIdentityKind.Owner).Create())
        {
        }

        var planted = new ConnectIdentityStore(directory, ConnectIdentityKind.Owner, new ForeignOwner(directory: false, file: true));
        var content = File.ReadAllBytes(planted.IdentityPath);

        Assert.Throws<ConnectIdentityUnavailableException>(() => planted.TryLoad());
        Assert.Throws<ConnectIdentityUnavailableException>(() => planted.LoadOrCreate());
        Assert.Equal(content, File.ReadAllBytes(planted.IdentityPath));
    }

    [Fact]
    public void ADirectoryOwnedByAnotherAccount_IsRefused_WhenLoading()
    {
        var directory = Path.Combine(_root, "identity");
        using (new ConnectIdentityStore(directory, ConnectIdentityKind.Owner).Create())
        {
        }

        var squatted = new ConnectIdentityStore(directory, ConnectIdentityKind.Owner, new ForeignOwner(directory: true, file: false));

        Assert.Throws<ConnectIdentityUnavailableException>(() => squatted.TryLoad());
        Assert.True(File.Exists(squatted.IdentityPath));
    }

    [Fact]
    public void ADirectoryOwnedByAnotherAccount_IsNeitherAdoptedNorTightened_WhenCreating()
    {
        // Someone else created the folder first, before this account ever needed it.
        var directory = Directory.CreateDirectory(Path.Combine(_root, "identity"));
        var squatted = new ConnectIdentityStore(directory.FullName, ConnectIdentityKind.Owner, new ForeignOwner(directory: true, file: false));

        Assert.Throws<ConnectIdentityUnavailableException>(() => squatted.Create());
        Assert.Throws<ConnectIdentityUnavailableException>(() => squatted.LoadOrCreate());
        Assert.False(File.Exists(squatted.IdentityPath));
        Assert.False(directory.GetAccessControl().AreAccessRulesProtected);
    }

    [Fact]
    public void ADirectoryThatAlsoGrantsSomeoneElse_IsRefused_AndTheFileKept()
    {
        var store = new ConnectIdentityStore(Path.Combine(_root, "identity"), ConnectIdentityKind.Device);
        using (store.Create())
        {
        }

        var directory = new DirectoryInfo(store.DirectoryPath);
        var security = directory.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.ReadAndExecute,
            AccessControlType.Allow));
        directory.SetAccessControl(security);

        Assert.Throws<ConnectIdentityUnavailableException>(() => store.TryLoad());
        Assert.True(File.Exists(store.IdentityPath));
    }

    [Fact]
    public void ADirectoryThatInheritsItsAcl_IsRefused()
    {
        var store = new ConnectIdentityStore(Path.Combine(_root, "identity"), ConnectIdentityKind.Device);
        using (store.Create())
        {
        }

        var directory = new DirectoryInfo(store.DirectoryPath);
        var security = directory.GetAccessControl();
        security.SetAccessRuleProtection(isProtected: false, preserveInheritance: true);
        directory.SetAccessControl(security);

        Assert.Throws<ConnectIdentityUnavailableException>(() => store.TryLoad());
    }

    [Fact]
    public void OnlySystem_AlsoTrustsAdministratorsAsTheOwner()
    {
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

        Assert.True(CurrentUserOnlyAccess.IsTrustedOwner(OwnedBy(CurrentUser), CurrentUser));
        Assert.True(CurrentUserOnlyAccess.IsTrustedOwner(OwnedBy(system), system));
        Assert.True(CurrentUserOnlyAccess.IsTrustedOwner(OwnedBy(administrators), system));
        Assert.False(CurrentUserOnlyAccess.IsTrustedOwner(OwnedBy(administrators), CurrentUser));
        Assert.False(CurrentUserOnlyAccess.IsTrustedOwner(OwnedBy(system), CurrentUser));
        Assert.False(CurrentUserOnlyAccess.IsTrustedOwner(OwnedBy(ForeignOwner.Account), CurrentUser));
        Assert.False(CurrentUserOnlyAccess.IsTrustedOwner(OwnedBy(ForeignOwner.Account), system));
        Assert.False(CurrentUserOnlyAccess.IsTrustedOwner(new DirectorySecurity(), CurrentUser));
    }

    [Fact]
    public void FileAssembledFromTwoIdentities_IsRefused()
    {
        var firstStore = new ConnectIdentityStore(Path.Combine(_root, "a"), ConnectIdentityKind.Device);
        var secondStore = new ConnectIdentityStore(Path.Combine(_root, "b"), ConnectIdentityKind.Device);
        using var first = firstStore.Create();
        using var second = secondStore.Create();

        var spliced = File.ReadAllText(firstStore.IdentityPath).Replace(first.KeyId, second.KeyId, StringComparison.Ordinal);
        File.WriteAllText(firstStore.IdentityPath, spliced);

        Assert.Throws<ConnectIdentityUnavailableException>(() => firstStore.TryLoad());
    }

    [Fact]
    public void DeviceFile_IsNotAcceptedAsAnOwner()
    {
        var directory = Path.Combine(_root, "identity");
        using var device = new ConnectIdentityStore(directory, ConnectIdentityKind.Device).Create();

        Assert.Throws<ConnectIdentityUnavailableException>(() =>
            new ConnectIdentityStore(directory, ConnectIdentityKind.Owner).TryLoad());
    }

    [Fact]
    public void DamagedProtectedKey_IsUnavailableNotReplaced()
    {
        var store = new ConnectIdentityStore(Path.Combine(_root, "identity"), ConnectIdentityKind.Device);
        using (store.Create())
        {
        }

        var text = File.ReadAllText(store.IdentityPath);
        var marker = "\"protectedKey\": \"";
        var start = text.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var damaged = text[..start] + (text[start] == 'A' ? 'B' : 'A') + text[(start + 1)..];
        File.WriteAllText(store.IdentityPath, damaged);

        Assert.Throws<ConnectIdentityUnavailableException>(() => store.TryLoad());
        Assert.Throws<ConnectIdentityUnavailableException>(() => store.LoadOrCreate());
        Assert.Equal(damaged, File.ReadAllText(store.IdentityPath));
    }

    private static SecurityIdentifier CurrentUser => WindowsIdentity.GetCurrent().User!;

    private static DirectorySecurity OwnedBy(SecurityIdentifier owner)
    {
        var security = new DirectorySecurity();
        security.SetOwner(owner);
        return security;
    }

    private static void AssertOnlyUserAndSystem(FileSystemSecurity security)
    {
        var user = WindowsIdentity.GetCurrent().User!;
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var forbidden = new[]
        {
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)
        };

        Assert.True(security.AreAccessRulesProtected);
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToList();
        Assert.NotEmpty(rules);
        Assert.All(rules, rule =>
        {
            var sid = (SecurityIdentifier)rule.IdentityReference;
            Assert.False(rule.IsInherited);
            Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
            Assert.True(sid == user || sid == system, $"Unexpected ACE for {sid.Value}");
            Assert.DoesNotContain(sid, forbidden);
        });
        Assert.Contains(rules, rule => (SecurityIdentifier)rule.IdentityReference == user);
    }

    /// <summary>
    /// Reports the real descriptors, except that the directory or the file seems to belong to
    /// another local account: what a folder or file planted by that account would look like.
    /// </summary>
    private sealed class ForeignOwner(bool directory, bool file) : IIdentityAccessInspector
    {
        public static readonly SecurityIdentifier Account = new("S-1-5-21-1111111111-2222222222-3333333333-1001");

        public DirectorySecurity ReadDirectory(string path)
        {
            var security = FileSystemIdentityAccessInspector.Instance.ReadDirectory(path);
            if (directory)
            {
                security.SetOwner(Account);
            }

            return security;
        }

        public FileSecurity ReadFile(FileStream stream)
        {
            var security = FileSystemIdentityAccessInspector.Instance.ReadFile(stream);
            if (file)
            {
                security.SetOwner(Account);
            }

            return security;
        }
    }
}
