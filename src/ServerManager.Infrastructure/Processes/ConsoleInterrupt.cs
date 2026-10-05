using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace ServerManager.Infrastructure.Processes;

/// <summary>
/// Asks a process to stop the way pressing Ctrl+C in its console window would. A Minecraft server
/// re-adopted after the Agent restarted has lost the console pipe 1Salem typed "stop" into, but it
/// still runs in its own hidden console; Ctrl+C there makes Java run Minecraft's shutdown hook, which
/// saves the worlds and exits. The event is raised from a short-lived helper (the Agent executable
/// started with <see cref="HelperArgument"/>), so the Agent never joins another console or changes
/// its own Ctrl+C handling. Nothing here kills a process.
/// </summary>
public static class ConsoleInterrupt
{
    public const string HelperArgument = "--console-interrupt";

    private const uint CtrlCEvent = 0;
    private static readonly TimeSpan HelperTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Runs inside the helper process: joins the target's console and raises Ctrl+C there.</summary>
    public static int RunHelper(string processIdText)
    {
        if (!int.TryParse(processIdText, NumberStyles.None, CultureInfo.InvariantCulture, out var processId) || processId <= 0)
        {
            return 2;
        }

        FreeConsole();
        if (!AttachConsole((uint)processId))
        {
            return 3;
        }

        // The helper shares the console now, so it ignores the event it raises.
        SetConsoleCtrlHandler(IntPtr.Zero, true);
        return GenerateConsoleCtrlEvent(CtrlCEvent, 0) ? 0 : 4;
    }

    /// <summary>Starts the helper for <paramref name="processId"/>; true when it raised Ctrl+C in that console.</summary>
    public static async Task<bool> RequestAsync(
        int processId,
        string helperFileName,
        IReadOnlyList<string> helperLeadingArguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = helperFileName,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in helperLeadingArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.ArgumentList.Add(HelperArgument);
        startInfo.ArgumentList.Add(processId.ToString(CultureInfo.InvariantCulture));
        using var helper = Process.Start(startInfo) ?? throw new InvalidOperationException("The stop helper did not start.");
        try
        {
            await helper.WaitForExitAsync(cancellationToken).WaitAsync(HelperTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            // Only the helper itself, by its own handle; never the server.
            helper.Kill();
            return false;
        }

        return helper.ExitCode == 0;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint processGroupId);
}
