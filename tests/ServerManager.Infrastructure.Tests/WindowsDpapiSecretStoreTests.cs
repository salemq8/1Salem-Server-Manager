using ServerManager.Infrastructure.Security;

namespace ServerManager.Infrastructure.Tests;

public sealed class WindowsDpapiSecretStoreTests
{
    [Fact]
    public void ProtectAndUnprotect_RoundTripsWithoutPlaintextStorage()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var store = new WindowsDpapiSecretStore();
        const string plaintext = "palworld-admin-secret";

        var protectedValue = store.Protect(plaintext);
        var unprotected = store.Unprotect(protectedValue);

        Assert.NotEqual(plaintext, protectedValue);
        Assert.Equal(plaintext, unprotected);
    }
}

