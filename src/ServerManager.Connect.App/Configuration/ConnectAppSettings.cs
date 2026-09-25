namespace ServerManager.Connect.App.Configuration;

public enum TransportMode
{
    /// <summary>A real userspace tailnet node per owner tailnet.</summary>
    Tsnet,

    /// <summary>Loopback stand-in for tests and the disposable proof. Not a security boundary.</summary>
    Fake
}

/// <summary>
/// What this copy of 1Salem Connect talks to. A release carries these values next to the
/// executable; a copy without a broker address is "not configured" and says so instead of
/// pretending to work.
/// </summary>
public sealed record ConnectAppSettings
{
    public Uri? BrokerUrl { get; init; }

    /// <summary>Allows plain HTTP to 127.0.0.1/localhost and the fake transport. Never in a release.</summary>
    public bool DevelopmentMode { get; init; }

    public TransportMode TransportMode { get; init; } = TransportMode.Tsnet;

    /// <summary>The node id the fake transport claims. Only meaningful in fake mode.</summary>
    public string? FakeNodeId { get; init; }

    public required string TransportExecutablePath { get; init; }

    public bool IsConfigured => BrokerUrl is not null;

    public static ConnectAppSettings Unconfigured(string baseDirectory) => new()
    {
        TransportExecutablePath = DefaultTransportPath(baseDirectory)
    };

    public static string DefaultTransportPath(string baseDirectory) =>
        Path.Combine(baseDirectory, "1Salem.Connect.Transport.exe");
}
