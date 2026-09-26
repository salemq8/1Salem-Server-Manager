using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TsnetSmoke;

/// <summary>
/// A Windows job object that ends every process in it once its handle is closed
/// (<c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>). Only the driver holds the handle, and Windows closes
/// it when the driver ends for any reason, so the host transport and the probe cannot outlive it
/// as live tailnet nodes. The friend transport is already in the app's own job
/// (<c>TransportProcess</c>). Copied from the app's internal <c>KillOnCloseJob</c>, which the
/// harness cannot reference.
/// </summary>
internal sealed class SmokeJob : IDisposable
{
    private const int JobObjectExtendedLimitInformationClass = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private readonly SafeJobHandle _job;

    private SmokeJob(SafeJobHandle job) => _job = job;

    /// <summary>Throws <see cref="Win32Exception"/> when Windows refuses the job or its limit.</summary>
    public static SmokeJob Create()
    {
        // No security attributes, so the handle is not inheritable: the children are started with
        // inherited handles (for their redirected output), and a copy of this handle in a child
        // would keep the job, and the child, alive after the driver is gone.
        var job = CreateJobObject(IntPtr.Zero, IntPtr.Zero);
        if (job.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            job.Dispose();
            throw new Win32Exception(error);
        }

        var limits = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = JobObjectLimitKillOnJobClose },
        };
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformationClass, ref limits, (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
        {
            var error = Marshal.GetLastWin32Error();
            job.Dispose();
            throw new Win32Exception(error);
        }

        return new SmokeJob(job);
    }

    /// <summary>
    /// Puts <paramref name="process"/> in the job. A process that cannot be tied to the driver is
    /// killed at once and the start fails: unlike the app, the harness must not leave a node
    /// running when it is interrupted.
    /// </summary>
    public void Assign(Process process)
    {
        if (!AssignProcessToJobObject(_job, process.SafeHandle))
        {
            var error = new Win32Exception(Marshal.GetLastWin32Error());
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException($"a started process could not be tied to the driver, so it was stopped: {error.Message}");
        }
    }

    /// <summary>Closes the handle, which ends every process still in the job.</summary>
    public void Dispose() => _job.Dispose();

    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true)]
    private static extern SafeJobHandle CreateJobObject(IntPtr jobAttributes, IntPtr name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeJobHandle job, int informationClass, ref JobObjectExtendedLimitInformation information, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeJobHandle job, SafeProcessHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    private sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeJobHandle()
            : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    // JOBOBJECT_BASIC_LIMIT_INFORMATION and JOBOBJECT_EXTENDED_LIMIT_INFORMATION, field for field:
    // only the kill-on-close flag is set, the rest keep Windows' "no limit" zeroes.
    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
