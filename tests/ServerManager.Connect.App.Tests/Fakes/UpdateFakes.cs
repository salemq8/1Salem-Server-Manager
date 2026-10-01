using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using ServerManager.Connect.App.Shell;
using ServerManager.Connect.App.Updates;

namespace ServerManager.Connect.App.Tests.Fakes;

/// <summary>Release metadata exactly as tools/build-release.ps1 writes it.</summary>
internal static class ReleaseFixtures
{
    public static readonly byte[] Installer = Encoding.ASCII.GetBytes("MZ fake 1SalemConnect-Setup.exe for build 12");
    public static readonly byte[] Portable = Encoding.ASCII.GetBytes("PK fake 1SalemConnect-Portable.zip for build 12");

    public static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    public static string Manifest(
        int build = 12,
        string version = "1.5",
        string? installerSha256 = null,
        long? installerSize = null,
        string repository = "salemq8/1Salem-Server-Manager",
        bool powerShellLayout = false)
    {
        var tag = $"v{version}-build-{build}";
        var assets = $"https://github.com/{repository}/releases/download/{tag}";
        var json = $$"""
            {
              "schema": 1,
              "product": "1Salem Connect",
              "channel": "Stable",
              "productVersion": "{{version}}",
              "buildRevision": {{build}},
              "releaseTag": "{{tag}}",
              "releaseUrl": "https://github.com/{{repository}}/releases/tag/{{tag}}",
              "publishedUtc": "2026-10-01T12:00:00.0000000+00:00",
              "installer": {
                "fileName": "1SalemConnect-Setup.exe",
                "url": "{{assets}}/1SalemConnect-Setup.exe",
                "size": {{(installerSize ?? Installer.Length).ToString(CultureInfo.InvariantCulture)}},
                "sha256": "{{installerSha256 ?? Sha256(Installer)}}"
              },
              "portable": {
                "fileName": "1SalemConnect-Portable.zip",
                "url": "{{assets}}/1SalemConnect-Portable.zip",
                "size": {{Portable.Length.ToString(CultureInfo.InvariantCulture)}},
                "sha256": "{{Sha256(Portable)}}"
              }
            }
            """;

        // Windows PowerShell 5.1's ConvertTo-Json: two spaces after each colon, CRLF line ends.
        return powerShellLayout ? json.Replace("\": ", "\":  ", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal) : json;
    }
}

internal sealed class FakeUpdateHttp : IUpdateHttp
{
    public byte[]? ManifestBytes { get; set; } = Encoding.UTF8.GetBytes(ReleaseFixtures.Manifest());

    public UpdateDownloadException? ManifestFailure { get; set; }

    /// <summary>What a download writes; the size and SHA-256 are checked like the real client does.</summary>
    public byte[] DownloadBytes { get; set; } = ReleaseFixtures.Installer;

    public int ManifestRequests { get; private set; }

    public List<string> Downloads { get; } = [];

    public Task<byte[]?> GetManifestAsync(CancellationToken cancellationToken)
    {
        ManifestRequests++;
        return ManifestFailure is { } failure ? Task.FromException<byte[]?>(failure) : Task.FromResult(ManifestBytes);
    }

    public Task DownloadAsync(ConnectUpdateFile file, string destination, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        Downloads.Add(file.FileName);
        if (DownloadBytes.LongLength != file.Size)
        {
            throw new UpdateDownloadException(UpdateDownloadFailure.Incomplete, "size");
        }

        if (!string.Equals(ReleaseFixtures.Sha256(DownloadBytes), file.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new UpdateDownloadException(UpdateDownloadFailure.HashMismatch, "hash");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllBytes(destination, DownloadBytes);
        progress?.Report(1);
        return Task.CompletedTask;
    }
}

internal sealed class FakeInstallerLauncher : IUpdateInstallerLauncher
{
    public InstallerLaunchOutcome Outcome { get; set; } = InstallerLaunchOutcome.Started;

    public List<(string Path, string Sha256, string Arguments)> Launches { get; } = [];

    public InstallerLaunchOutcome Launch(string installerPath, string expectedSha256, string arguments)
    {
        Launches.Add((installerPath, expectedSha256, arguments));
        return Outcome;
    }
}

internal sealed class FakeHandshake : IUpdateHandshake
{
    public Dictionary<string, InstallerResult> Results { get; } = new(StringComparer.Ordinal);

    public List<string> Signals { get; } = [];

    public bool SignalStarted(string token)
    {
        Signals.Add(token);
        return true;
    }

    public InstallerResult? ReadResult(string token) => Results.GetValueOrDefault(token);
}

internal sealed class FakeAppShell : IAppShell
{
    public int ProcessId => 4242;

    public int ShutdownCalls { get; private set; }

    public List<Uri> OpenedLinks { get; } = [];

    public List<ThemeChoice> Themes { get; } = [];

    public void Shutdown() => ShutdownCalls++;

    public bool OpenReleaseLink(Uri address)
    {
        OpenedLinks.Add(address);
        return ConnectUpdateSource.IsAllowedHost(address);
    }

    public void ApplyTheme(ThemeChoice choice) => Themes.Add(choice);
}

/// <summary>Answers by URL; records every request so redirects can be checked.</summary>
internal sealed class RoutedHandler : HttpMessageHandler
{
    private readonly Func<Uri, HttpResponseMessage> _route;

    public RoutedHandler(Func<Uri, HttpResponseMessage> route) => _route = route;

    public List<Uri> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!);
        return Task.FromResult(_route(request.RequestUri!));
    }

    public static HttpResponseMessage Bytes(byte[] body, long? contentLength = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        if (contentLength is { } length)
        {
            response.Content.Headers.ContentLength = length;
        }

        return response;
    }

    public static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(location);
        return response;
    }
}
