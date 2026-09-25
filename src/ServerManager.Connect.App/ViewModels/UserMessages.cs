using ServerManager.Connect.App.Broker;
using ServerManager.Connect.App.Localization;
using ServerManager.Connect.App.Transport;
using ServerManager.Connect.Core.Enrollment;

namespace ServerManager.Connect.App.ViewModels;

/// <summary>
/// Turns the failures the app expects into one plain sentence for the friend. The technical cause
/// goes to Diagnostics. Pages catch only these, except fire-and-forget loops (polling, the session
/// monitor) and the Connection page's Connect, which must not stop silently or be left half-way:
/// they also catch the rest and show it as <see cref="Text.ErrorUnexpected"/>.
/// </summary>
internal static class UserMessages
{
    public static bool IsExpected(Exception exception) =>
        exception is BrokerException or TransportException or EnrollmentDecryptionException;

    public static string For(Exception exception) => exception switch
    {
        BrokerException { Failure: BrokerFailure.Unreachable or BrokerFailure.Unavailable } => Text.ErrorBrokerUnreachable,
        BrokerException { Failure: BrokerFailure.RateLimited } => Text.ErrorTooManyAttempts,
        BrokerException { Failure: BrokerFailure.Unauthorized } => Text.ErrorDeviceNotAccepted,
        // Not "reinstall": the pipe is held by something that is not this app's transport, and
        // nothing was sent to it (TransportServerVerifier).
        TransportException { Code: TransportErrorCodes.Untrusted } => Text.ErrorTransportUntrusted,
        TransportException => Text.ErrorTransportUnavailable,
        EnrollmentDecryptionException => Text.ErrorSetupFailed,
        _ => Text.ErrorUnexpected
    };
}
