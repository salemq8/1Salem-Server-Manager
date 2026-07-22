namespace ServerManager.Client.Shell;

public enum NotificationKind
{
    Information,
    Success,
    Warning,
    Error
}

public sealed record AppNotification(
    NotificationKind Kind,
    string Title,
    string Message,
    bool Persistent = false);

public static class NotificationService
{
    public static event EventHandler<AppNotification>? Published;

    public static void Publish(
        NotificationKind kind,
        string title,
        string message,
        bool persistent = false) =>
        Published?.Invoke(
            null,
            new AppNotification(kind, title, message, persistent));
}
