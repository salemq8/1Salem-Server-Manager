using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ServerManager.Infrastructure.Processes;

/// <summary>
/// Discovers a process's live descendants through OS-level parent-child relationships,
/// independent of Windows Job Object membership. A Job Object only reports processes that
/// were actually assigned to it; a child spawned before its parent ever joined a Job -- the
/// normal case when re-adopting an already-running server after an Agent restart -- is
/// invisible to a Job Object query even though it is a real, live descendant. This walks the
/// system process table instead, so discovery is correct whether the root process was just
/// started by this Agent instance or already existed when it started.
/// </summary>
public interface IProcessTreeDiscovery
{
    /// <summary>
    /// Returns <paramref name="rootProcessId"/> plus every process transitively descended
    /// from it that is still alive, resolved from one consistent snapshot of the system
    /// process table. A candidate child is discarded rather than trusted if it appears to
    /// predate the parent it claims -- a defensive check against a stale parent-process-id
    /// field left behind by Windows recycling the real parent's PID.
    /// </summary>
    IReadOnlyList<int> DescendantsOf(int rootProcessId);
}

public sealed class WindowsProcessTreeDiscovery : IProcessTreeDiscovery
{
    private const uint SnapshotFlagsProcess = 0x00000002;
    private static readonly nint InvalidHandleValue = new(-1);

    public IReadOnlyList<int> DescendantsOf(int rootProcessId)
    {
        if (!OperatingSystem.IsWindows())
        {
            return [rootProcessId];
        }

        var entries = CaptureSnapshot();
        if (entries.Count == 0)
        {
            return [rootProcessId];
        }

        var startTimeCache = new Dictionary<int, DateTime?>();
        return BuildDescendantList(
            rootProcessId,
            entries,
            processId => ResolveStartTime(processId, startTimeCache));
    }

    /// <summary>
    /// The pure tree walk, separated from how the process table and start times are read so
    /// it can be exercised directly against constructed inputs -- including the PID-reuse
    /// case, which cannot be forced against a live system.
    /// </summary>
    internal static IReadOnlyList<int> BuildDescendantList(
        int rootProcessId,
        IReadOnlyList<ProcessEntry> entries,
        Func<int, DateTime?> resolveStartTime)
    {
        var childrenByParent = new Dictionary<int, List<int>>();
        foreach (var entry in entries)
        {
            if (entry.ProcessId == entry.ParentProcessId)
            {
                continue;
            }

            if (!childrenByParent.TryGetValue(entry.ParentProcessId, out var list))
            {
                list = [];
                childrenByParent[entry.ParentProcessId] = list;
            }

            list.Add(entry.ProcessId);
        }

        var result = new List<int> { rootProcessId };
        var visited = new HashSet<int> { rootProcessId };
        var queue = new Queue<int>();
        queue.Enqueue(rootProcessId);

        while (queue.Count > 0)
        {
            var parentId = queue.Dequeue();
            if (!childrenByParent.TryGetValue(parentId, out var children))
            {
                continue;
            }

            var parentStart = resolveStartTime(parentId);
            foreach (var childId in children)
            {
                if (!visited.Add(childId))
                {
                    continue;
                }

                var childStart = resolveStartTime(childId);
                if (parentStart is { } parent && childStart is { } child && child < parent)
                {
                    continue;
                }

                result.Add(childId);
                queue.Enqueue(childId);
            }
        }

        return result;
    }

    private static DateTime? ResolveStartTime(int processId, Dictionary<int, DateTime?> cache)
    {
        if (cache.TryGetValue(processId, out var cached))
        {
            return cached;
        }

        DateTime? value = null;
        try
        {
            using var process = Process.GetProcessById(processId);
            value = process.StartTime;
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            InvalidOperationException or
            Win32Exception or
            NotSupportedException)
        {
        }

        cache[processId] = value;
        return value;
    }

    private static List<ProcessEntry> CaptureSnapshot()
    {
        var handle = CreateToolhelp32Snapshot(SnapshotFlagsProcess, 0);
        if (handle == InvalidHandleValue)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Unable to snapshot the system process table.");
        }

        try
        {
            var entries = new List<ProcessEntry>();
            var entry = default(ProcessEntry32);
            entry.Size = (uint)Marshal.SizeOf<ProcessEntry32>();
            if (!Process32FirstW(handle, ref entry))
            {
                return entries;
            }

            do
            {
                entries.Add(new ProcessEntry(
                    (int)entry.ProcessId,
                    (int)entry.ParentProcessId));
            }
            while (Process32NextW(handle, ref entry));

            return entries;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    internal readonly record struct ProcessEntry(int ProcessId, int ParentProcessId);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public nint DefaultHeapId;
        public uint ModuleId;
        public uint ThreadCount;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(nint hSnapshot, ref ProcessEntry32 lppe);

    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(nint hSnapshot, ref ProcessEntry32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint hObject);
}
