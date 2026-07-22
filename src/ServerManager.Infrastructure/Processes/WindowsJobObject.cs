using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ServerManager.Infrastructure.Processes;

internal sealed class WindowsJobObject : IDisposable
{
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const uint JobObjectLimitJobMemory = 0x00000200;
    private readonly SafeFileHandle? _handle;

    public WindowsJobObject()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        _handle = CreateJobObject(nint.Zero, null);
        if (_handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to create a Windows Job Object.");
        }

        ApplyLimits(null);
    }

    public void SetMemoryLimit(long? bytes)
    {
        if (bytes is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bytes),
                "A Job Object memory limit must be positive.");
        }

        ApplyLimits(bytes);
    }

    private void ApplyLimits(long? memoryLimitBytes)
    {
        if (_handle is null)
        {
            return;
        }

        var information = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = JobObjectLimitKillOnJobClose |
                             (memoryLimitBytes is null
                                 ? 0
                                 : JobObjectLimitJobMemory)
            },
            JobMemoryLimit = memoryLimitBytes is null
                ? 0
                : checked((nuint)memoryLimitBytes.Value)
        };
        var length = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        var pointer = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(information, pointer, false);
            if (!SetInformationJobObject(
                    _handle,
                    JobObjectInformationClass.ExtendedLimitInformation,
                    pointer,
                    (uint)length))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Unable to configure the Windows Job Object limits.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    public void Assign(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (_handle is null)
        {
            return;
        }

        if (!AssignProcessToJobObject(_handle, process.Handle))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Unable to assign process {process.Id} to its Windows Job Object.");
        }
    }

    public IReadOnlyList<int> GetProcessIds()
    {
        if (_handle is null)
        {
            return [];
        }

        var capacity = 16;
        while (capacity <= 16_384)
        {
            var headerSize = sizeof(uint) * 2;
            var length = checked(headerSize + (capacity * IntPtr.Size));
            var pointer = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.WriteInt32(pointer, 0, 0);
                Marshal.WriteInt32(pointer, sizeof(uint), 0);
                if (!QueryInformationJobObject(
                        _handle,
                        JobObjectInformationClass.BasicProcessIdList,
                        pointer,
                        (uint)length,
                        out _))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == ErrorMoreData)
                    {
                        capacity = Math.Max(
                            capacity * 2,
                            Marshal.ReadInt32(pointer, 0));
                        continue;
                    }

                    throw new Win32Exception(
                        error,
                        "Unable to query processes in the Windows Job Object.");
                }

                var activeCount = Math.Min(
                    Marshal.ReadInt32(pointer, sizeof(uint)),
                    capacity);
                var processIds = new List<int>(activeCount);
                for (var index = 0; index < activeCount; index++)
                {
                    var value = Marshal.ReadIntPtr(
                        pointer,
                        headerSize + (index * IntPtr.Size));
                    var processId = value.ToInt64();
                    if (processId is > 0 and <= int.MaxValue)
                    {
                        processIds.Add((int)processId);
                    }
                }

                return processIds;
            }
            finally
            {
                Marshal.FreeHGlobal(pointer);
            }
        }

        throw new InvalidOperationException(
            "The managed process tree is too large to inspect safely.");
    }

    public void Dispose() => _handle?.Dispose();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(nint jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeFileHandle job,
        JobObjectInformationClass informationClass,
        nint information,
        uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, nint process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(
        SafeFileHandle job,
        JobObjectInformationClass informationClass,
        nint information,
        uint informationLength,
        out uint returnLength);

    private const int ErrorMoreData = 234;

    private enum JobObjectInformationClass
    {
        BasicProcessIdList = 3,
        ExtendedLimitInformation = 9
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
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
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }
}
