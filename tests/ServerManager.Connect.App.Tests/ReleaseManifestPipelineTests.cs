using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using ServerManager.Connect.App.Updates;

namespace ServerManager.Connect.App.Tests;

/// <summary>
/// The release pipeline and the app agree: what tools/New-ConnectUpdateManifest.ps1 writes for a
/// release (as tools/build-release.ps1 runs it) is exactly what the app's strict reader accepts,
/// with the real sizes and SHA-256 values of the files beside it.
/// </summary>
public sealed class ReleaseManifestPipelineTests : IDisposable
{
    private readonly string _release = Path.Combine(Path.GetTempPath(), "1salem-connect-app-tests", Guid.NewGuid().ToString("N"));

    public ReleaseManifestPipelineTests() => Directory.CreateDirectory(_release);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_release, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void The_generated_release_metadata_is_what_the_app_accepts()
    {
        var setup = Encoding.ASCII.GetBytes("MZ setup bytes " + Guid.NewGuid());
        var portable = Encoding.ASCII.GetBytes("PK portable bytes " + Guid.NewGuid());
        File.WriteAllBytes(Path.Combine(_release, "1SalemConnect-Setup.exe"), setup);
        File.WriteAllBytes(Path.Combine(_release, "1SalemConnect-Portable.zip"), portable);

        RunGenerator("1.5", 12);

        var bytes = File.ReadAllBytes(Path.Combine(_release, ConnectUpdateSource.ManifestFileName));
        Assert.False(bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]), "written without a byte-order mark");
        var manifest = ConnectUpdateManifestReader.Read(bytes);
        Assert.Equal(ConnectBuild.Create("1.5", 12), manifest.Build);
        Assert.Equal("v1.5-build-12", manifest.ReleaseTag);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(setup)), manifest.Installer.Sha256);
        Assert.Equal(setup.LongLength, manifest.Installer.Size);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(portable)), manifest.Portable.Sha256);
        Assert.Equal(
            new Uri("https://github.com/salemq8/1Salem-Server-Manager/releases/download/v1.5-build-12/1SalemConnect-Setup.exe"),
            manifest.Installer.Url);
        Assert.Equal(UpdateDecision.UpdateAvailable, ConnectUpdatePolicy.Decide(ConnectBuild.Create("1.5", 11), manifest.Build));
    }

    private void RunGenerator(string version, int build)
    {
        var script = Path.Combine(RepositoryRoot(), "tools", "New-ConnectUpdateManifest.ps1");
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
                 {
                     "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script,
                     "-ReleaseRoot", _release, "-Version", version, "-BuildRevision", build.ToString(System.Globalization.CultureInfo.InvariantCulture),
                     "-PublishedUtc", "2026-10-01T12:00:00.0000000+00:00"
                 })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var errors = process.StandardError.ReadToEndAsync();
        process.StandardOutput.ReadToEnd();
        Assert.True(process.WaitForExit(60_000), "the generator did not finish");
        Assert.True(process.ExitCode == 0, errors.Result);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "BUILD_REVISION")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }
}
