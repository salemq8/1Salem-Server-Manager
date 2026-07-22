using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using ServerManager.Core;

namespace ServerManager.Infrastructure.Games.Minecraft;

public sealed partial class JavaRuntimeLocator : IJavaRuntimeLocator
{
    public async Task<JavaRuntimeInfo?> FindAsync(
        int minimumMajorVersion,
        CancellationToken cancellationToken = default)
    {
        foreach (var candidate in EnumerateCandidates().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var runtime = await InspectCandidateAsync(candidate, cancellationToken);
            if (runtime is not null && runtime.MajorVersion >= minimumMajorVersion)
            {
                return runtime;
            }
        }

        return null;
    }

    public Task<JavaRuntimeInfo?> InspectAsync(
        string executablePath,
        CancellationToken cancellationToken = default) =>
        InspectCandidateAsync(executablePath, cancellationToken);

    private static IEnumerable<string> EnumerateCandidates()
    {
        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome))
        {
            yield return Path.Combine(javaHome, "bin", "java.exe");
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(path))
        {
            foreach (var directory in path.Split(
                         Path.PathSeparator,
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                yield return Path.Combine(directory.Trim('"'), "java.exe");
            }
        }

        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
                 }.Where(root => !string.IsNullOrWhiteSpace(root)))
        {
            foreach (var vendorRoot in new[]
                     {
                         Path.Combine(root, "Microsoft"),
                         Path.Combine(root, "Eclipse Adoptium"),
                         Path.Combine(root, "Java")
                     })
            {
                if (!Directory.Exists(vendorRoot))
                {
                    continue;
                }

                foreach (var directory in Directory.EnumerateDirectories(vendorRoot, "jdk-*"))
                {
                    yield return Path.Combine(directory, "bin", "java.exe");
                }
            }
        }

        if (!OperatingSystem.IsWindows())
        {
            yield break;
        }

        foreach (var registryPath in new[]
                 {
                     @"SOFTWARE\Microsoft\JDK",
                     @"SOFTWARE\JavaSoft\JDK",
                     @"SOFTWARE\Eclipse Adoptium\JDK"
                 })
        {
            using var key = Registry.LocalMachine.OpenSubKey(registryPath);
            if (key is null)
            {
                continue;
            }

            foreach (var version in key.GetSubKeyNames().OrderByDescending(value => value))
            {
                using var versionKey = key.OpenSubKey(version);
                var home = versionKey?.GetValue("Path") as string ??
                           versionKey?.GetValue("JavaHome") as string;
                if (!string.IsNullOrWhiteSpace(home))
                {
                    yield return Path.Combine(home, "bin", "java.exe");
                }
            }
        }
    }

    private static async Task<JavaRuntimeInfo?> InspectCandidateAsync(
        string executable,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(executable))
        {
            return null;
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = "-version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        if (!process.Start())
        {
            return null;
        }

        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.Kill(true);
            return null;
        }

        var versionText = $"{await standardError} {await standardOutput}".Trim();
        var major = ParseMajorVersion(versionText);
        if (major is null)
        {
            return null;
        }

        return new JavaRuntimeInfo(
            Path.GetFullPath(executable),
            major.Value,
            versionText);
    }

    public static int? ParseMajorVersion(string versionText)
    {
        if (string.IsNullOrWhiteSpace(versionText))
        {
            return null;
        }

        var match = JavaVersionRegex().Match(versionText);
        if (!match.Success)
        {
            return null;
        }

        var first = int.Parse(
            match.Groups["first"].Value,
            System.Globalization.CultureInfo.InvariantCulture);
        return first == 1 && match.Groups["second"].Success
            ? int.Parse(
                match.Groups["second"].Value,
                System.Globalization.CultureInfo.InvariantCulture)
            : first;
    }

    [GeneratedRegex("\"(?<first>\\d+)(?:\\.(?<second>\\d+))?")]
    private static partial Regex JavaVersionRegex();
}
