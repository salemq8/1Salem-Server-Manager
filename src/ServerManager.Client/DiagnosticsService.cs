using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using ServerManager.Contracts;
using ServerManager.Client.Shell;

namespace ServerManager.Client;

public static partial class DiagnosticsService
{
    public static async Task<string> ExportAsync(
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(destinationPath) ||
            !Path.IsPathFullyQualified(destinationPath) ||
            !Path.GetExtension(destinationPath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Choose an absolute .zip destination.",
                nameof(destinationPath));
        }

        var staging = Path.Combine(
            Path.GetTempPath(),
            "1SalemServerManager-Diagnostics",
            Guid.NewGuid().ToString("N"));
        var temporaryArchive = destinationPath + ".new";
        Directory.CreateDirectory(staging);
        try
        {
            var resourceProfileSummary =
                await TryGetResourceProfileSummaryAsync(cancellationToken);
            var report = new
            {
                Product = "1Salem Server Manager",
                Version = ProductInfo.Version,
                BuildRevision = ProductInfo.BuildRevision,
                CapturedAtUtc = DateTimeOffset.UtcNow,
                Os = RuntimeInformation.OSDescription,
                OsArchitecture = RuntimeInformation.OSArchitecture.ToString(),
                ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                Runtime = RuntimeInformation.FrameworkDescription,
                Machine = Environment.MachineName,
                Culture = System.Globalization.CultureInfo.CurrentUICulture.Name,
                FlowDirection = LayoutDirectionService.ForCulture(
                    System.Globalization.CultureInfo.CurrentUICulture).ToString(),
                HighContrast = SystemParameters.HighContrast,
                Elevated = ElevationService.IsAdministrator(),
                ProcessWorkingSetBytes = Process.GetCurrentProcess().WorkingSet64,
                ProcessorCount = Environment.ProcessorCount,
                ResourceProfileSummary = resourceProfileSummary,
                DataRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "1SalemServerManager")
            };
            await File.WriteAllTextAsync(
                Path.Combine(staging, "diagnostics.json"),
                JsonSerializer.Serialize(
                    report,
                    new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken);

            var logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "1SalemServerManager",
                "logs",
                "agent.jsonl");
            if (File.Exists(logPath))
            {
                var lines = new Queue<string>();
                foreach (var line in File.ReadLines(logPath))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    lines.Enqueue(Redact(line));
                    if (lines.Count > 2000)
                    {
                        lines.Dequeue();
                    }
                }

                await File.WriteAllLinesAsync(
                    Path.Combine(staging, "agent-log-tail.jsonl"),
                    lines,
                    cancellationToken);
            }

            await File.WriteAllTextAsync(
                Path.Combine(staging, "privacy.txt"),
                "Credentials, passwords, tokens, and secret-like values are redacted. " +
                "The SQLite database and game files are not included.",
                cancellationToken);
            ZipFile.CreateFromDirectory(
                staging,
                temporaryArchive,
                CompressionLevel.Optimal,
                false);
            File.Move(temporaryArchive, destinationPath, true);
            return destinationPath;
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, true);
            }

            if (File.Exists(temporaryArchive))
            {
                File.Delete(temporaryArchive);
            }
        }
    }

    public static string Redact(string value) =>
        SecretPattern().Replace(value, "$1=[REDACTED]");

    private static async Task<string> TryGetResourceProfileSummaryAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = new HttpClient
            {
                BaseAddress =
                    new Uri(AgentTransportDefaults.ResolveLoopbackApiUrl()),
                Timeout = TimeSpan.FromSeconds(5)
            };
            var dashboard =
                await client.GetFromJsonAsync<DashboardSnapshot>(
                    "/api/v1/dashboard",
                    cancellationToken);
            return dashboard?.ResourceProfileSummary ??
                   "Resource profile unavailable.";
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException)
        {
            return "Resource profile unavailable.";
        }
    }

    [GeneratedRegex(
        @"(?i)\b(password|token|secret|credential|adminpassword|serverpassword)\b\s*[""':=]+\s*[""']?[^,""'\s}]+",
        RegexOptions.CultureInvariant)]
    private static partial Regex SecretPattern();
}
