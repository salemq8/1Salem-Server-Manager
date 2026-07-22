using System.Diagnostics;
using System.Text;
using ServerManager.Core;

namespace ServerManager.Infrastructure.Windows;

internal static class WindowsCommandRunner
{
    public static async Task<OperationResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return OperationResult.Fail(
                "WindowsRequired",
                "This operation is available only on Windows.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = JoinOutput(await standardOutput, await standardError);
        return process.ExitCode == 0
            ? new OperationResult(true, Message: output)
            : OperationResult.Fail(
                "WindowsCommandFailed",
                string.IsNullOrWhiteSpace(output)
                    ? $"{Path.GetFileName(executable)} exited with code {process.ExitCode}."
                    : output);
    }

    private static string JoinOutput(string standardOutput, string standardError)
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(standardOutput))
        {
            builder.Append(standardOutput.Trim());
        }

        if (!string.IsNullOrWhiteSpace(standardError))
        {
            if (builder.Length > 0)
            {
                builder.AppendLine();
            }

            builder.Append(standardError.Trim());
        }

        return builder.ToString();
    }
}
