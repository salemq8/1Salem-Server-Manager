using System.ComponentModel;
using Microsoft.Win32.SafeHandles;
using ServerManager.Connect.App.Transport;

namespace ServerManager.Connect.App.Tests.Fakes;

/// <summary>
/// Processes as the verifier sees them, set by the test. This test process itself is answered
/// with <paramref name="ownLevel"/> (null: its integrity level cannot be read). Every handle
/// handed out is recorded, so a test can tell which were kept and which released.
/// </summary>
internal sealed class FakeProcessInspector(int? ownLevel) : IProcessInspector
{
    public Dictionary<int, FakeProcess> Processes { get; } = [];

    public List<FakeOpenedProcess> Opened { get; } = [];

    public int ServerProcessId(SafePipeHandle pipe) => throw new Win32Exception();

    public IOpenedProcess? Open(int processId)
    {
        FakeProcess? process;
        if (processId == Environment.ProcessId)
        {
            process = new FakeProcess(ownLevel is { } level ? new InspectedProcess(Environment.ProcessPath!, level) : null);
        }
        else if (!Processes.TryGetValue(processId, out process))
        {
            return null;
        }

        var opened = new FakeOpenedProcess(processId, process);
        Opened.Add(opened);
        return opened;
    }

    /// <summary>The handles opened for <paramref name="processId"/> that are still open.</summary>
    public List<FakeOpenedProcess> OpenHandles(int processId) =>
        Opened.Where(opened => opened.ProcessId == processId && !opened.Disposed).ToList();
}

/// <param name="info">What reading it returns; null when it cannot be read.</param>
internal sealed class FakeProcess(InspectedProcess? info)
{
    public InspectedProcess? Info { get; } = info;

    public bool HasExited { get; set; }
}

internal sealed class FakeOpenedProcess(int processId, FakeProcess process) : IOpenedProcess
{
    public int ProcessId { get; } = processId;

    public int Inspections { get; private set; }

    public bool Disposed { get; private set; }

    public bool HasExited => Disposed ? throw new ObjectDisposedException(nameof(FakeOpenedProcess)) : process.HasExited;

    public InspectedProcess? Inspect()
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        Inspections++;
        return process.Info;
    }

    public void Dispose() => Disposed = true;
}
