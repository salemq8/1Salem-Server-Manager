using System.Globalization;
using System.IO.Compression;
using ServerManager.Client.Shell;

namespace ServerManager.Client.Tests;

public sealed class LocalizationAndDiagnosticsTests
{
    [Fact]
    public void ArabicLocalization_ProvidesArabicNavigationLabels()
    {
        var original = CultureInfo.CurrentUICulture;
        try
        {
            LocalizationService.Apply("ar-SA");

            Assert.Equal("الرئيسية", LocalizationService.Get("Home"));
            Assert.Equal("الإعدادات", LocalizationService.Get("Settings"));
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
            CultureInfo.DefaultThreadCurrentUICulture = original;
        }
    }

    [Theory]
    [InlineData("password=super-secret", "password=[REDACTED]")]
    [InlineData("AdminPassword: hunter2", "AdminPassword=[REDACTED]")]
    public void Diagnostics_RedactsSecretLikeValues(string input, string expected)
    {
        Assert.Equal(expected, DiagnosticsService.Redact(input));
    }

    [Fact]
    public void Diagnostics_RedactsAJsonStyleQuotedValue()
    {
        // The quoted value (including its own delimiting quotes) is now matched and replaced
        // as one unit, so the exact surviving punctuation differs slightly from the flat
        // key=value cases above -- what matters is that the secret is gone and a clear
        // [REDACTED] marker replaces it.
        var redacted = DiagnosticsService.Redact("\"token\":\"abc123\"");

        Assert.DoesNotContain("abc123", redacted, StringComparison.Ordinal);
        Assert.Contains("token=[REDACTED]", redacted, StringComparison.Ordinal);
    }

    // --- P1-04: a multi-word secret value must be redacted completely, not just its first word.
    // Uses synthetic sentinel fragments (never a value that could be mistaken for a real
    // credential) and asserts none of the individual words leak into the output.

    [Theory]
    [InlineData("ServerPassword=\"sentinel alpha beta\"")]
    [InlineData("ServerPassword='sentinel alpha beta'")]
    [InlineData("AdminPassword: sentinel alpha beta")]
    public void Redact_MultiWordQuotedOrUnquotedValue_HidesEveryWord(string input)
    {
        var redacted = DiagnosticsService.Redact(input);

        Assert.DoesNotContain("sentinel", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("alpha", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("beta", redacted, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_JsonStyleMultiWordValue_HidesEveryWordAndKeepsSurroundingStructure()
    {
        var input = """{"serverPassword":"sentinel alpha beta","port":25565}""";

        var redacted = DiagnosticsService.Redact(input);

        Assert.DoesNotContain("sentinel", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("alpha", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("beta", redacted, StringComparison.Ordinal);
        // Unrelated fields around the secret must survive untouched.
        Assert.Contains("\"port\":25565", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_ConnectionStringStyleValue_StopsAtTheFieldSeparator()
    {
        var input = "Data Source=.;Password=sentinel-connection-secret;Trusted=false";

        var redacted = DiagnosticsService.Redact(input);

        Assert.DoesNotContain("sentinel-connection-secret", redacted, StringComparison.Ordinal);
        Assert.Contains("Data Source=.;", redacted, StringComparison.Ordinal);
        Assert.Contains(";Trusted=false", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_QueryStringStyleToken_IsFullyHidden()
    {
        var input = "GET /hubs/agent?access_token=sentinel.jwt.fragment HTTP/1.1";

        var redacted = DiagnosticsService.Redact(input);

        Assert.DoesNotContain("sentinel.jwt.fragment", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_ValueSpanningMultipleLines_DoesNotLeakLaterLines()
    {
        var input = "AdminPassword=\"sentinel line one\nsentinel line two\"\nNextField=visible";

        var redacted = DiagnosticsService.Redact(input);

        Assert.DoesNotContain("sentinel", redacted, StringComparison.Ordinal);
        Assert.Contains("NextField=visible", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Diagnostics_ExportsReportWithoutDatabaseOrCredentials()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"1Salem-Diagnostics-{Guid.NewGuid():N}.zip");
        try
        {
            await DiagnosticsService.ExportAsync(path);

            using var archive = ZipFile.OpenRead(path);
            Assert.Contains(archive.Entries, entry => entry.FullName == "diagnostics.json");
            Assert.Contains(archive.Entries, entry => entry.FullName == "privacy.txt");
            Assert.DoesNotContain(
                archive.Entries,
                entry => entry.FullName.EndsWith(".db", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
