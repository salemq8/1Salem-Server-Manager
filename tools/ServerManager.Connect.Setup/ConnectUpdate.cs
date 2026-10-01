using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ServerManager.Connect.Setup;

/// <summary>
/// How Setup's update mode and the app talk. The same constants are in
/// src/ServerManager.Connect.App/Updates/UpdateEnvironment.cs, and a test keeps the two equal.
/// </summary>
internal static partial class UpdateProtocol
{
    public const string UpdateArgument = "--update";
    public const string WaitPidArgument = "--wait-pid";
    public const string ExpectedVersionArgument = "--expected-version";
    public const string ExpectedBuildArgument = "--expected-build";
    public const string TokenArgument = "--token";
    public const string EventPrefix = @"Global\1SalemConnect.Update.";

    public static string ResultDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "1Salem Connect", "updates");

    public static bool IsToken(string? value) => value is not null && TokenPattern().IsMatch(value);

    [GeneratedRegex("^[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();
}

/// <summary>What the app asked Setup to do: replace the installed build with exactly this one.</summary>
internal sealed partial record UpdateRequest(int WaitPid, string ExpectedVersion, int ExpectedBuild, string Token)
{
    public static UpdateRequest? Parse(IReadOnlyList<string> args)
    {
        string? Value(string name)
        {
            for (var index = 0; index < args.Count - 1; index++)
            {
                if (args[index].Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    return args[index + 1];
                }
            }

            return null;
        }

        if (!args.Any(arg => arg.Equals(UpdateProtocol.UpdateArgument, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var version = Value(UpdateProtocol.ExpectedVersionArgument);
        return int.TryParse(Value(UpdateProtocol.WaitPidArgument), NumberStyles.None, CultureInfo.InvariantCulture, out var pid) &&
               int.TryParse(Value(UpdateProtocol.ExpectedBuildArgument), NumberStyles.None, CultureInfo.InvariantCulture, out var build) &&
               build > 0 && pid > 0 &&
               version is not null && VersionPattern().IsMatch(version) &&
               Value(UpdateProtocol.TokenArgument) is { } token && UpdateProtocol.IsToken(token)
            ? new UpdateRequest(pid, version, build, token)
            : null;
    }

    [GeneratedRegex(@"^\d{1,4}\.\d{1,4}$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
}

internal enum UpdateOutcome
{
    Updated = 0,
    Failed = 1,
    Blocked = 2,
    RolledBack = 3,

    /// <summary>The new build is installed (complete) but never said it started, and the previous one could not be put back.</summary>
    NewBuildUnconfirmed = 4
}

internal sealed record UpdateRunResult(UpdateOutcome Outcome, int? FromBuild, int? ToBuild, string Message);

/// <summary>How long an update waits: for the app to close, for other windows, for the new build to start.</summary>
internal sealed record UpdateTimings(TimeSpan AppExit, TimeSpan OtherWindows, TimeSpan Start, TimeSpan Poll)
{
    public static UpdateTimings Default { get; } = new(
        TimeSpan.FromSeconds(60),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMilliseconds(500));
}

/// <summary>The machine around an update: processes, launching, Windows registration, the start signal. Faked in tests.</summary>
internal interface IUpdateHost
{
    /// <summary>Waits for one process to end. True once it has (or never existed).</summary>
    bool WaitForExit(int processId, TimeSpan timeout);

    /// <summary>
    /// Process ids running from inside the folder, found by their path, never by name. With
    /// <paramref name="thisSessionOnly"/>, only those in this Windows session (the friend's own,
    /// since elevation keeps the session) count.
    /// </summary>
    IReadOnlyList<int> ProcessesUnder(string folder, bool thisSessionOnly = false);

    void Kill(int processId);

    /// <summary>Starts the app as the signed-in user, not elevated.</summary>
    void LaunchAsUser(string executable);

    void Register();

    /// <summary>Prepares the signal the new build sets when it starts. False if it cannot be made safely.</summary>
    bool PrepareStartSignal(string token);

    bool WaitForStartSignal(TimeSpan timeout);

    void WriteResult(string token, UpdateRunResult result);
}

/// <summary>
/// Setup's update mode. The app has already downloaded and verified this installer and closed
/// itself; Setup waits for it to be gone, checks that its own payload is exactly the build the
/// app asked for and not older than what is installed, swaps the folders, relaunches the app as
/// the signed-in user and waits for the new build to say it started. Only then is the previous
/// build removed; if the new build does not start, the previous one is put back and opened.
/// Per-user data (%LOCALAPPDATA%\1Salem Connect) is never touched, so invitations, device
/// identity, servers and preferences carry over unchanged.
/// </summary>
internal sealed class ConnectUpdate(ConnectInstallation installation, IUpdateHost host, UpdateTimings? timings = null)
{
    private readonly UpdateTimings _timings = timings ?? UpdateTimings.Default;

    public UpdateRunResult Run(UpdateRequest request, Func<Stream?> openPayload, IProgress<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(openPayload);
        var installed = ConnectInstallation.ReadBuild(installation.InstallRoot);
        var result = RunCore(request, openPayload, installed, progress);
        try
        {
            host.WriteResult(request.Token, result);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The app still tells the outcome from its own build; only the reason would be missing.
        }

        // Written first, so the app opened now finds the reason. An update that went through has
        // already opened the new build (that is how it was confirmed).
        if (result.Outcome != UpdateOutcome.Updated)
        {
            OpenInstalledApp();
        }

        return result;
    }

    /// <summary>
    /// An update Setup could not even begin (another Setup holds the lock): the app has already
    /// closed for it, so say why where the app will look, and open it again.
    /// </summary>
    public UpdateRunResult Refuse(UpdateRequest request, UpdateOutcome outcome, string message)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = new UpdateRunResult(outcome, ConnectInstallation.ReadBuild(installation.InstallRoot)?.Build, request.ExpectedBuild, message);
        try
        {
            host.WriteResult(request.Token, result);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        OpenInstalledApp();
        return result;
    }

    private UpdateRunResult RunCore(
        UpdateRequest request,
        Func<Stream?> openPayload,
        (string Version, int Build)? installed,
        IProgress<string>? progress)
    {
        var from = installed?.Build;
        var target = (request.ExpectedVersion, request.ExpectedBuild);

        progress?.Report("Waiting for 1Salem Connect to close");
        if (!host.WaitForExit(request.WaitPid, _timings.AppExit) || !WaitForOtherWindows())
        {
            return Unchanged(UpdateOutcome.Blocked, from, request.ExpectedBuild,
                "1Salem Connect is still open in another window. Close it, then try again.");
        }

        string? staging = null;
        var readyToSwap = false;
        try
        {
            installation.RecoverInterrupted();
            installed = ConnectInstallation.ReadBuild(installation.InstallRoot);
            from = installed?.Build;
            using (var payload = openPayload())
            {
                if (payload is null)
                {
                    return Unchanged(UpdateOutcome.Failed, from, request.ExpectedBuild,
                        "This copy of Setup does not contain 1Salem Connect.");
                }

                progress?.Report("Copying files");
                staging = installation.Stage(payload);
            }

            var offered = ConnectInstallation.ReadBuild(staging);
            if (offered is not { } payloadBuild || ConnectInstallation.Compare(payloadBuild, target) != 0)
            {
                return Unchanged(UpdateOutcome.Failed, from, request.ExpectedBuild,
                    "This installer does not contain the build that was asked for.");
            }

            if (installed is { } current && ConnectInstallation.Compare(payloadBuild, current) < 0)
            {
                return Unchanged(UpdateOutcome.Failed, from, request.ExpectedBuild,
                    "This installer is older than the installed 1Salem Connect.");
            }

            if (!host.PrepareStartSignal(request.Token))
            {
                return Unchanged(UpdateOutcome.Failed, from, request.ExpectedBuild,
                    "The update could not be prepared safely.");
            }

            readyToSwap = true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return Unchanged(UpdateOutcome.Failed, from, request.ExpectedBuild, "The update could not be prepared: " + exception.Message);
        }
        finally
        {
            // Every early return leaves the installed app exactly as it was, with no staged copy.
            if (!readyToSwap && staging is not null)
            {
                ConnectInstallation.TryDelete(staging);
            }
        }

        string? previous;
        try
        {
            progress?.Report("Installing");
            previous = installation.Swap(staging!);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Swap has already put the previous build back and removed the staged copy.
            return Unchanged(UpdateOutcome.Failed, from, request.ExpectedBuild, "The files could not be replaced: " + exception.Message);
        }

        try
        {
            host.Register();
            progress?.Report("Starting the new version");
            host.LaunchAsUser(installation.InstalledApp);
            if (host.WaitForStartSignal(_timings.Start))
            {
                installation.Commit(previous);
                return new UpdateRunResult(UpdateOutcome.Updated, from, request.ExpectedBuild,
                    "1Salem Connect was updated.");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Falls through to restoring the previous build.
        }

        return RestorePrevious(previous, from, request.ExpectedBuild);
    }

    /// <summary>The new build did not start: stop it, put the previous build back, register and open it.</summary>
    private UpdateRunResult RestorePrevious(string? previous, int? from, int to)
    {
        if (previous is null)
        {
            return new UpdateRunResult(UpdateOutcome.NewBuildUnconfirmed, from, to,
                "The new version is installed but did not confirm that it started, and there was no earlier version to go back to.");
        }

        try
        {
            foreach (var process in host.ProcessesUnder(installation.InstallRoot))
            {
                host.Kill(process);
            }

            installation.RollBack(previous);
            host.Register();
            return new UpdateRunResult(UpdateOutcome.RolledBack, from, to,
                "The new version did not start, so the previous version was put back.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // The new build stays: complete, just unconfirmed. Never half of each.
            return new UpdateRunResult(UpdateOutcome.NewBuildUnconfirmed, from, to,
                "The new version did not confirm that it started, and the previous version could not be put back: " + exception.Message);
        }
    }

    /// <summary>Nothing was changed; the app that closed for the update is opened again afterwards.</summary>
    private static UpdateRunResult Unchanged(UpdateOutcome outcome, int? from, int to, string message) =>
        new(outcome, from, to, message);

    /// <summary>Opens whatever build is installed now, unless the friend already has a copy running.</summary>
    private void OpenInstalledApp()
    {
        try
        {
            // Another user's copy (fast user switching) does not stand in for the friend's own.
            if (File.Exists(installation.InstalledApp) && host.ProcessesUnder(installation.InstallRoot, thisSessionOnly: true).Count == 0)
            {
                host.LaunchAsUser(installation.InstalledApp);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private bool WaitForOtherWindows()
    {
        var deadline = Stopwatch.StartNew();
        while (host.ProcessesUnder(installation.InstallRoot).Count > 0)
        {
            if (deadline.Elapsed >= _timings.OtherWindows)
            {
                return false;
            }

            Thread.Sleep(_timings.Poll);
        }

        return true;
    }
}

/// <summary>The real machine: Windows processes, Explorer for a non-elevated start, the registry, a named event.</summary>
internal sealed class WindowsUpdateHost(string installRoot) : IUpdateHost, IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private EventWaitHandle? _started;

    public bool WaitForExit(int processId, TimeSpan timeout)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return true;
        }

        using (process)
        {
            try
            {
                return process.WaitForExit(timeout);
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return true;
            }
        }
    }

    public IReadOnlyList<int> ProcessesUnder(string folder, bool thisSessionOnly = false)
    {
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        var self = Environment.ProcessId;
        int session;
        using (var current = Process.GetCurrentProcess())
        {
            session = current.SessionId;
        }

        var found = new List<int>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.Id != self &&
                    (!thisSessionOnly || process.SessionId == session) &&
                    process.MainModule?.FileName is { } path &&
                    path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    found.Add(process.Id);
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Exited, or not ours to inspect.
            }
            finally
            {
                process.Dispose();
            }
        }

        return found;
    }

    public void Kill(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installRoot)) + Path.DirectorySeparatorChar;
            if (process.MainModule?.FileName is { } path && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(TimeSpan.FromSeconds(10));
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    public void LaunchAsUser(string executable)
    {
        // Explorer starts it in the signed-in user's session, unelevated, even though this
        // process runs as administrator (possibly a different account).
        using var _ = Process.Start(new ProcessStartInfo("explorer.exe", $"\"{executable}\"") { UseShellExecute = false });
    }

    public void Register() => ConnectInstaller.Register();

    public bool PrepareStartSignal(string token)
    {
        var security = new EventWaitHandleSecurity();
        security.AddAccessRule(new EventWaitHandleAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), EventWaitHandleRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new EventWaitHandleAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), EventWaitHandleRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new EventWaitHandleAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            EventWaitHandleRights.Modify | EventWaitHandleRights.Synchronize,
            AccessControlType.Allow));
        _started = EventWaitHandleAcl.Create(false, EventResetMode.ManualReset, UpdateProtocol.EventPrefix + token, out var createdNew, security);
        if (!createdNew)
        {
            // Someone else made this name first; their signal would mean nothing.
            _started.Dispose();
            _started = null;
            return false;
        }

        return true;
    }

    public bool WaitForStartSignal(TimeSpan timeout) => _started?.WaitOne(timeout) == true;

    public void WriteResult(string token, UpdateRunResult result)
    {
        if (!UpdateProtocol.IsToken(token) || ResultFolder.Trusted(UpdateProtocol.ResultDirectory) is not { } folder)
        {
            // Never written where another account could have made or redirected the folder; the
            // app still tells the outcome from its own build.
            return;
        }

        foreach (var old in Directory.EnumerateFiles(folder, "result-*.json"))
        {
            var info = new FileInfo(old);
            if ((info.Attributes & FileAttributes.ReparsePoint) == 0 && info.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-7))
            {
                info.Delete();
            }
        }

        using var stream = new FileStream(Path.Combine(folder, $"result-{token}.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                outcome = result.Outcome.ToString(),
                fromBuild = result.FromBuild,
                toBuild = result.ToBuild,
                message = result.Message
            },
            Json));
    }

    public void Dispose() => _started?.Dispose();
}

/// <summary>
/// %ProgramData%\1Salem Connect\updates, where Setup leaves each update's outcome for the app. Setup
/// runs as administrator and %ProgramData% lets any user make folders, so the folder (and its
/// parent) is created with its own ACL: SYSTEM and Administrators full control, Users read only,
/// nothing inherited. An existing folder is used only if it is a real folder (not a link) owned
/// by SYSTEM, Administrators or this administrator; otherwise nothing is written there.
/// </summary>
internal static class ResultFolder
{
    public static string? Trusted(string resultDirectory)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(resultDirectory));
        var parent = Path.GetDirectoryName(full);
        return parent is not null && EnsureProtected(parent) && EnsureProtected(full) ? full : null;
    }

    private static bool EnsureProtected(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            if (!info.Exists)
            {
                var security = new DirectorySecurity();
                security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
                security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
                security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
                if (WindowsIdentity.GetCurrent().User is { } self)
                {
                    security.AddAccessRule(new FileSystemAccessRule(self, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
                }

                security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
                info.Create(security);
                info.Refresh();
            }

            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }

            var owner = info.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            return owner is not null &&
                   (owner.IsWellKnown(WellKnownSidType.LocalSystemSid) ||
                    owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) ||
                    (WindowsIdentity.GetCurrent().User is { } administrator && owner.Equals(administrator)));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }
}
