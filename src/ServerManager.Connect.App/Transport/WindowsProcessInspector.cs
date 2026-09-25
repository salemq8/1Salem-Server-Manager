using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace ServerManager.Connect.App.Transport;

/// <summary>
/// The Win32 side of <see cref="TransportServerVerifier"/>, and the only place in the app that
/// calls into Windows for it. A process is opened with <c>PROCESS_QUERY_LIMITED_INFORMATION</c>
/// and <c>SYNCHRONIZE</c> only: enough for its image path, its token's integrity level and
/// whether it has exited, and nothing that could change it. Whatever cannot be read comes back as
/// null, which the verifier refuses.
/// </summary>
internal sealed class WindowsProcessInspector : IProcessInspector
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint Synchronize = 0x0010_0000;
    private const uint WaitObject0 = 0;
    private const uint TokenQuery = 0x0008;
    private const int TokenIntegrityLevelClass = 25;
    private const int ErrorInsufficientBuffer = 122;

    // Mandatory label SIDs are S-1-16-<level>.
    private const string MandatoryLabelPrefix = "S-1-16-";

    public int ServerProcessId(SafePipeHandle pipe) =>
        GetNamedPipeServerProcessId(pipe, out var processId)
            ? checked((int)processId)
            : throw new Win32Exception(Marshal.GetLastWin32Error());

    public IOpenedProcess? Open(int processId)
    {
        var process = OpenProcess(ProcessQueryLimitedInformation | Synchronize, false, checked((uint)processId));
        if (process.IsInvalid)
        {
            process.Dispose();
            return null;
        }

        return new OpenedProcess(process);
    }

    private static string? ImagePath(SafeProcessHandle process)
    {
        var buffer = new char[short.MaxValue];
        var length = (uint)buffer.Length;
        return QueryFullProcessImageName(process, 0, buffer, ref length) ? new string(buffer, 0, (int)length) : null;
    }

    private static int? IntegrityLevel(SafeProcessHandle process)
    {
        var opened = OpenProcessToken(process, TokenQuery, out var token);
        using (token)
        {
            if (!opened ||
                GetTokenInformation(token, TokenIntegrityLevelClass, IntPtr.Zero, 0, out var needed) ||
                Marshal.GetLastWin32Error() != ErrorInsufficientBuffer)
            {
                return null;
            }

            var buffer = Marshal.AllocHGlobal((int)needed);
            try
            {
                if (!GetTokenInformation(token, TokenIntegrityLevelClass, buffer, needed, out _))
                {
                    return null;
                }

                // TOKEN_MANDATORY_LABEL starts with SID_AND_ATTRIBUTES, which starts with the SID pointer.
                var label = new SecurityIdentifier(Marshal.ReadIntPtr(buffer)).Value;
                return label.StartsWith(MandatoryLabelPrefix, StringComparison.Ordinal) &&
                       int.TryParse(label.AsSpan(MandatoryLabelPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var level)
                    ? level
                    : null;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, [Out] char[] exeName, ref uint size);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint desiredAccess, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass, IntPtr information, uint length, out uint returnLength);

    private sealed class OpenedProcess(SafeProcessHandle process) : IOpenedProcess
    {
        // A zero timeout only looks. A failed look (WAIT_FAILED) is "not known to have exited".
        public bool HasExited => WaitForSingleObject(process, 0) == WaitObject0;

        public InspectedProcess? Inspect() =>
            ImagePath(process) is { } imagePath && IntegrityLevel(process) is { } integrityLevel
                ? new InspectedProcess(imagePath, integrityLevel)
                : null;

        public void Dispose() => process.Dispose();
    }
}
