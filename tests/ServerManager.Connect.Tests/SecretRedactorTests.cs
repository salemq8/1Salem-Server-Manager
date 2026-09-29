using ServerManager.Connect.Core.Crypto;
using ServerManager.Connect.Core.Diagnostics;
using ServerManager.Connect.Tests.Support;

namespace ServerManager.Connect.Tests;

public sealed class SecretRedactorTests
{
    [Theory]
    [InlineData("enroll with tskey-auth-kABC123CNTRL-SecretPart99", "tskey-auth-[REDACTED]", "SecretPart99")]
    [InlineData("client tskey-client-kXYZ-TopSecret", "tskey-client-[REDACTED]", "TopSecret")]
    [InlineData("key=tskey-api-kQQ-Hidden", "tskey-api-[REDACTED]", "Hidden")]
    [InlineData("invite https://onesalem-connect-broker-production.onesalemconnect.workers.dev/i#Zm9vYmFyYmF6cXV4cXV1eHF1dXhxdXV4cXV1eHF1dXg", "/i#[REDACTED]", "Zm9vYmFy")]
    [InlineData("Authorization: Bearer abc.def.ghi", "Authorization: [REDACTED]", "Bearer abc")]
    [InlineData("X-1S-Sig: AbCdEfGhIjKlMnOp", "X-1S-Sig: [REDACTED]", "AbCdEfGhIjKlMnOp")]
    [InlineData("{\"authKey\":\"tsk-something\",\"keyId\":\"k1\"}", "\"authKey\":\"[REDACTED]\"", "tsk-something")]
    [InlineData("{\"sessionKey\":\"MIGHAgEAMBMG\"}", "\"sessionKey\":\"[REDACTED]\"", "MIGHAgEAMBMG")]
    [InlineData("{\"secret\":\"invite-secret-value\"}", "\"secret\":\"[REDACTED]\"", "invite-secret-value")]
    [InlineData("POST /x?clientSecret=abc123&x=1", "clientSecret=[REDACTED]", "abc123")]
    public void KnownSecretShapes_AreRedacted(string input, string expectedFragment, string secretFragment)
    {
        var redacted = SecretRedactor.Redact(input);

        Assert.Contains(expectedFragment, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(secretFragment, redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Tickets_AreRedacted()
    {
        using var issuer = new TestTicketIssuer();
        var ticket = issuer.Issue();

        var redacted = SecretRedactor.Redact($"open ticket={ticket} now");

        Assert.Equal("open ticket=[REDACTED TICKET] now", redacted);
    }

    [Fact]
    public void PrivateKeys_AreRedactedInPemAndBase64Forms()
    {
        using var key = Es256.CreateKey();
        var pkcs8 = key.ExportPkcs8PrivateKey();
        var sec1 = key.ExportECPrivateKey();

        foreach (var text in new[]
                 {
                     key.ExportPkcs8PrivateKeyPem(),
                     key.ExportECPrivateKeyPem(),
                     "TICKET_SIGNING_KEY=" + Convert.ToBase64String(pkcs8),
                     "key " + Base64Url.Encode(pkcs8),
                     "sec1 " + Convert.ToBase64String(sec1),
                     key.ExportPkcs8PrivateKeyPem()[..60]
                 })
        {
            var redacted = SecretRedactor.Redact(text);
            Assert.Contains("[REDACTED PRIVATE KEY]", redacted, StringComparison.Ordinal);
            Assert.DoesNotContain(Convert.ToBase64String(pkcs8)[20..40], redacted, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void PublicMaterialAndOrdinaryText_AreLeftAlone()
    {
        using var key = Es256.CreateKey();
        var spki = Base64Url.Encode(Es256.ExportPublicKey(key));
        var ordinary = $"session opened on 127.0.0.1:18211 for dev_ABCDEFGHIJKLMNOPQRSTUVWXYZ spki={spki}";

        Assert.Equal(ordinary, SecretRedactor.Redact(ordinary));
        Assert.Equal(string.Empty, SecretRedactor.Redact(null));
    }
}
