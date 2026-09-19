using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ServerManager.Infrastructure.Windows;

public enum WindowsServiceState
{
    NotInstalled,
    Stopped,
    StartPending,
    StopPending,
    Running,
    ContinuePending,
    PausePending,
    Paused,
    Unknown
}

/// <summary>
/// Reports and controls a Windows service by its real Service Control Manager state.
/// <para>
/// A successful <c>sc.exe stop</c> only means the stop control was accepted -- the service is
/// usually still STOP_PENDING when the command returns, and its process still holds handles to
/// its own installation directory. Anything that replaces those files has to wait for the
/// service to actually reach Stopped, which requires querying real state rather than trusting a
/// process exit code or parsing localized console output.
/// </para>
/// </summary>
public interface IWindowsServiceControl
{
    WindowsServiceState GetState(string serviceName);

    void RequestStop(string serviceName);

    void RequestStart(string serviceName);
}

public sealed class WindowsServiceControl : IWindowsServiceControl
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceStart = 0x0010;
    private const uint ServiceStop = 0x0020;
    private const uint ServiceControlStop = 0x0001;
    private const int ScStatusProcessInfo = 0;
    private const int ErrorServiceDoesNotExist = 1060;
    private const int ErrorServiceNotActive = 1062;
    private const int ErrorServiceCannotAcceptControl = 1061;
    private const int ErrorServiceAlreadyRunning = 1056;

    public WindowsServiceState GetState(string serviceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        if (!OperatingSystem.IsWindows())
        {
            return WindowsServiceState.NotInstalled;
        }

        using var manager = OpenManager();
        if (manager is null)
        {
            return WindowsServiceState.Unknown;
        }

        using var service = OpenService(manager, serviceName, ServiceQueryStatus);
        if (service is null)
        {
            return Marshal.GetLastWin32Error() == ErrorServiceDoesNotExist
                ? WindowsServiceState.NotInstalled
                : WindowsServiceState.Unknown;
        }

        var size = Marshal.SizeOf<ServiceStatusProcess>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!QueryServiceStatusEx(
                    service.Value,
                    ScStatusProcessInfo,
                    buffer,
                    (uint)size,
                    out _))
            {
                return WindowsServiceState.Unknown;
            }

            var status = Marshal.PtrToStructure<ServiceStatusProcess>(buffer);
            return status.CurrentState switch
            {
                1 => WindowsServiceState.Stopped,
                2 => WindowsServiceState.StartPending,
                3 => WindowsServiceState.StopPending,
                4 => WindowsServiceState.Running,
                5 => WindowsServiceState.ContinuePending,
                6 => WindowsServiceState.PausePending,
                7 => WindowsServiceState.Paused,
                _ => WindowsServiceState.Unknown
            };
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void RequestStop(string serviceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var manager = OpenManager() ??
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Unable to open the Windows service control manager.");
        using var service = OpenService(manager, serviceName, ServiceStop | ServiceQueryStatus) ??
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Unable to open the {serviceName} service to stop it.");

        var status = default(ServiceStatus);
        if (ControlService(service.Value, ServiceControlStop, ref status))
        {
            return;
        }

        var error = Marshal.GetLastWin32Error();

        // Already stopped, or already stopping and therefore not accepting another stop --
        // both mean the caller's intent is satisfied; waiting for Stopped is the caller's job.
        if (error is ErrorServiceNotActive or ErrorServiceCannotAcceptControl)
        {
            return;
        }

        throw new Win32Exception(error, $"Unable to stop the {serviceName} service.");
    }

    public void RequestStart(string serviceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var manager = OpenManager() ??
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Unable to open the Windows service control manager.");
        using var service = OpenService(manager, serviceName, ServiceStart | ServiceQueryStatus) ??
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Unable to open the {serviceName} service to start it.");

        if (StartServiceW(service.Value, 0, nint.Zero))
        {
            return;
        }

        var error = Marshal.GetLastWin32Error();

        // The service came up on its own between the state query and this call. That is the
        // requested end state, not a failure -- this is the condition that previously surfaced
        // as a hard "code 1056" update failure.
        if (error == ErrorServiceAlreadyRunning)
        {
            return;
        }

        throw new Win32Exception(error, $"Unable to start the {serviceName} service.");
    }

    private static ServiceHandle? OpenManager()
    {
        var handle = OpenSCManagerW(null, null, ScManagerConnect);
        return handle == nint.Zero ? null : new ServiceHandle(handle);
    }

    private static ServiceHandle? OpenService(
        ServiceHandle manager,
        string serviceName,
        uint access)
    {
        var handle = OpenServiceW(manager.Value, serviceName, access);
        return handle == nint.Zero ? null : new ServiceHandle(handle);
    }

    private sealed class ServiceHandle(nint value) : IDisposable
    {
        public nint Value { get; private set; } = value;

        public void Dispose()
        {
            if (Value != nint.Zero)
            {
                CloseServiceHandle(Value);
                Value = nint.Zero;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint OpenSCManagerW(
        string? machineName,
        string? databaseName,
        uint desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint OpenServiceW(
        nint manager,
        string serviceName,
        uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(
        nint service,
        int infoLevel,
        nint buffer,
        uint bufferSize,
        out uint bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ControlService(
        nint service,
        uint control,
        ref ServiceStatus status);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartServiceW(
        nint service,
        uint argumentCount,
        nint argumentVectors);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(nint handle);
}
