using ServerManager.Connect.App.Localization;

namespace ServerManager.Connect.App.ViewModels;

/// <summary>The only connection states a friend ever sees.</summary>
public enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    ServerOffline,
    AccessExpired,
    AccessRevoked
}

public static class ConnectionStateText
{
    public static string For(ConnectionState state) => state switch
    {
        ConnectionState.Connecting => Text.StateConnecting,
        ConnectionState.Connected => Text.StateConnected,
        ConnectionState.ServerOffline => Text.StateServerOffline,
        ConnectionState.AccessExpired => Text.StateAccessExpired,
        ConnectionState.AccessRevoked => Text.StateAccessRevoked,
        _ => Text.StateDisconnected
    };
}
