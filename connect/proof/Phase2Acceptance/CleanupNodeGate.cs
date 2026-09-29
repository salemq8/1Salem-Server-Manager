namespace Phase2Acceptance;

/// <summary>Pure evidence gate; no node is considered removed before a final, visible 404.</summary>
internal readonly record struct CleanupNodeDecision(bool SeenByApi, bool DeleteDevice, string? Failure)
{
    public bool PreserveRecoveryState => Failure is not null;

    public bool ConfirmsRemoval(int finalStatus) => !PreserveRecoveryState && SeenByApi && finalStatus == 404;
}

internal static class CleanupNodeGate
{
    public static void RequireNotBaseline(bool isBaseline)
    {
        if (isBaseline) throw new InvalidOperationException("Baseline device is protected.");
    }

    public static CleanupNodeDecision Evaluate(int initialStatus, bool matchesRun,
        bool driverSawNode, bool agentSawNode)
    {
        var seen = driverSawNode || agentSawNode;
        if (initialStatus == 404)
        {
            // A never-visible node can be hidden from this OAuth client, not deleted.
            return new(seen, false, seen ? null : "Device was never positively observed by the API; removal is unconfirmed.");
        }

        if (initialStatus != 200 || !matchesRun)
            return new(seen, false, "Device does not match this run.");

        return new(true, true, null);
    }
}
