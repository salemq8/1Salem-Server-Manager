using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace ServerManager.Connect.Setup;

/// <summary>
/// The file side of installing and updating one install folder, testable with any folder. New
/// files are unpacked next to the install folder and checked first; the install folder is then
/// swapped by renaming, so at every moment either the old or the new app is complete. In update
/// mode the previous folder is kept until the new build is known to start, and put back
/// otherwise. Nothing outside the install folder and its siblings is touched; per-user state in
/// %LOCALAPPDATA% is never read or written.
/// </summary>
internal sealed class ConnectInstallation
{
    private static readonly string[] RequiredFiles =
    [
        ConnectInstaller.AppExecutable,
        ConnectInstaller.TransportExecutable,
        ConnectInstaller.SettingsFile,
        ConnectInstaller.UninstallerExecutable,
        BuildInfoFile
    ];

    public const string BuildInfoFile = "build-info.json";

    public ConnectInstallation(string installRoot)
    {
        InstallRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installRoot));
        Parent = Path.GetDirectoryName(InstallRoot) ??
                 throw new ArgumentException("The install folder needs a parent folder.", nameof(installRoot));
        Name = Path.GetFileName(InstallRoot);
    }

    public string InstallRoot { get; }

    public string Parent { get; }

    private string Name { get; }

    public string InstalledApp => Path.Combine(InstallRoot, ConnectInstaller.AppExecutable);

    /// <summary>
    /// Puts right what an interrupted run left: if the install folder is missing but one previous
    /// copy is next to it (a crash between the two renames), that copy comes back. Staging folders
    /// and previous copies of a complete install are removed.
    /// </summary>
    public void RecoverInterrupted()
    {
        var previous = Directory.EnumerateDirectories(Parent, Name + ".old-*").ToList();
        if (!Directory.Exists(InstallRoot) && previous.Count > 0)
        {
            var newest = previous.OrderByDescending(Directory.GetLastWriteTimeUtc).First();
            Directory.Move(newest, InstallRoot);
            previous.Remove(newest);
        }

        foreach (var stale in Directory.EnumerateDirectories(Parent, Name + ".new-*")
                     .Concat(Directory.EnumerateDirectories(Parent, Name + ".failed-*"))
                     .Concat(previous))
        {
            TryDelete(stale);
        }
    }

    /// <summary>Unpacks the payload into a fresh sibling folder and checks it is a whole app.</summary>
    public string Stage(Stream payload)
    {
        var staging = Path.Combine(Parent, $"{Name}.new-{Suffix()}");
        try
        {
            Extract(payload, staging);
            foreach (var required in RequiredFiles)
            {
                if (!File.Exists(Path.Combine(staging, required)))
                {
                    throw new InvalidDataException($"The installer payload is incomplete: {required} is missing.");
                }
            }

            return staging;
        }
        catch
        {
            TryDelete(staging);
            throw;
        }
    }

    /// <summary>
    /// Swaps the staged folder in. Returns the previous folder (null on a first install), still on
    /// disk until <see cref="Commit"/> or <see cref="RollBack"/>. On failure the old app is back
    /// in place and the staged folder is gone.
    /// </summary>
    public string? Swap(string staging)
    {
        string? previous = null;
        try
        {
            if (Directory.Exists(InstallRoot))
            {
                previous = Path.Combine(Parent, $"{Name}.old-{Suffix()}");
                Directory.Move(InstallRoot, previous);
            }

            Directory.Move(staging, InstallRoot);
            return previous;
        }
        catch
        {
            if (previous is not null && !Directory.Exists(InstallRoot) && Directory.Exists(previous))
            {
                Directory.Move(previous, InstallRoot);
            }

            TryDelete(staging);
            throw;
        }
    }

    /// <summary>The new app is confirmed: the previous copy is no longer needed.</summary>
    public void Commit(string? previous)
    {
        if (previous is not null)
        {
            TryDelete(previous);
        }
    }

    /// <summary>Puts the previous copy back. The new files are moved aside first, then removed.</summary>
    public void RollBack(string? previous)
    {
        if (previous is null || !Directory.Exists(previous))
        {
            throw new InvalidOperationException("There is no previous copy to restore.");
        }

        string? failed = null;
        if (Directory.Exists(InstallRoot))
        {
            failed = Path.Combine(Parent, $"{Name}.failed-{Suffix()}");
            Directory.Move(InstallRoot, failed);
        }

        try
        {
            Directory.Move(previous, InstallRoot);
        }
        catch
        {
            if (failed is not null && !Directory.Exists(InstallRoot))
            {
                Directory.Move(failed, InstallRoot);
            }

            throw;
        }

        if (failed is not null)
        {
            TryDelete(failed);
        }
    }

    /// <summary>The release in a folder, from the build-info.json the release pipeline writes next to the app.</summary>
    public static (string Version, int Build)? ReadBuild(string folder)
    {
        try
        {
            var bytes = File.ReadAllBytes(Path.Combine(folder, BuildInfoFile));
            var span = bytes.AsSpan();
            if (span.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
            {
                span = span[3..];
            }

            using var document = JsonDocument.Parse(span.ToArray());
            var root = document.RootElement;
            var version = root.GetProperty("productVersion").GetString();
            var build = root.GetProperty("buildRevision").GetInt32();
            return version is { Length: > 0 } && build > 0 ? (version, build) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or
                                              KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Version first, then build: 1.5 Build 12 is newer than 1.5 Build 11 and never "1.5.12".</summary>
    public static int Compare((string Version, int Build) left, (string Version, int Build) right)
    {
        static (int Major, int Minor) Parts(string version)
        {
            var parts = version.Split('.');
            return parts.Length == 2 &&
                   int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major) &&
                   int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
                ? (major, minor)
                : (0, 0);
        }

        var byVersion = Parts(left.Version).CompareTo(Parts(right.Version));
        return byVersion != 0 ? byVersion : left.Build.CompareTo(right.Build);
    }

    /// <summary>Unpacks the payload, refusing any entry that would land outside the target folder.</summary>
    private static void Extract(Stream payload, string target)
    {
        Directory.CreateDirectory(target);
        var root = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
        using var archive = new ZipArchive(payload, ZipArchiveMode.Read, leaveOpen: true);
        foreach (var entry in archive.Entries)
        {
            var destination = Path.GetFullPath(Path.Combine(root, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"The installer payload contains an unsafe path: {entry.FullName}");
            }

            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: false);
        }
    }

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    internal static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
