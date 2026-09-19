using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Win32;
using ServerManager.Contracts;

namespace ServerManager.Setup;

public sealed record InstallRequest(
    string Mode,
    string InstallRoot,
    bool DesktopShortcut,
    bool StartMenuShortcut,
    bool InstallAgentService,
    bool AddFirewallRules,
    bool StartWithWindows,
    bool LaunchAfterInstall);

public sealed record InstallProgress(int Percent, string Message);

public sealed record InstallResult(
    string LogPath,
    IReadOnlyList<string> Warnings,
    bool ServiceHealthVerified);

public static class InstallerEngine
{
    private const string MarkerName = ".1salem-install";
    private const string MarkerValue = "1Salem Server Manager v1";
    public static string ProductVersion { get; } =
        ProductIdentity.VersionOf(typeof(InstallerEngine).Assembly);
    public static int BuildRevision { get; } =
        ProductIdentity.BuildRevisionOf(typeof(InstallerEngine).Assembly);

    private static string VersionBuildPath(
        string installRoot,
        params string[] segments)
    {
        var parts = new List<string>
        {
            installRoot,
            "Versions",
            ProductVersion,
            "Builds",
            BuildRevision.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
        };
        parts.AddRange(segments);
        return Path.Combine(parts.ToArray());
    }
    private const string UninstallKeyPath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\1SalemServerManager";

    public static string? LastLogPath { get; private set; }

    public static async Task<InstallResult> InstallAsync(
        InstallRequest request,
        IProgress<InstallProgress> progress,
        CancellationToken cancellationToken = default)
    {
        using var log = new InstallerLog();
        LastLogPath = log.Path;
        var warnings = new List<string>();
        DeploymentJournal? journal = null;
        WindowsServiceInstaller? serviceInstaller = null;
        HttpAgentHealthProbe? healthProbe = null;
        InstalledServiceSnapshot? originalService = null;
        InstalledApplicationManifest? installationManifest = null;
        string? installationDataRoot = null;
        var staging = Path.Combine(
            Path.GetTempPath(),
            "1SalemServerManager-Setup",
            Guid.NewGuid().ToString("N"));

        try
        {
            await log.WriteAsync(
                "Setup",
                $"Starting 1Salem Server Manager {ProductVersion}; " +
                $"mode={request.Mode}; installRoot=\"{request.InstallRoot}\"; " +
                $"elevated={InstallerSecurity.IsElevated()}",
                cancellationToken);
            InstallerPreflight.ValidateRequest(
                request,
                InstallerSecurity.IsElevated());
            var installRoot = Path.GetFullPath(request.InstallRoot);
            var installedBefore = InstalledVersionDetector.Detect(
                new InstalledVersionDetectionOptions(installRoot));
            InstallerPreflight.ValidateDriveSpace(
                installRoot,
                500L * 1024 * 1024);

            progress.Report(new InstallProgress(5, "Pre-flight validation passed."));
            Directory.CreateDirectory(staging);
            var contentRoot = Path.Combine(staging, "content");
            Directory.CreateDirectory(contentRoot);

            progress.Report(new InstallProgress(12, "Extracting and validating payload..."));
            await ExtractPayloadAsync(contentRoot, cancellationToken);
            var includesClient = request.Mode is "AllInOne" or "ClientOnly";
            var includesAgent = request.Mode is "AllInOne" or "AgentOnly";
            InstallerPreflight.ValidatePayload(
                contentRoot,
                includesClient,
                includesAgent);
            InstallerPreflight.VerifyFolderWritable(installRoot);
            await log.WriteAsync(
                "Payload preflight",
                "Required payload files, destination write access, and free space verified.",
                cancellationToken);

            journal = new DeploymentJournal(
                installRoot,
                Path.Combine(staging, "rollback"));
            var runner = new InstallerProcessRunner(log);
            var backend = new ScServiceControlBackend(runner);
            healthProbe = new HttpAgentHealthProbe();
            serviceInstaller = new WindowsServiceInstaller(
                backend,
                healthProbe,
                log);

            if (includesAgent && request.InstallAgentService)
            {
                progress.Report(new InstallProgress(
                    20,
                    "Checking and safely stopping the existing Agent service..."));
                originalService = await serviceInstaller.PrepareForDeploymentAsync(
                    cancellationToken);
            }

            if (includesClient)
            {
                progress.Report(new InstallProgress(
                    25,
                    "Closing the dashboard for a safe repair..."));
                await StopInstalledDashboardAsync(
                    installRoot,
                    log,
                    cancellationToken);
            }

            journal.WriteText(
                Path.Combine(installRoot, MarkerName),
                MarkerValue);
            if (includesAgent)
            {
                progress.Report(new InstallProgress(32, "Deploying Agent files..."));
                journal.DeployDirectory(
                    Path.Combine(contentRoot, "Agent"),
                    Path.Combine(installRoot, "Agent"));
                journal.DeployDirectory(
                    Path.Combine(contentRoot, "Agent"),
                    VersionBuildPath(installRoot, "Agent"));
            }

            if (includesClient)
            {
                progress.Report(new InstallProgress(48, "Deploying dashboard files..."));
                journal.DeployDirectory(
                    Path.Combine(contentRoot, "Client"),
                    VersionBuildPath(installRoot, "Client"));
                journal.DeployDirectory(
                    Path.Combine(contentRoot, "Client", "Updater"),
                    Path.Combine(installRoot, "Client", "Updater"));
                journal.DeployFile(
                    Path.Combine(
                        contentRoot,
                        "Client",
                        "Launcher",
                        "1Salem.ServerManager.Launcher.exe"),
                    Path.Combine(
                        installRoot,
                        "Client",
                        "1Salem.ServerManager.exe"));
            }

            var uninstaller = Path.Combine(
                installRoot,
                "Uninstall 1Salem Server Manager.exe");
            journal.DeployFile(
                Environment.ProcessPath ??
                    throw new InvalidOperationException(
                        "Setup could not locate its own executable."),
                uninstaller);

            var agentExecutable = Path.Combine(
                installRoot,
                "Agent",
                "1Salem.ServerManager.Agent.exe");
            var serviceHealthVerified = false;
            if (includesAgent && request.InstallAgentService)
            {
                if (!File.Exists(agentExecutable))
                {
                    throw new InstallerFailureException(
                        "Agent service preflight",
                        "The Agent executable was not copied successfully.",
                        $"Expected file after deployment: {agentExecutable}");
                }

                progress.Report(new InstallProgress(
                    64,
                    "Creating or repairing the Agent Windows service..."));
                var dataRoot = Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.CommonApplicationData),
                    "1SalemServerManager");
                var serviceResult = await serviceInstaller.InstallOrRepairAsync(
                    agentExecutable,
                    dataRoot,
                    cancellationToken,
                    originalService);
                serviceHealthVerified = serviceResult.HealthVerified;
                await log.WriteAsync(
                    "Agent service",
                    $"Service ready. repaired={serviceResult.RepairedExistingService}; " +
                    $"state={serviceResult.State}; health={serviceResult.HealthVerified}; " +
                    $"binaryPath=\"{serviceResult.BinaryPath}\"",
                    cancellationToken);
            }

            if (request.AddFirewallRules)
            {
                progress.Report(new InstallProgress(
                    76,
                    "Configuring Private-network firewall rules..."));
                if (includesAgent)
                {
                    await AddFirewallRuleAsync(
                        runner,
                        "Agent firewall rule",
                        "1Salem Server Manager - Agent HTTPS",
                        "TCP",
                        5252,
                        agentExecutable,
                        warnings,
                        cancellationToken);
                }

                await AddFirewallRuleAsync(
                    runner,
                    "Minecraft firewall rule",
                    "1Salem Server Manager - Minecraft",
                    "TCP",
                    25565,
                    null,
                    warnings,
                    cancellationToken);
                await AddFirewallRuleAsync(
                    runner,
                    "Palworld firewall rule",
                    "1Salem Server Manager - Palworld",
                    "UDP",
                    8211,
                    null,
                    warnings,
                    cancellationToken);
            }

            var clientExecutable = Path.Combine(
                installRoot,
                "Client",
                "1Salem.ServerManager.exe");
            if (includesClient)
            {
                var dataRoot = Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.CommonApplicationData),
                    "1SalemServerManager");
                var versionClient = Path.Combine(
                    VersionBuildPath(installRoot),
                    "Client",
                    "1Salem.ServerManager.exe");
                var updater = Path.Combine(
                    installRoot,
                    "Client",
                    "Updater",
                    "1Salem.ServerManager.Updater.exe");
                installationManifest = new InstalledApplicationManifest(
                    2,
                    ProductVersion,
                    installedBefore.Client.Version,
                    installedBefore.Client.Version,
                    ProductIdentity.StableChannel,
                    clientExecutable,
                    versionClient,
                    agentExecutable,
                    updater,
                    null,
                    "Succeeded",
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow,
                    BuildRevision,
                    null,
                    null);
                journal.WriteText(
                    Path.Combine(installRoot, "current.json"),
                    JsonSerializer.Serialize(
                        installationManifest,
                        new JsonSerializerOptions(JsonSerializerDefaults.Web)
                        {
                            WriteIndented = true
                        }));
                installationDataRoot = dataRoot;
                progress.Report(new InstallProgress(
                    86,
                    "Creating shortcuts and dashboard startup settings..."));
                try
                {
                    CreateShortcuts(
                        clientExecutable,
                        installRoot,
                        request.DesktopShortcut,
                        request.StartMenuShortcut);
                    SetStartup(clientExecutable, request.StartWithWindows);
                }
                catch (Exception exception)
                {
                    var warning = $"Shortcut/startup configuration warning: {exception.Message}";
                    warnings.Add(warning);
                    await log.WriteAsync(
                        "Dashboard startup registration",
                        warning,
                        cancellationToken);
                }
            }

            progress.Report(new InstallProgress(94, "Registering maintenance information..."));
            RegisterUninstallEntry(
                installRoot,
                includesClient ? clientExecutable : uninstaller,
                uninstaller);
            journal.Commit();
            if (installationManifest is not null && installationDataRoot is not null)
            {
                Directory.CreateDirectory(installationDataRoot);
                File.WriteAllText(
                    Path.Combine(installationDataRoot, "installation.json"),
                    JsonSerializer.Serialize(
                        installationManifest,
                        new JsonSerializerOptions(JsonSerializerDefaults.Web)
                        {
                            WriteIndented = true
                        }));
            }

            progress.Report(new InstallProgress(98, "Final verification complete."));
            await log.WriteAsync(
                "Setup",
                $"Installation completed with {warnings.Count} warning(s).",
                cancellationToken);

            if (includesClient && request.LaunchAfterInstall)
            {
                try
                {
                    Process.Start(
                        new ProcessStartInfo
                        {
                            FileName = clientExecutable,
                            UseShellExecute = true
                        });
                    await log.WriteAsync(
                        "Launch dashboard",
                        $"Started \"{clientExecutable}\".",
                        cancellationToken);
                }
                catch (Exception exception)
                {
                    var warning = $"Dashboard launch warning: {exception.Message}";
                    warnings.Add(warning);
                    await log.WriteAsync(
                        "Launch dashboard",
                        warning,
                        cancellationToken);
                }
            }

            return new InstallResult(log.Path, warnings, serviceHealthVerified);
        }
        catch (Exception exception)
        {
            await log.WriteAsync(
                "Failure",
                exception.ToString(),
                CancellationToken.None);
            if (serviceInstaller is not null && originalService is not null)
            {
                try
                {
                    await serviceInstaller.PrepareForDeploymentAsync(
                        CancellationToken.None);
                }
                catch (Exception stopException)
                {
                    await log.WriteAsync(
                        "Rollback",
                        $"Service stop before file rollback warning: {stopException}",
                        CancellationToken.None);
                }
            }

            if (journal is not null)
            {
                try
                {
                    await journal.RollBackAsync(log, CancellationToken.None);
                }
                catch (Exception rollbackException)
                {
                    await log.WriteAsync(
                        "Rollback",
                        $"File rollback warning: {rollbackException}",
                        CancellationToken.None);
                }
            }

            if (serviceInstaller is not null && originalService is not null)
            {
                try
                {
                    if (originalService.Exists)
                    {
                        await serviceInstaller.RestoreAsync(
                            originalService,
                            CancellationToken.None);
                    }
                    else
                    {
                        await serviceInstaller.RemoveAsync(CancellationToken.None);
                    }
                }
                catch (Exception rollbackException)
                {
                    await log.WriteAsync(
                        "Rollback",
                        $"Service restoration warning: {rollbackException}",
                        CancellationToken.None);
                }
            }

            throw;
        }
        finally
        {
            healthProbe?.Dispose();
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, true);
            }
        }
    }

    public static async Task<string> UninstallAsync(
        string installRoot,
        CancellationToken cancellationToken = default)
    {
        using var log = new InstallerLog();
        LastLogPath = log.Path;
        installRoot = Path.GetFullPath(installRoot);
        var marker = Path.Combine(installRoot, MarkerName);
        if (!File.Exists(marker) ||
            !File.ReadAllText(marker).Equals(MarkerValue, StringComparison.Ordinal))
        {
            return "Uninstall stopped because the application ownership marker is missing.";
        }

        var runner = new InstallerProcessRunner(log);
        using var healthProbe = new HttpAgentHealthProbe();
        var serviceInstaller = new WindowsServiceInstaller(
            new ScServiceControlBackend(runner),
            healthProbe,
            log);
        await StopInstalledDashboardAsync(
            installRoot,
            log,
            cancellationToken);
        await serviceInstaller.RemoveAsync(cancellationToken);

        foreach (var name in new[]
                 {
                     "1Salem Server Manager - Agent HTTPS",
                     "1Salem Server Manager - Minecraft",
                     "1Salem Server Manager - Palworld"
                 })
        {
            await runner.RunAsync(
                $"Remove firewall rule: {name}",
                "netsh.exe",
                ["advfirewall", "firewall", "delete", "rule", $"name={name}"],
                cancellationToken);
        }

        RemoveShortcuts();
        using (var key = Registry.CurrentUser.CreateSubKey(
                   @"Software\Microsoft\Windows\CurrentVersion\Run",
                   true))
        {
            key.DeleteValue("1Salem Server Manager", false);
        }

        Registry.LocalMachine.DeleteSubKeyTree(UninstallKeyPath, false);
        foreach (var component in new[] { "Client", "Agent" })
        {
            var path = Path.Combine(installRoot, component);
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }

        await log.WriteAsync(
            "Uninstall",
            "Removed application binaries, service, firewall rules, shortcuts, " +
            "startup entry, and Apps & Features registration. Preserved ProgramData, " +
            "game servers, configurations, saves, and backups.",
            cancellationToken);
        return
            "Application components were removed. Agent data, game servers, " +
            $"configurations, saves, and backups were preserved.{Environment.NewLine}" +
            $"Log: {log.Path}";
    }

    private static async Task ExtractPayloadAsync(
        string destination,
        CancellationToken cancellationToken)
    {
        var sidecarPath = Path.Combine(AppContext.BaseDirectory, "Payload.zip");
        if (File.Exists(sidecarPath))
        {
            ZipFile.ExtractToDirectory(sidecarPath, destination);
            return;
        }

        await using var stream = typeof(InstallerEngine).Assembly
            .GetManifestResourceStream("1Salem.Payload.zip") ??
            throw new FileNotFoundException(
                "The embedded setup payload is missing. Download Setup.exe again.");
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, false);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!target.StartsWith(
                    Path.GetFullPath(destination) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "The setup payload contains a path outside its staging directory.");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var source = entry.Open();
            await using var output = new FileStream(
                target,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                true);
            await source.CopyToAsync(output, cancellationToken);
        }
    }

    private static async Task AddFirewallRuleAsync(
        IInstallerProcessRunner runner,
        string step,
        string name,
        string protocol,
        int port,
        string? executable,
        ICollection<string> warnings,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string>
        {
            "advfirewall",
            "firewall",
            "add",
            "rule",
            $"name={name}",
            "dir=in",
            "action=allow",
            $"protocol={protocol}",
            $"localport={port}",
            "profile=private",
            "enable=yes"
        };
        if (!string.IsNullOrWhiteSpace(executable))
        {
            arguments.Add($"program={executable}");
        }

        var result = await runner.RunAsync(
            step,
            "netsh.exe",
            arguments,
            cancellationToken);
        if (!result.Success)
        {
            warnings.Add(
                $"{step} failed with exit code {result.ExitCode}. " +
                "The application was installed; see the install log for details.");
        }
    }

    internal static void CreateShortcuts(
        string clientExecutable,
        string installRoot,
        bool desktop,
        bool startMenu)
    {
        if (desktop)
        {
            CreateShortcut(
                Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.DesktopDirectory),
                    "1Salem Server Manager.lnk"),
                clientExecutable,
                string.Empty,
                clientExecutable,
                stampAppUserModelId: true);
        }

        if (!startMenu)
        {
            return;
        }

        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            "1Salem Server Manager");
        Directory.CreateDirectory(folder);
        CreateShortcut(
            Path.Combine(folder, "1Salem Server Manager.lnk"),
            clientExecutable,
            string.Empty,
            clientExecutable,
            stampAppUserModelId: true);
        CreateShortcut(
            Path.Combine(folder, "1Salem Server Manager (Administrator).lnk"),
            clientExecutable,
            "--admin",
            clientExecutable,
            stampAppUserModelId: true);
        var uninstaller = Path.Combine(
            installRoot,
            "Uninstall 1Salem Server Manager.exe");
        CreateShortcut(
            Path.Combine(folder, "Uninstall 1Salem Server Manager.lnk"),
            uninstaller,
            "--uninstall",
            uninstaller);
    }

    internal static void CreateShortcut(
        string path,
        string target,
        string arguments,
        string iconPath,
        bool stampAppUserModelId = false)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ??
            throw new InvalidOperationException(
                "Windows shortcut service is unavailable.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic shortcut = shell.CreateShortcut(path);
        shortcut.TargetPath = target;
        shortcut.Arguments = arguments;
        shortcut.WorkingDirectory = Path.GetDirectoryName(target);
        shortcut.IconLocation = $"{iconPath},0";
        shortcut.Save();

        // Stamped only on shortcuts that launch the Client itself (never the uninstaller,
        // which is a different utility and should not visually merge with the app in the
        // taskbar/jump-list). See ProductIdentity.StampShortcutAppUserModelId for why this
        // matters: without it, a shortcut created by a normal install and pinned to the
        // taskbar -- the only supported pinning method -- would never carry the fixed Stable
        // AppUserModelID the running Client process sets on itself, so Windows would fail to
        // merge the pinned icon with the running app's taskbar button.
        if (stampAppUserModelId)
        {
            ProductIdentity.StampShortcutAppUserModelId(path, ProductIdentity.AppUserModelId);
        }
    }

    private static void SetStartup(string clientExecutable, bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Run",
            true);
        if (enabled)
        {
            key.SetValue(
                "1Salem Server Manager",
                $"\"{clientExecutable}\" --minimized");
        }
        else
        {
            key.DeleteValue("1Salem Server Manager", false);
        }
    }

    private static void RegisterUninstallEntry(
        string installRoot,
        string displayIcon,
        string uninstaller)
    {
        using var key = Registry.LocalMachine.CreateSubKey(
            UninstallKeyPath,
            true);
        key.SetValue("DisplayName", "1Salem Server Manager");
        key.SetValue("DisplayVersion", ProductVersion);
        key.SetValue("Publisher", "1Salem");
        key.SetValue("InstallLocation", installRoot);
        key.SetValue("DisplayIcon", $"{displayIcon},0");
        key.SetValue("UninstallString", $"\"{uninstaller}\" --uninstall");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 0, RegistryValueKind.DWord);
    }

    private static void RemoveShortcuts()
    {
        var paths = new[]
        {
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.DesktopDirectory),
                "1Salem Server Manager.lnk"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                "1Salem Server Manager")
        };
        if (File.Exists(paths[0]))
        {
            File.Delete(paths[0]);
        }

        if (Directory.Exists(paths[1]))
        {
            Directory.Delete(paths[1], true);
        }
    }

    private static async Task StopInstalledDashboardAsync(
        string installRoot,
        InstallerLog log,
        CancellationToken cancellationToken)
    {
        var expectedPath = Path.GetFullPath(
            Path.Combine(
                installRoot,
                "Client",
                "1Salem.ServerManager.exe"));
        foreach (var process in Process.GetProcessesByName(
                     "1Salem.ServerManager"))
        {
            using (process)
            {
                string? processPath;
                try
                {
                    processPath = process.MainModule?.FileName;
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException or
                        System.ComponentModel.Win32Exception)
                {
                    await log.WriteAsync(
                        "Close dashboard",
                        $"Could not inspect dashboard process {process.Id}: " +
                        exception.Message,
                        cancellationToken);
                    continue;
                }

                var normalizedPath = string.IsNullOrWhiteSpace(processPath)
                    ? null
                    : Path.GetFullPath(processPath);
                var normalizedRoot = Path.GetFullPath(installRoot).TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar) +
                    Path.DirectorySeparatorChar;
                if (normalizedPath is null ||
                    (!normalizedPath.Equals(expectedPath, StringComparison.OrdinalIgnoreCase) &&
                     !normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                await log.WriteAsync(
                    "Close dashboard",
                    $"Stopping installed dashboard process {process.Id} before " +
                    "updating application binaries.",
                    cancellationToken);
                process.CloseMainWindow();
                var exited = await Task.WhenAny(
                    process.WaitForExitAsync(cancellationToken),
                    Task.Delay(TimeSpan.FromSeconds(2), cancellationToken));
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(cancellationToken);
                    await log.WriteAsync(
                        "Close dashboard",
                        $"Dashboard process {process.Id} required a bounded forced " +
                        "exit because closing its window minimizes it to the tray.",
                        cancellationToken);
                }
                else
                {
                    await exited;
                }
            }
        }
    }
}
