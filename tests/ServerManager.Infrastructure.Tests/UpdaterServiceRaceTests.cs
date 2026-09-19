using System.Text.Json;
using ServerManager.Contracts;
using ServerManager.Infrastructure.Updates;
using ServerManager.Infrastructure.Windows;

namespace ServerManager.Infrastructure.Tests;

/// <summary>
/// The real Version 1.5 Build 4 install failed here: the stop control was accepted, the
/// installer treated that as "stopped", and replacing the Agent directory a moment later hit
/// access denied because the service was still STOP_PENDING with its files open. These tests
/// hold the installer to the actual service state instead.
/// </summary>
public sealed class UpdaterServiceRaceTests : IDisposable
{
    private readonly List<string> _temporaryRoots = [];

    [Fact]
    public async Task AgentDirectory_IsNotTouchedWhileTheServiceIsStillStopPending()
    {
        var (installRoot, dataRoot) = CreateInstalledLayout("1.3.1");
        var control = new StopPendingServiceControl(
            pendingPolls: 3,
            agentDirectory: Path.Combine(installRoot, "Agent"));
        var installer = CreateInstaller(control);

        var result = await installer.ApplyAsync(CreateOptions(installRoot, dataRoot));

        Assert.True(result.Success, result.Message);

        // The whole point: at no moment while the service reported StopPending had the Agent
        // directory been modified. The old implementation replaced it on the first poll.
        Assert.False(
            control.AgentChangedWhileStopPending,
            "The Agent directory was modified while the service was still STOP_PENDING.");
        Assert.True(control.ReachedStopped, "The installer never waited for a real Stopped.");
        Assert.Equal(1, control.StopRequests);
        Assert.Equal("agent-1.3.2", await File.ReadAllTextAsync(
            Path.Combine(installRoot, "Agent", "1Salem.ServerManager.Agent.exe")));
    }

    [Fact]
    public async Task StopThatNeverCompletes_AbortsBeforeReplacingTheAgentDirectory()
    {
        var (installRoot, dataRoot) = CreateInstalledLayout("1.3.1");
        var control = new NeverStopsServiceControl();
        var installer = CreateInstaller(control);

        var result = await installer.ApplyAsync(CreateOptions(installRoot, dataRoot));

        Assert.False(result.Success);
        // The installed Agent must be exactly as it was: a service that is merely slow to stop
        // must not cost the installation its Agent, nor require a rollback to get it back.
        Assert.Equal("agent-old", await File.ReadAllTextAsync(
            Path.Combine(installRoot, "Agent", "1Salem.ServerManager.Agent.exe")));
        Assert.Contains("did not reach the state", result.Message, StringComparison.Ordinal);
        Assert.Contains("StopPending", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecoveryStart_DoesNotRepeatStartAgainstAnAlreadyRunningService()
    {
        var (installRoot, dataRoot) = CreateInstalledLayout("1.3.1");
        var control = new HealthyFakeServiceControl();
        var installer = new VersionedUpdateInstaller(
            control,
            (_, _, _) => throw new InvalidOperationException("simulated health failure"),
            () => false,
            (_, _) => Task.CompletedTask);

        await installer.ApplyAsync(CreateOptions(installRoot, dataRoot));

        // Two stop/start pairs: the install itself, then recovery. Never two starts in a row,
        // which is what produced ERROR_SERVICE_ALREADY_RUNNING (1056).
        Assert.Equal(["stop", "start", "stop", "start"], control.Operations);
    }

    [Fact]
    public async Task SuccessfulActivation_NeverReportsServiceAlreadyRunning()
    {
        var (installRoot, dataRoot) = CreateInstalledLayout("1.3.1");
        var control = new StopPendingServiceControl(
            pendingPolls: 2,
            agentDirectory: Path.Combine(installRoot, "Agent"));
        var installer = CreateInstaller(control);

        var result = await installer.ApplyAsync(CreateOptions(installRoot, dataRoot));

        Assert.True(result.Success, result.Message);
        Assert.DoesNotContain("1056", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Access to the path", result.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Service waits use a virtual clock that only advances when the state machine polls, so a
    /// 60-second timeout is reached in a few hundred iterations instead of 60 real seconds.
    /// </summary>
    private static VersionedUpdateInstaller CreateInstaller(IWindowsServiceControl control)
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        return new VersionedUpdateInstaller(
            control,
            (_, _, _) => Task.CompletedTask,
            () => false,
            (duration, _) =>
            {
                now = now.Add(duration);
                return Task.CompletedTask;
            },
            () => now);
    }

    private static VersionedUpdateOptions CreateOptions(string installRoot, string dataRoot) =>
        new(
            CreateVersionedPackage(installRoot, "1.3.2"),
            installRoot,
            dataRoot,
            "1.3.2",
            "test-agent",
            ActivateAgent: true,
            SkipBinaryVersionVerification: true,
            UpdateRegistry: false);

    public void Dispose()
    {
        foreach (var root in _temporaryRoots)
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private (string InstallRoot, string DataRoot) CreateInstalledLayout(string installedVersion)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"1salem-service-race-{Guid.NewGuid():N}");
        _temporaryRoots.Add(root);
        var installRoot = Path.Combine(root, "install");
        var dataRoot = Path.Combine(root, "data");
        Directory.CreateDirectory(Path.Combine(installRoot, "Client", "Updater"));
        Directory.CreateDirectory(Path.Combine(installRoot, "Agent"));
        Directory.CreateDirectory(dataRoot);
        File.WriteAllText(
            Path.Combine(installRoot, "Client", "1Salem.ServerManager.exe"),
            "stable-launcher-old");
        File.WriteAllText(
            Path.Combine(installRoot, "Client", "Updater", "1Salem.ServerManager.Updater.exe"),
            "updater-old");
        File.WriteAllText(
            Path.Combine(installRoot, "Agent", "1Salem.ServerManager.Agent.exe"),
            "agent-old");

        var manifest = new InstalledApplicationManifest(
            1,
            installedVersion,
            null,
            null,
            ProductIdentity.StableChannel,
            Path.Combine(installRoot, "Client", "1Salem.ServerManager.exe"),
            Path.Combine(installRoot, "Client", "1Salem.ServerManager.exe"),
            Path.Combine(installRoot, "Agent", "1Salem.ServerManager.Agent.exe"),
            Path.Combine(installRoot, "Client", "Updater", "1Salem.ServerManager.Updater.exe"),
            null,
            "Succeeded",
            DateTimeOffset.UtcNow);
        var json = JsonSerializer.Serialize(
            manifest,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        File.WriteAllText(Path.Combine(installRoot, "current.json"), json);
        File.WriteAllText(Path.Combine(dataRoot, "installation.json"), json);
        return (installRoot, dataRoot);
    }

    private static string CreateVersionedPackage(string installRoot, string version)
    {
        var staging = Path.Combine(
            Path.GetDirectoryName(installRoot)!,
            $"package-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(staging, "Client", "Launcher"));
        Directory.CreateDirectory(Path.Combine(staging, "Client", "Updater"));
        Directory.CreateDirectory(Path.Combine(staging, "Agent"));
        File.WriteAllText(
            Path.Combine(staging, "Client", "1Salem.ServerManager.exe"),
            $"client-{version}");
        File.WriteAllText(
            Path.Combine(staging, "Client", "Launcher", "1Salem.ServerManager.Launcher.exe"),
            $"stable-launcher-{version}");
        File.WriteAllText(
            Path.Combine(staging, "Client", "Updater", "1Salem.ServerManager.Updater.exe"),
            $"updater-{version}");
        File.WriteAllText(
            Path.Combine(staging, "Agent", "1Salem.ServerManager.Agent.exe"),
            $"agent-{version}");

        var packagePath = Path.Combine(
            Path.GetDirectoryName(installRoot)!,
            $"update-{version}-{Guid.NewGuid():N}.zip");
        System.IO.Compression.ZipFile.CreateFromDirectory(staging, packagePath);
        Directory.Delete(staging, recursive: true);
        return packagePath;
    }
}

/// <summary>
/// Reports StopPending for a configured number of polls after the stop is requested, then
/// Stopped -- the real Windows behaviour the old implementation raced. Watches the Agent
/// directory so the test can prove nothing touched it during the pending window.
/// </summary>
internal sealed class StopPendingServiceControl(int pendingPolls, string agentDirectory)
    : IWindowsServiceControl
{
    private WindowsServiceState _state = WindowsServiceState.Running;
    private int _remainingPendingPolls = pendingPolls;
    private string? _agentFingerprintAtStop;

    public int StopRequests { get; private set; }

    public int StartRequests { get; private set; }

    public bool ReachedStopped { get; private set; }

    public bool AgentChangedWhileStopPending { get; private set; }

    public WindowsServiceState GetState(string serviceName)
    {
        if (_state == WindowsServiceState.StopPending)
        {
            if (Fingerprint() != _agentFingerprintAtStop)
            {
                AgentChangedWhileStopPending = true;
            }

            if (--_remainingPendingPolls <= 0)
            {
                _state = WindowsServiceState.Stopped;
                ReachedStopped = true;
            }
        }

        return _state;
    }

    public void RequestStop(string serviceName)
    {
        StopRequests++;
        _agentFingerprintAtStop = Fingerprint();
        _state = WindowsServiceState.StopPending;
    }

    public void RequestStart(string serviceName)
    {
        StartRequests++;
        _state = WindowsServiceState.Running;
    }

    private string Fingerprint()
    {
        if (!Directory.Exists(agentDirectory))
        {
            return "<missing>";
        }

        var files = Directory.GetFiles(agentDirectory, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
        return string.Join(
            "|",
            files.Select(path => $"{Path.GetFileName(path)}:{SafeRead(path)}"));
    }

    private static string SafeRead(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (IOException)
        {
            return "<locked>";
        }
    }
}

/// <summary>A service that accepts the stop but never finishes stopping.</summary>
internal sealed class NeverStopsServiceControl : IWindowsServiceControl
{
    private WindowsServiceState _state = WindowsServiceState.Running;

    public WindowsServiceState GetState(string serviceName) => _state;

    public void RequestStop(string serviceName) => _state = WindowsServiceState.StopPending;

    public void RequestStart(string serviceName) => _state = WindowsServiceState.Running;
}
