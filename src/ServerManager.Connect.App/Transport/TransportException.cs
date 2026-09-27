namespace ServerManager.Connect.App.Transport;

/// <summary>
/// Error codes: the transport's own pipe codes (<c>ticket_rejected</c>, <c>already_enrolled</c>…),
/// passed through unchanged, plus the app-side conditions below.
/// </summary>
public static class TransportErrorCodes
{
    /// <summary>The pipe is not there or broke mid-call: the transport is not running.</summary>
    public const string Unavailable = "unavailable";

    /// <summary>
    /// The verified pipe is connected, but the call did not finish in time. Unlike
    /// <see cref="Unavailable"/>, the transport is running (busy or stuck), and whether the
    /// request took effect is unknown.
    /// </summary>
    public const string NoAnswer = "no_answer";

    /// <summary>
    /// The pipe is owned by another account, or served by a process that is not this app's
    /// transport (<see cref="TransportServerVerifier"/>). Nothing was sent to it.
    /// </summary>
    public const string Untrusted = "untrusted";

    /// <summary>The transport answered something this client cannot interpret.</summary>
    public const string InvalidResponse = "invalid_response";

    /// <summary>The transport executable is not installed next to the app.</summary>
    public const string Missing = "missing";

    /// <summary>The transport process ended before it served its pipe.</summary>
    public const string Exited = "exited";

    /// <summary>The transport's data folder, where its keyset file goes before it starts, cannot be written.</summary>
    public const string DataUnwritable = "data_unwritable";

    /// <summary>A transport is already running, in the other mode than the configured one.</summary>
    public const string ModeMismatch = "mode_mismatch";

    public const string AlreadyEnrolled = "already_enrolled";
    public const string NotEnrolled = "not_enrolled";
    public const string NoSession = "no_session";
    public const string TicketRejected = "ticket_rejected";
    public const string SessionKeyRejected = "session_key_rejected";
    public const string TicketMismatch = "ticket_mismatch";
}

/// <summary>A failed transport call. The message carries the code only.</summary>
public sealed class TransportException : Exception
{
    public TransportException(string code, Exception? innerException = null)
        : base($"The connection component reported '{code}'.", innerException)
    {
        Code = code;
    }

    public string Code { get; }
}
