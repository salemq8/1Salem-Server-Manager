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
    [InlineData("\"token\":\"abc123\"", "\"token=[REDACTED]\"")]
    [InlineData("AdminPassword: hunter2", "AdminPassword=[REDACTED]")]
    public void Diagnostics_RedactsSecretLikeValues(string input, string expected)
    {
        Assert.Equal(expected, DiagnosticsService.Redact(input));
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
