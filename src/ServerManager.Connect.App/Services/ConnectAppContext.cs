using ServerManager.Connect.App.Broker;
using ServerManager.Connect.App.Configuration;
using ServerManager.Connect.App.Identity;
using ServerManager.Connect.App.Shell;
using ServerManager.Connect.App.Transport;
using ServerManager.Connect.App.Updates;

namespace ServerManager.Connect.App.Services;

/// <summary>What a configured app with a usable device identity can talk to.</summary>
public sealed record ConnectServices(IBrokerClient Broker, IDeviceIdentity Identity, ITransportProcess TransportProcess);

/// <summary>Everything the view models depend on, built once at startup (or from fakes in tests).</summary>
public sealed class ConnectAppContext
{
    public required ConnectAppSettings Settings { get; init; }

    /// <summary>Null when the app is not configured, or when the device identity could not be opened.</summary>
    public ConnectServices? Services { get; init; }

    /// <summary>A user-facing reason the app cannot work, other than simply not being configured.</summary>
    public string? StartupProblem { get; init; }

    /// <summary>
    /// The file that keeps, across runs, which memberships' one-time enrollment blobs this device
    /// has used (<see cref="ConsumedEnrollments"/>). Null keeps that for the current run only, for
    /// a caller that never restarts the app, such as a test.
    /// </summary>
    public string? ConsumedEnrollmentsPath { get; init; }

    /// <summary>Always present: Diagnostics can ask a running transport even when nothing else works.</summary>
    public required ITransportClient Transport { get; init; }

    public required IAppClock Clock { get; init; }

    public required DiagnosticsLog Log { get; init; }

    public required IClipboardService Clipboard { get; init; }

    /// <summary>Checks, downloads and hands over updates. Null where updates are not offered (tests that do not need it).</summary>
    public ConnectUpdater? Updater { get; init; }

    /// <summary>The friend's own choices (theme). Null keeps the defaults without saving them.</summary>
    public ConnectPreferencesStore? Preferences { get; init; }

    /// <summary>Closing for an update, opening release links, applying a theme. Null where there is no window.</summary>
    public IAppShell? Shell { get; init; }
}
