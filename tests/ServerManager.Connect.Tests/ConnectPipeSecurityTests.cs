using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using ServerManager.Connect.Core.Pipes;

namespace ServerManager.Connect.Tests;

public sealed class ConnectPipeSecurityTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void Security_AllowsCurrentUserAndSystemAndDeniesNetwork() =>
        AssertConnectDacl(ConnectPipeSecurity.CreateSecurity());

    [Fact]
    public void CreatedPipe_CarriesTheProtectedDaclAndOwner()
    {
        using var server = ConnectPipeSecurity.CreateFirstInstance(TestPipeName());

        AssertConnectDacl(server.GetAccessControl());
    }

    [Fact]
    public void SecondFirstInstance_OnTheSameName_Fails()
    {
        var name = TestPipeName();
        using var server = ConnectPipeSecurity.CreateFirstInstance(name);

        Assert.Throws<ConnectPipeNameInUseException>(() => ConnectPipeSecurity.CreateFirstInstance(name));
    }

    [Fact]
    public void FirstInstanceCheck_AlsoCatchesAPipeCreatedWithoutConnectSecurity()
    {
        var name = TestPipeName();
        using var squatter = new NamedPipeServerStream(name, PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        Assert.Throws<ConnectPipeNameInUseException>(() => ConnectPipeSecurity.CreateFirstInstance(name));
    }

    [Fact]
    public void OwningAccount_CanCreateFurtherInstances()
    {
        var name = TestPipeName();
        using var first = ConnectPipeSecurity.CreateFirstInstance(name);

        using var next = ConnectPipeSecurity.CreateNextInstance(name);

        Assert.NotNull(next);
    }

    [Fact]
    public async Task Client_VerifiesTheOwnerAndExchangesJsonLines()
    {
        var name = TestPipeName();
        using var server = ConnectPipeSecurity.CreateFirstInstance(name);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var cancellation = new CancellationTokenSource(Timeout);

        await Task.WhenAll(server.WaitForConnectionAsync(cancellation.Token), client.ConnectAsync(cancellation.Token));
        ConnectPipeSecurity.VerifyServerOwner(client, ConnectPipeSecurity.CurrentUser);

        await JsonLines.WriteAsync(client, "{\"id\":1,\"op\":\"hello\",\"v\":1}"u8.ToArray(), cancellation.Token);
        var request = await new JsonLineReader(server).ReadAsync(cancellation.Token);
        Assert.Equal("hello", request!.Value.GetProperty("op").GetString());

        await JsonLines.WriteAsync(server, "{\"id\":1,\"ok\":true,\"v\":1}"u8.ToArray(), cancellation.Token);
        var response = await new JsonLineReader(client).ReadAsync(cancellation.Token);
        Assert.True(response!.Value.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Client_RefusesAPipeOwnedBySomeoneElse()
    {
        var name = TestPipeName();
        using var server = ConnectPipeSecurity.CreateFirstInstance(name);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var cancellation = new CancellationTokenSource(Timeout);
        await Task.WhenAll(server.WaitForConnectionAsync(cancellation.Token), client.ConnectAsync(cancellation.Token));

        // The friend app expects its own account; the host transport expects the Agent's.
        var otherAccount = ConnectPipeSecurity.CurrentUser == ConnectPipeSecurity.LocalSystem
            ? new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null)
            : ConnectPipeSecurity.LocalSystem;

        Assert.Throws<ConnectPipeUntrustedException>(() => ConnectPipeSecurity.VerifyServerOwner(client, otherAccount));
    }

    [Fact]
    public async Task ClientRights_AreEnoughForAClient()
    {
        // The SYSTEM entry carries only ClientRights. This pipe gives the current user exactly
        // that set, and a client must still be able to connect, talk and check the owner.
        var user = ConnectPipeSecurity.CurrentUser;
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(user);
        security.AddAccessRule(new PipeAccessRule(user, ConnectPipeSecurity.ClientRights, AccessControlType.Allow));
        var name = TestPipeName();
        using var server = NamedPipeServerStreamAcl.Create(
            name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.FirstPipeInstance | PipeOptions.Asynchronous, 4096, 4096, security);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var cancellation = new CancellationTokenSource(Timeout);

        await Task.WhenAll(server.WaitForConnectionAsync(cancellation.Token), client.ConnectAsync(cancellation.Token));
        ConnectPipeSecurity.VerifyServerOwner(client, user);
        await JsonLines.WriteAsync(client, "{\"id\":2,\"op\":\"status\"}"u8.ToArray(), cancellation.Token);

        Assert.NotNull(await new JsonLineReader(server).ReadAsync(cancellation.Token));
    }

    [Fact]
    public void OwnerCheck_NeedsAConnectedPipe()
    {
        using var client = new NamedPipeClientStream(".", TestPipeName(), PipeDirection.InOut);

        Assert.Throws<InvalidOperationException>(() => ConnectPipeSecurity.VerifyServerOwner(client, ConnectPipeSecurity.CurrentUser));
    }

    [Theory]
    [InlineData("")]
    [InlineData(@"\\.\pipe\1Salem.Connect.HostAuthz.v1")]
    [InlineData("a/b")]
    [InlineData("anonymous")]
    public void PipeNames_MustBeBare(string name) =>
        Assert.Throws<ArgumentException>(() => ConnectPipeSecurity.CreateFirstInstance(name));

    [Fact]
    public void PipeNames_FollowTheContract()
    {
        var user = ConnectPipeSecurity.CurrentUser;

        Assert.Equal("1Salem.Connect.HostAuthz.v1", ConnectPipeNames.HostAuthorization);
        Assert.Equal($"1Salem.Connect.Transport.{user.Value}", ConnectPipeNames.FriendTransport(user));
    }

    [Fact]
    public async Task Reader_AcceptsExactly64KiBAndRefusesMore()
    {
        var exact = LineOfSize(JsonLines.MaxLineBytes);
        var tooLong = LineOfSize(JsonLines.MaxLineBytes + 1);

        var accepted = await new JsonLineReader(new MemoryStream([.. exact, (byte)'\n'])).ReadAsync(CancellationToken.None);
        Assert.Equal(JsonLines.MaxLineBytes - 8, accepted!.Value.GetProperty("x").GetString()!.Length);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new JsonLineReader(new MemoryStream([.. tooLong, (byte)'\n'])).ReadAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new JsonLineReader(new MemoryStream(new byte[200_000])).ReadAsync(CancellationToken.None));
        Assert.Throws<ArgumentException>(() => JsonLines.WriteAsync(Stream.Null, tooLong, CancellationToken.None).GetAwaiter().GetResult());
    }

    [Fact]
    public async Task Reader_ReadsSeveralLinesAndEndsCleanly()
    {
        var reader = new JsonLineReader(new MemoryStream("{\"a\":1}\n{\"b\":2}\r\n"u8.ToArray()));

        Assert.Equal(1, (await reader.ReadAsync(CancellationToken.None))!.Value.GetProperty("a").GetInt32());
        Assert.Equal(2, (await reader.ReadAsync(CancellationToken.None))!.Value.GetProperty("b").GetInt32());
        Assert.Null(await reader.ReadAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("[1,2]\n")]
    [InlineData("\"text\"\n")]
    [InlineData("{\"op\":\"open\",\"op\":\"close\"}\n")]
    [InlineData("{not json}\n")]
    [InlineData("\n")]
    [InlineData("{\"a\":1}")]
    public async Task Reader_RefusesAnythingButOneObjectPerLine(string input) =>
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new JsonLineReader(new MemoryStream(Encoding.UTF8.GetBytes(input))).ReadAsync(CancellationToken.None));

    [Theory]
    [InlineData("{\"a\":1}\n")]
    [InlineData("{\"a\":\n1}")]
    [InlineData("[1]")]
    [InlineData("")]
    public void Writer_RefusesAnythingButOneObjectOnOneLine(string input) =>
        Assert.Throws<ArgumentException>(() =>
            JsonLines.WriteAsync(Stream.Null, Encoding.UTF8.GetBytes(input), CancellationToken.None).GetAwaiter().GetResult());

    private static void AssertConnectDacl(PipeSecurity security)
    {
        var user = ConnectPipeSecurity.CurrentUser;
        var system = ConnectPipeSecurity.LocalSystem;
        var network = new SecurityIdentifier(WellKnownSidType.NetworkSid, null);

        Assert.True(security.AreAccessRulesProtected);
        Assert.Equal(user, security.GetOwner(typeof(SecurityIdentifier)));
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>()
            .ToList();

        var deny = Assert.Single(rules, rule => rule.AccessControlType == AccessControlType.Deny);
        Assert.Equal(network, deny.IdentityReference);
        Assert.Equal(PipeAccessRights.FullControl, deny.PipeAccessRights & PipeAccessRights.FullControl);

        var allows = rules.Where(rule => rule.AccessControlType == AccessControlType.Allow).ToList();
        Assert.All(allows, rule => Assert.True(
            (SecurityIdentifier)rule.IdentityReference == user || (SecurityIdentifier)rule.IdentityReference == system,
            $"Unexpected allow entry for {rule.IdentityReference.Value}"));

        var userRule = Assert.Single(allows, rule => (SecurityIdentifier)rule.IdentityReference == user);
        Assert.Equal(ConnectPipeSecurity.ServerRights, userRule.PipeAccessRights);
        if (user != system)
        {
            var systemRule = Assert.Single(allows, rule => (SecurityIdentifier)rule.IdentityReference == system);
            Assert.Equal(ConnectPipeSecurity.ClientRights, systemRule.PipeAccessRights);
            Assert.False(systemRule.PipeAccessRights.HasFlag(PipeAccessRights.CreateNewInstance));
        }

        Assert.DoesNotContain(allows, rule =>
            (SecurityIdentifier)rule.IdentityReference == new SecurityIdentifier(WellKnownSidType.WorldSid, null) ||
            (SecurityIdentifier)rule.IdentityReference == new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null) ||
            (SecurityIdentifier)rule.IdentityReference == new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null));
    }

    private static byte[] LineOfSize(int size)
    {
        // {"x":"…"} has 8 bytes of structure around the string.
        var line = Encoding.ASCII.GetBytes("{\"x\":\"" + new string('a', size - 8) + "\"}");
        Assert.Equal(size, line.Length);
        return line;
    }

    private static string TestPipeName() => $"1Salem.Connect.Test.{Guid.NewGuid():N}";
}
