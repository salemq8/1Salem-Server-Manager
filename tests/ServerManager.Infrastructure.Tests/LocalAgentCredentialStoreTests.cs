using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using ServerManager.Contracts;
using ServerManager.Infrastructure.Security;

namespace ServerManager.Infrastructure.Tests;

public sealed class LocalAgentCredentialStoreTests : IDisposable
{
    private readonly string _dataRoot = Path.Combine(
        Path.GetTempPath(),
        "1salem-local-agent-credential-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void EnsureKeyFile_CreatesAHighEntropyHexKey()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        _ = new LocalAgentCredentialStore(_dataRoot);

        var key = File.ReadAllText(
            AgentTransportDefaults.ResolveLocalAgentKeyPath(_dataRoot)).Trim();
        Assert.Equal(64, key.Length);
        Assert.All(key, character => Assert.True(Uri.IsHexDigit(character)));
    }

    [Fact]
    public void Validate_AcceptsTheGeneratedKeyAndRejectsEverythingElse()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var store = new LocalAgentCredentialStore(_dataRoot);
        var key = File.ReadAllText(
            AgentTransportDefaults.ResolveLocalAgentKeyPath(_dataRoot)).Trim();

        Assert.True(store.Validate(key));
        Assert.False(store.Validate(key + "x"));
        Assert.False(store.Validate(key[..^1]));
        Assert.False(store.Validate(string.Empty));
        Assert.False(store.Validate(null));
    }

    [Fact]
    public void EnsureKeyFile_ReusesAnExistingKeyAcrossInstances()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        _ = new LocalAgentCredentialStore(_dataRoot);
        var first = File.ReadAllText(
            AgentTransportDefaults.ResolveLocalAgentKeyPath(_dataRoot)).Trim();

        // A fresh instance (as if the Agent restarted) must not silently mint a new key --
        // that would invalidate every Client's already-cached credential on every restart.
        _ = new LocalAgentCredentialStore(_dataRoot);
        var second = File.ReadAllText(
            AgentTransportDefaults.ResolveLocalAgentKeyPath(_dataRoot)).Trim();

        Assert.Equal(first, second);
    }

    [Fact]
    public void Rotate_InvalidatesThePreviousKeyImmediately_ForTheSameRunningInstance()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var store = new LocalAgentCredentialStore(_dataRoot);
        var original = File.ReadAllText(
            AgentTransportDefaults.ResolveLocalAgentKeyPath(_dataRoot)).Trim();
        Assert.True(store.Validate(original));

        var rotated = store.Rotate();

        Assert.NotEqual(original, rotated);
        Assert.False(store.Validate(original));
        Assert.True(store.Validate(rotated));

        // A separately running process (e.g., a future Client connection) also sees the
        // rotated value once it (re-)reads the file.
        var storeAfterRotation = new LocalAgentCredentialStore(_dataRoot);
        Assert.True(storeAfterRotation.Validate(rotated));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void EnsureKeyFile_RestrictsAccessToAdministratorsAndSystemOnly()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        _ = new LocalAgentCredentialStore(_dataRoot);
        var path = AgentTransportDefaults.ResolveLocalAgentKeyPath(_dataRoot);

        var security = new FileInfo(path).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected, "Inheritance must be disabled.");

        var identities = security
            .GetAccessRules(true, false, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Select(rule => (SecurityIdentifier)rule.IdentityReference)
            .ToArray();
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        var authenticatedUsers = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);

        Assert.Contains(administrators, identities);
        Assert.Contains(system, identities);
        Assert.DoesNotContain(everyone, identities);
        Assert.DoesNotContain(authenticatedUsers, identities);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataRoot))
        {
            Directory.Delete(_dataRoot, true);
        }
    }
}
