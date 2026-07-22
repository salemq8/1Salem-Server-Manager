using ServerManager.Contracts;

namespace ServerManager.Core;

public static class ServerRecoveryPolicy
{
    public static ServerState ReconcileAfterAgentRestart(
        ServerState persistedState,
        bool autoStart) =>
        autoStart
            ? ServerState.Starting
            : persistedState is ServerState.NotInstalled
                ? ServerState.NotInstalled
                : ServerState.Stopped;

    public static bool ShouldAutoStart(bool autoStart, bool installationExists) =>
        autoStart && installationExists;
}
