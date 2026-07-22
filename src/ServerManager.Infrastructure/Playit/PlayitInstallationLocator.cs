using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;

namespace ServerManager.Infrastructure.Playit;

public sealed record PlayitInstallation(
    bool IsInstalled,
    string? ExecutablePath,
    string? Version,
    bool IsSigned,
    string? Signer);

public sealed class PlayitInstallationLocator
{
    private readonly IReadOnlyList<string> _candidates;

    public PlayitInstallationLocator(IEnumerable<string>? candidates = null)
    {
        var supplied = candidates?.ToArray();
        _candidates = supplied is { Length: > 0 }
            ? supplied.Select(Path.GetFullPath).Distinct(
                StringComparer.OrdinalIgnoreCase).ToArray()
            : DefaultCandidates().ToArray();
    }

    public PlayitInstallation Detect()
    {
        var executable = _candidates.FirstOrDefault(File.Exists);
        if (executable is null)
        {
            return new PlayitInstallation(false, null, null, false, null);
        }

        var information = FileVersionInfo.GetVersionInfo(executable);
        var version = information.ProductVersion ?? information.FileVersion;
        var (signed, signer) = ReadSignature(executable);
        return new PlayitInstallation(
            true,
            executable,
            string.IsNullOrWhiteSpace(version) ? null : version,
            signed,
            signer);
    }

    public static string? FindExistingSecretPath()
    {
        var paths = new List<string>();
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(local))
        {
            paths.Add(Path.Combine(local, "playit_gg", "playit.toml"));
        }

        var systemDrive = Environment.GetEnvironmentVariable("SystemDrive")
            ?? Path.GetPathRoot(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles))
            ?? "C:\\";
        var usersRoot = Path.Combine(systemDrive, "Users");
        if (Directory.Exists(usersRoot))
        {
            try
            {
                paths.AddRange(Directory.EnumerateDirectories(usersRoot)
                    .Select(directory => Path.Combine(
                        directory,
                        "AppData",
                        "Local",
                        "playit_gg",
                        "playit.toml")));
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return paths.Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static IEnumerable<string> DefaultCandidates()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            yield return Path.Combine(programFiles, "playit_gg", "bin", "playit.exe");
        }

        if (!string.IsNullOrWhiteSpace(local))
        {
            yield return Path.Combine(local, "playit_gg", "bin", "playit.exe");
            yield return Path.Combine(local, "Programs", "playit", "playit.exe");
        }
    }

    private static (bool Signed, string? Signer) ReadSignature(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return (false, null);
        }

        try
        {
#pragma warning disable SYSLIB0026
            using var certificate = new X509Certificate2(
                X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0026
            return (true, certificate.GetNameInfo(X509NameType.SimpleName, false));
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return (false, null);
        }
    }
}
