using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace Phase2Acceptance;

internal sealed record AcceptanceOptions(
    string Work, string Agent, Uri Broker, Uri ApiUrl, string HostTransport,
    string FriendTransport, string Probe, string Credential, string Precheck, bool Live)
{
    public static AcceptanceOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var live = false;
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "--work", "--agent", "--broker", "--api-url", "--host-transport",
            "--friend-transport", "--probe", "--credential", "--tailnet-lock-off-evidence"
        };
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--live" && !live) { live = true; continue; }
            if (!allowed.Contains(args[i]) || i + 1 >= args.Length || !values.TryAdd(args[i], args[++i]))
                throw new ArgumentException("Unknown, duplicate, or incomplete acceptance argument.");
        }
        string Value(string key) => values.GetValueOrDefault(key) ?? throw new ArgumentException("Missing " + key);
        string Full(string key)
        {
            var path = Value(key);
            if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
                throw new ArgumentException("Acceptance paths must be absolute local paths.");
            path = Path.GetFullPath(path);
            AssertNoReparse(path);
            return path;
        }
        Uri Loopback(string key)
        {
            if (!Uri.TryCreate(Value(key), UriKind.Absolute, out var uri) || uri.Scheme != "http" ||
                uri.Host != "127.0.0.1" || uri.AbsolutePath != "/" || uri.Query.Length != 0 ||
                uri.Fragment.Length != 0 || uri.UserInfo.Length != 0 || uri.Port < 1024 ||
                uri.Port is 5251 or 5252 or 7780 or 25565 or 8211 or 8212)
                throw new ArgumentException("Acceptance endpoints require separate explicit IPv4 loopback ports.");
            return uri;
        }
        var options = new AcceptanceOptions(Full("--work"), Full("--agent"), Loopback("--broker"),
            Loopback("--api-url"), Full("--host-transport"), Full("--friend-transport"), Full("--probe"),
            Full("--credential"), Full("--tailnet-lock-off-evidence"), live);
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "1Salem Connect Phase2 Acceptance", "oauth-client.dpapi");
        if (!string.Equals(options.Credential, expected, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only the separately approved Phase2 DPAPI staging path is accepted.");
        if (options.Broker.Port == options.ApiUrl.Port ||
            Directory.Exists(options.Work) && Directory.EnumerateFileSystemEntries(options.Work).Any())
            throw new ArgumentException("Use distinct endpoint ports and a new or empty acceptance work directory.");
        var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!options.Work.StartsWith(temp, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(options.Work), "driver", StringComparison.Ordinal))
            throw new ArgumentException("The driver work directory must be an isolated TEMP descendant named driver.");
        foreach (var path in new[] { options.Agent, options.HostTransport, options.FriendTransport, options.Probe })
            if (!File.Exists(path)) throw new ArgumentException("A required prebuilt acceptance executable is missing.");
        return options;
    }

    public void VerifyPrecheckAndCredentialBoundary()
    {
        AssertNoReparse(Precheck);
        using var evidence = JsonDocument.Parse(File.ReadAllBytes(Precheck));
        var root = evidence.RootElement;
        var checkedAt = root.GetProperty("checkedAtUtc").GetDateTimeOffset();
        if (!root.GetProperty("tailnetLockOff").GetBoolean() || !root.GetProperty("policySafe").GetBoolean() ||
            !root.GetProperty("requiredScopesPresent").GetBoolean() ||
            string.IsNullOrWhiteSpace(root.GetProperty("source").GetString()) ||
            DateTimeOffset.UtcNow - checkedAt > TimeSpan.FromMinutes(30) || checkedAt > DateTimeOffset.UtcNow.AddMinutes(2))
            throw new InvalidOperationException("A fresh independent Tailnet Lock OFF, scope and policy precheck is required.");

        AssertNoReparse(Credential);
        var file = new FileInfo(Credential);
        if (!file.Exists || file.Length is < 1 or > 65536)
            throw new InvalidOperationException("The Phase2 staging credential is missing or outside its size limit.");
        var user = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("No Windows user SID.");
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var directoryAcl = file.Directory!.GetAccessControl();
        if (!directoryAcl.AreAccessRulesProtected || !user.Equals(directoryAcl.GetOwner(typeof(SecurityIdentifier))))
            throw new InvalidOperationException("The staging directory must be protected and owned by this user.");
        CheckReaders(directoryAcl, user, system);
        var acl = file.GetAccessControl();
        var owner = acl.GetOwner(typeof(SecurityIdentifier));
        if (!user.Equals(owner) && !admins.Equals(owner))
            throw new InvalidOperationException("Unexpected staging credential owner.");
        CheckReaders(acl, user, system);
    }

    private static void CheckReaders(FileSystemSecurity acl, SecurityIdentifier user, SecurityIdentifier system)
    {
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow &&
                !user.Equals(rule.IdentityReference) && !system.Equals(rule.IdentityReference))
                throw new InvalidOperationException("The staging credential boundary permits another identity.");
    }

    public static void AssertNoReparse(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("Reparse points are not allowed in acceptance paths.");
    }
}
