namespace ServerManager.Connect.App.Broker;

/// <summary>
/// How a broker call failed, at the level of detail the broker deliberately exposes (§14): a
/// missing, foreign, pending or revoked resource are all <see cref="NotFound"/>.
/// </summary>
public enum BrokerFailure
{
    /// <summary>404. Generic by design; the caller works out what it means from context.</summary>
    NotFound,

    /// <summary>401: unknown device key, bad signature, or this PC's clock is off by more than 5 minutes.</summary>
    Unauthorized,

    /// <summary>409, for example a node already bound.</summary>
    Conflict,

    /// <summary>429, with <see cref="BrokerException.RetryAfter"/> when the broker sent one.</summary>
    RateLimited,

    /// <summary>503 or another server error: the broker is up but cannot serve.</summary>
    Unavailable,

    /// <summary>No HTTP answer at all: offline, DNS, TLS, timeout.</summary>
    Unreachable,

    /// <summary>400, 413 or a redirect: the broker refused the request as sent.</summary>
    Rejected,

    /// <summary>A response this client cannot trust or understand.</summary>
    InvalidResponse
}

/// <summary>A failed broker call. The message names the operation and status only, never a body or secret.</summary>
public sealed class BrokerException : Exception
{
    public BrokerException(BrokerFailure failure, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Failure = failure;
    }

    public BrokerFailure Failure { get; }

    public TimeSpan? RetryAfter { get; init; }
}

/// <summary>The configured broker address is not one the app may use (<see cref="Configuration.BrokerEndpointPolicy"/>).</summary>
public sealed class BrokerEndpointRefusedException : Exception
{
    public BrokerEndpointRefusedException(string reason)
        : base(reason)
    {
    }
}
