using ServerManager.Contracts;

namespace ServerManager.Core;

public static class ServerActionPolicy
{
    public static ServerActionAvailability For(
        ServerState state,
        bool hasBackup = false) =>
        state switch
        {
            ServerState.Stopped => new(
                CanStart: true,
                CanStop: false,
                CanRestart: false,
                CanForceStop: false,
                CanBackup: true,
                CanRestore: hasBackup,
                CanUpdate: true,
                CanSendCommand: false),
            ServerState.Running => new(
                CanStart: false,
                CanStop: true,
                CanRestart: true,
                CanForceStop: true,
                CanBackup: true,
                CanRestore: false,
                CanUpdate: true,
                CanSendCommand: true),
            ServerState.Error or ServerState.Crashed => new(
                CanStart: true,
                CanStop: false,
                CanRestart: false,
                CanForceStop: false,
                CanBackup: true,
                CanRestore: hasBackup,
                CanUpdate: true,
                CanSendCommand: false),
            _ => new(
                CanStart: false,
                CanStop: false,
                CanRestart: false,
                CanForceStop: false,
                CanBackup: false,
                CanRestore: false,
                CanUpdate: false,
                CanSendCommand: false)
        };
}
