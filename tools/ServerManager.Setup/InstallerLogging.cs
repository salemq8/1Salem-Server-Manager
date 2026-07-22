using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Principal;
using System.Text;

namespace ServerManager.Setup;

public sealed record ProcessExecutionResult(
    string Step,
    string Executable,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    int ExitCode,
    string StandardOutput,
    string StandardError,
    TimeSpan Duration)
{
    public bool Success => ExitCode == 0;

    public string CombinedOutput =>
        string.Join(
            Environment.NewLine,
            new[] { StandardOutput.Trim(), StandardError.Trim() }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
}

public interface IInstallerProcessRunner
{
    Task<ProcessExecutionResult> RunAsync(
        string step,
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken);
}

public sealed class InstallerFailureException : Exception
{
    public InstallerFailureException(
        string step,
        string message,
        string details,
        int? exitCode = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Step = step;
        Details = details;
        ExitCode = exitCode;
    }

    public string Step { get; }

    public string Details { get; }

    public int? ExitCode { get; }

    public static InstallerFailureException FromProcess(ProcessExecutionResult result)
    {
        var output = result.CombinedOutput;
        var concise = string.IsNullOrWhiteSpace(output)
            ? $"{Path.GetFileName(result.Executable)} exited with code {result.ExitCode}."
            : FirstLine(output);
        return new InstallerFailureException(
            result.Step,
            $"{result.Step} failed (exit code {result.ExitCode}): {concise}",
            FormatDetails(result),
            result.ExitCode);
    }

    private static string FirstLine(string value)
    {
        var line = value.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        if (string.IsNullOrEmpty(line))
        {
            return "No error details were returned.";
        }

        return line.Length <= 240 ? line : $"{line[..237]}...";
    }

    private static string FormatDetails(ProcessExecutionResult result) =>
        $"""
        Step: {result.Step}
        Executable: {result.Executable}
        Arguments: {string.Join(" | ", result.Arguments)}
        Working directory: {result.WorkingDirectory}
        Exit code: {result.ExitCode}
        Duration: {result.Duration}

        stdout:
        {result.StandardOutput}

        stderr:
        {result.StandardError}
        """;
}

public sealed class InstallerLog : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public InstallerLog(string? logRootOverride = null)
    {
        var logRoot = logRootOverride;
        if (string.IsNullOrWhiteSpace(logRoot))
        {
            var localApplicationData =
                Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if (string.IsNullOrWhiteSpace(localApplicationData))
            {
                localApplicationData = Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData);
            }

            logRoot = System.IO.Path.Combine(
                localApplicationData,
                "1SalemServerManager",
                "Installer",
                "Logs");
        }

        Directory.CreateDirectory(logRoot);
        Path = System.IO.Path.Combine(
            logRoot,
            $"setup-{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}-" +
            $"{Guid.NewGuid():N}.log");
        _writer = new StreamWriter(
            new FileStream(
                Path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.ReadWrite,
                4096,
                FileOptions.Asynchronous),
            new UTF8Encoding(false))
        {
            AutoFlush = true
        };
    }

    public string Path { get; }

    public async Task WriteAsync(
        string step,
        string message,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await _writer.WriteLineAsync(
                $"[{DateTimeOffset.Now:O}] [{step}] {message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _writer.Dispose();
        _gate.Dispose();
    }
}

public sealed class InstallerProcessRunner(InstallerLog log) : IInstallerProcessRunner
{
    public async Task<ProcessExecutionResult> RunAsync(
        string step,
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(step);
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);

        var resolvedExecutable = ResolveSystemExecutable(executable);
        var workingDirectory = AppContext.BaseDirectory;
        var sanitizedArguments = Sanitize(arguments);
        await log.WriteAsync(
            step,
            $"Starting external operation. executable=\"{resolvedExecutable}\"; " +
            $"arguments=[{string.Join(", ", sanitizedArguments.Select(Quote))}]; " +
            $"workingDirectory=\"{workingDirectory}\"; elevated={InstallerSecurity.IsElevated()}",
            cancellationToken);

        var startInfo = new ProcessStartInfo
        {
            FileName = resolvedExecutable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var process = Process.Start(startInfo) ??
                throw new InvalidOperationException(
                    $"Could not start {Path.GetFileName(resolvedExecutable)}.");
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var result = new ProcessExecutionResult(
                step,
                resolvedExecutable,
                sanitizedArguments,
                workingDirectory,
                process.ExitCode,
                await outputTask,
                await errorTask,
                stopwatch.Elapsed);
            await log.WriteAsync(
                step,
                $"Completed external operation. exitCode={result.ExitCode}; " +
                $"duration={result.Duration}{Environment.NewLine}" +
                $"stdout:{Environment.NewLine}{result.StandardOutput.Trim()}" +
                $"{Environment.NewLine}stderr:{Environment.NewLine}{result.StandardError.Trim()}",
                cancellationToken);
            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await log.WriteAsync(
                step,
                $"External operation could not run after {stopwatch.Elapsed}: {exception}",
                cancellationToken);
            throw;
        }
    }

    private static string ResolveSystemExecutable(string executable)
    {
        if (Path.IsPathFullyQualified(executable))
        {
            return executable;
        }

        var systemCandidate = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            executable);
        return File.Exists(systemCandidate) ? systemCandidate : executable;
    }

    private static IReadOnlyList<string> Sanitize(IReadOnlyList<string> arguments)
    {
        var sanitized = new string[arguments.Count];
        var redactNext = false;
        for (var index = 0; index < arguments.Count; index++)
        {
            var value = arguments[index];
            if (redactNext)
            {
                sanitized[index] = "<redacted>";
                redactNext = false;
                continue;
            }

            if (value.Equals("--password", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("--token", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("--secret", StringComparison.OrdinalIgnoreCase))
            {
                sanitized[index] = value;
                redactNext = true;
                continue;
            }

            var separator = value.IndexOf('=');
            if (separator > 0 &&
                value[..separator].Contains(
                    "password",
                    StringComparison.OrdinalIgnoreCase))
            {
                sanitized[index] = $"{value[..(separator + 1)]}<redacted>";
                continue;
            }

            sanitized[index] = value;
        }

        return sanitized;
    }

    private static string Quote(string value) =>
        value.Any(char.IsWhiteSpace) ? $"\"{value}\"" : value;
}

public static class InstallerSecurity
{
    public static bool IsElevated()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
