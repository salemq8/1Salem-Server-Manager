using System.ComponentModel;
using System.Runtime.InteropServices;
using ServerManager.Core;

namespace ServerManager.Infrastructure.Processes;

public sealed class WindowsSystemResourceReader : ISystemResourceReader
{
    private readonly object _sync = new();
    private ulong _lastIdle;
    private ulong _lastKernel;
    private ulong _lastUser;

    public SystemResourceSnapshot Capture(ResourcePolicy activePolicy)
    {
        ArgumentNullException.ThrowIfNull(activePolicy);
        var (total, available) = ReadMemory();
        var warnings = new List<string>();
        if (available < activePolicy.WindowsReserveBytes)
        {
            warnings.Add(
                "Available memory is below the configured Windows safety reserve.");
        }

        return new SystemResourceSnapshot(
            DateTimeOffset.UtcNow,
            total,
            available,
            ReadCpuPercent(),
            ReadSystemDriveFreeBytes(),
            activePolicy,
            warnings);
    }

    private static (long Total, long Available) ReadMemory()
    {
        if (!OperatingSystem.IsWindows())
        {
            var info = GC.GetGCMemoryInfo();
            var total = Math.Max(
                ResourcePolicyCatalog.Gibibyte * 2,
                info.TotalAvailableMemoryBytes);
            return (total, Math.Max(0, total - GC.GetTotalMemory(false)));
        }

        var status = new MemoryStatusEx();
        if (!GlobalMemoryStatusEx(status))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return (checked((long)status.TotalPhysical), checked((long)status.AvailablePhysical));
    }

    private double ReadCpuPercent()
    {
        if (!OperatingSystem.IsWindows() ||
            !GetSystemTimes(out var idle, out var kernel, out var user))
        {
            return 0;
        }

        var idleValue = idle.ToUInt64();
        var kernelValue = kernel.ToUInt64();
        var userValue = user.ToUInt64();
        lock (_sync)
        {
            var idleDelta = idleValue - _lastIdle;
            var kernelDelta = kernelValue - _lastKernel;
            var userDelta = userValue - _lastUser;
            _lastIdle = idleValue;
            _lastKernel = kernelValue;
            _lastUser = userValue;

            var totalDelta = kernelDelta + userDelta;
            if (totalDelta == 0 || _lastKernel == kernelDelta)
            {
                return 0;
            }

            return Math.Clamp(
                (totalDelta - idleDelta) * 100d / totalDelta,
                0,
                100);
        }
    }

    private static long ReadSystemDriveFreeBytes()
    {
        try
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory);
            return string.IsNullOrWhiteSpace(root)
                ? 0
                : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx buffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(
        out FileTime idleTime,
        out FileTime kernelTime,
        out FileTime userTime);

    [StructLayout(LayoutKind.Sequential)]
    private sealed class MemoryStatusEx
    {
        public uint Length = (uint)Marshal.SizeOf<MemoryStatusEx>();
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct FileTime
    {
        private readonly uint _low;
        private readonly uint _high;

        public ulong ToUInt64() => ((ulong)_high << 32) | _low;
    }
}
