using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ServerManager.Connect.App.Updates;

public enum InstallationKind
{
    /// <summary>Installed by 1SalemConnect-Setup.exe in Program Files: updates itself in place.</summary>
    Installed,

    /// <summary>Run from any other folder (the portable package, or a build output): never overwritten.</summary>
    Portable
}

/// <summary>
/// Installed means this exact copy is the one Setup put in Program Files and registered with
/// Windows: the app folder is the install folder, the uninstaller sits next to the app (the
/// portable package has none), and Installed apps lists that folder. Anything else is portable.
/// </summary>
public static class InstallationDetector
{
    public const string InstallFolderName = "1Salem Connect";
    public const string UninstallerFileName = "1Salem.Connect.Setup.exe";
    public const string UninstallKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\1SalemConnect";

    public static string DefaultInstallRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), InstallFolderName);

    public static InstallationKind Detect() =>
        Detect(AppContext.BaseDirectory, DefaultInstallRoot, ReadRegisteredInstallLocation);

    public static InstallationKind Detect(string appDirectory, string installRoot, Func<string?> registeredInstallLocation)
    {
        ArgumentNullException.ThrowIfNull(registeredInstallLocation);
        var here = Normalize(appDirectory);
        var root = Normalize(installRoot);
        if (here is null || root is null || !here.Equals(root, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(Path.Combine(root, UninstallerFileName)))
        {
            return InstallationKind.Portable;
        }

        var registered = Normalize(registeredInstallLocation());
        return registered is not null && registered.Equals(root, StringComparison.OrdinalIgnoreCase)
            ? InstallationKind.Installed
            : InstallationKind.Portable;
    }

    private static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim().Trim('"')));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string? ReadRegisteredInstallLocation()
    {
        try
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = machine.OpenSubKey(UninstallKeyPath);
            return key?.GetValue("InstallLocation") as string;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }
}

/// <summary>An update this app handed to Setup, kept until the next start says how it ended.</summary>
public sealed record PendingUpdate(
    string Token,
    string FromVersion,
    int FromBuild,
    string ToVersion,
    int ToBuild,
    DateTimeOffset StartedUtc);

/// <summary>
/// <see cref="LastCheckedUtc"/> is the last check that got an answer (shown in Settings);
/// <see cref="LastAttemptUtc"/> is the last try, answered or not, which paces automatic checks.
/// </summary>
public sealed record UpdateState(DateTimeOffset? LastCheckedUtc = null, PendingUpdate? Pending = null, DateTimeOffset? LastAttemptUtc = null);

/// <summary>
/// <c>%LOCALAPPDATA%\1Salem Connect\updates\update-state.json</c>: when updates were last checked
/// and the update in progress, if any. Written atomically next to the friend's other state, which
/// it never touches; an unreadable file is treated as empty rather than blocking the app.
/// </summary>
public sealed class UpdateStateStore
{
    public const string FileName = "update-state.json";
    private const int MaxBytes = 16 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string? _path;
    private UpdateState _memory = new();

    /// <summary>A null path keeps the state for this run only (tests, or an app that may not write).</summary>
    public UpdateStateStore(string? path)
    {
        _path = path is null ? null : Path.GetFullPath(path);
    }

    public static string DefaultDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "1Salem Connect", "updates");

    public static string DefaultPath() => Path.Combine(DefaultDirectory, FileName);

    public UpdateState Load()
    {
        if (_path is null)
        {
            return _memory;
        }

        try
        {
            var info = new FileInfo(_path);
            if (!info.Exists || info.Length > MaxBytes)
            {
                return new UpdateState();
            }

            return JsonSerializer.Deserialize<UpdateState>(File.ReadAllBytes(_path), Json) ?? new UpdateState();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new UpdateState();
        }
    }

    public void Save(UpdateState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (_path is null)
        {
            _memory = state;
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, state, Json);
            }

            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}

/// <summary>
/// How the app and Setup's update mode talk. The app starts Setup with these arguments; Setup
/// creates the named event before it relaunches the app and commits the update only when the new
/// build signals it; Setup leaves its outcome where every user can read it. The same constants are
/// in tools/ServerManager.Connect.Setup/UpdateProtocol.cs, and a test keeps the two equal.
/// </summary>
public static partial class UpdateProtocol
{
    public const string UpdateArgument = "--update";
    public const string WaitPidArgument = "--wait-pid";
    public const string ExpectedVersionArgument = "--expected-version";
    public const string ExpectedBuildArgument = "--expected-build";
    public const string TokenArgument = "--token";
    public const string EventPrefix = @"Global\1SalemConnect.Update.";

    public static string ResultDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "1Salem Connect", "updates");

    public static string NewToken() => Guid.NewGuid().ToString("N");

    public static bool IsToken(string? value) => value is not null && TokenPattern().IsMatch(value);

    public static string EventName(string token) =>
        IsToken(token) ? EventPrefix + token : throw new ArgumentException("Not an update token.", nameof(token));

    public static string ResultPath(string token) =>
        IsToken(token)
            ? Path.Combine(ResultDirectory, $"result-{token}.json")
            : throw new ArgumentException("Not an update token.", nameof(token));

    /// <summary>The installer's command line: numbers, a two-part version and a hex token, so nothing needs quoting.</summary>
    public static string Arguments(int waitPid, ConnectBuild target, string token)
    {
        if (!IsToken(token))
        {
            throw new ArgumentException("Not an update token.", nameof(token));
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{UpdateArgument} {WaitPidArgument} {waitPid} {ExpectedVersionArgument} {target.ProductVersion} {ExpectedBuildArgument} {target.BuildRevision} {TokenArgument} {token}");
    }

    [GeneratedRegex("^[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();
}

/// <summary>Setup's own account of an update, from its result file.</summary>
public sealed record InstallerResult(string Outcome, int? FromBuild, int? ToBuild, string? Message)
{
    public const string Updated = "Updated";
    public const string RolledBack = "RolledBack";
    public const string Blocked = "Blocked";
    public const string Failed = "Failed";
}

public interface IUpdateHandshake
{
    /// <summary>Tells Setup that this (new) build started. False when Setup is no longer waiting.</summary>
    bool SignalStarted(string token);

    InstallerResult? ReadResult(string token);
}

public sealed class UpdateHandshake : IUpdateHandshake
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public bool SignalStarted(string token)
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(UpdateProtocol.EventName(token), out var handle))
            {
                return false;
            }

            using (handle)
            {
                return handle.Set();
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException or ArgumentException)
        {
            return false;
        }
    }

    public InstallerResult? ReadResult(string token)
    {
        try
        {
            var path = UpdateProtocol.ResultPath(token);
            var info = new FileInfo(path);
            return info.Exists && info.Length <= 16 * 1024
                ? JsonSerializer.Deserialize<InstallerResult>(File.ReadAllBytes(path), Json)
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return null;
        }
    }
}

public enum InstallerLaunchOutcome
{
    Started,

    /// <summary>The friend said no to the Windows administrator prompt.</summary>
    Cancelled,

    Failed
}

public interface IUpdateInstallerLauncher
{
    InstallerLaunchOutcome Launch(string installerPath, string expectedSha256, string arguments);
}

/// <summary>
/// Starts the verified installer with the Windows administrator prompt, and only that: the app
/// itself never runs elevated. The installer is held open with writing and deleting refused from
/// the moment it is hashed again until Windows has started it, so the file that runs is the file
/// that was verified, even though it sits in the friend's own (writable) folder.
/// </summary>
public sealed class ElevatedInstallerLauncher : IUpdateInstallerLauncher
{
    private const int ErrorCancelled = 1223;

    public InstallerLaunchOutcome Launch(string installerPath, string expectedSha256, string arguments)
    {
        using var locked = new FileStream(installerPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var actual = SHA256.HashData(locked);
        if (!CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(expectedSha256)))
        {
            throw new UpdateDownloadException(UpdateDownloadFailure.HashMismatch, "The downloaded installer changed after it was verified.");
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo(installerPath, arguments)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(installerPath)!
            });
            return process is null ? InstallerLaunchOutcome.Failed : InstallerLaunchOutcome.Started;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == ErrorCancelled)
        {
            return InstallerLaunchOutcome.Cancelled;
        }
    }
}
