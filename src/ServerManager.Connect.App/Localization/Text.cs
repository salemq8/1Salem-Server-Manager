using System.Globalization;
using System.Resources;

namespace ServerManager.Connect.App.Localization;

/// <summary>
/// Typed access to Resources/Strings.resx. Each property's name is its resource key, so code and
/// XAML (<c>{x:Static loc:Text.InviteTitle}</c>) never spell a key by hand, and a test checks that
/// every property resolves and every entry has a property. Lookups use the current UI culture, so
/// adding Strings.ar.resx is all an Arabic build needs.
/// </summary>
public static class Text
{
    internal const string TableName = "ServerManager.Connect.App.Resources.Strings";

    internal static readonly ResourceManager Table = new(TableName, typeof(Text).Assembly);

    public static string AppTitle => Get(nameof(AppTitle));
    public static string NavServers => Get(nameof(NavServers));
    public static string NavDiagnostics => Get(nameof(NavDiagnostics));
    public static string CommonBack => Get(nameof(CommonBack));
    public static string CommonRetry => Get(nameof(CommonRetry));

    public static string WelcomeTitle => Get(nameof(WelcomeTitle));
    public static string WelcomeBody => Get(nameof(WelcomeBody));
    public static string WelcomeGetStarted => Get(nameof(WelcomeGetStarted));
    public static string NotConfigured => Get(nameof(NotConfigured));
    public static string NotConfiguredDetail => Get(nameof(NotConfiguredDetail));

    public static string InviteTitle => Get(nameof(InviteTitle));
    public static string InviteBody => Get(nameof(InviteBody));
    public static string InviteInputLabel => Get(nameof(InviteInputLabel));
    public static string InviteJoin => Get(nameof(InviteJoin));
    public static string InviteNotValid => Get(nameof(InviteNotValid));

    public static string WaitingTitle => Get(nameof(WaitingTitle));
    public static string WaitingBody => Get(nameof(WaitingBody));
    public static string WaitingStatus => Get(nameof(WaitingStatus));
    public static string WaitingDeclined => Get(nameof(WaitingDeclined));

    public static string ServersTitle => Get(nameof(ServersTitle));
    public static string ServersEmpty => Get(nameof(ServersEmpty));
    public static string ServersAdd => Get(nameof(ServersAdd));
    public static string ServersRefresh => Get(nameof(ServersRefresh));
    public static string ServersOpen => Get(nameof(ServersOpen));
    public static string ServersOpenNamed => Get(nameof(ServersOpenNamed));

    public static string StatusWaitingForApproval => Get(nameof(StatusWaitingForApproval));
    public static string StatusEnrollmentPending => Get(nameof(StatusEnrollmentPending));
    public static string StatusConfirmationPending => Get(nameof(StatusConfirmationPending));
    public static string StatusConfirmationFailed => Get(nameof(StatusConfirmationFailed));
    public static string StatusReady => Get(nameof(StatusReady));
    public static string StatusDeclined => Get(nameof(StatusDeclined));
    public static string StatusUnavailable => Get(nameof(StatusUnavailable));

    public static string StateConnecting => Get(nameof(StateConnecting));
    public static string StateConnected => Get(nameof(StateConnected));
    public static string StateDisconnected => Get(nameof(StateDisconnected));
    public static string StateServerOffline => Get(nameof(StateServerOffline));
    public static string StateAccessExpired => Get(nameof(StateAccessExpired));
    public static string StateAccessRevoked => Get(nameof(StateAccessRevoked));

    public static string ConnectionConnect => Get(nameof(ConnectionConnect));
    public static string ConnectionDisconnect => Get(nameof(ConnectionDisconnect));
    public static string ConnectionCopy => Get(nameof(ConnectionCopy));
    public static string ConnectionCopyAddress => Get(nameof(ConnectionCopyAddress));
    public static string ConnectionCopied => Get(nameof(ConnectionCopied));
    public static string ConnectionAddressLabel => Get(nameof(ConnectionAddressLabel));
    public static string ConnectionAddressHint => Get(nameof(ConnectionAddressHint));

    public static string ErrorBrokerUnreachable => Get(nameof(ErrorBrokerUnreachable));
    public static string ErrorTooManyAttempts => Get(nameof(ErrorTooManyAttempts));
    public static string ErrorDeviceNotAccepted => Get(nameof(ErrorDeviceNotAccepted));
    public static string ErrorTransportUnavailable => Get(nameof(ErrorTransportUnavailable));
    public static string ErrorTransportUntrusted => Get(nameof(ErrorTransportUntrusted));
    public static string ErrorSetupFailed => Get(nameof(ErrorSetupFailed));
    public static string ErrorIdentityUnavailable => Get(nameof(ErrorIdentityUnavailable));
    public static string ErrorSessionLost => Get(nameof(ErrorSessionLost));
    public static string ErrorCloseUnconfirmed => Get(nameof(ErrorCloseUnconfirmed));
    public static string ErrorUnexpected => Get(nameof(ErrorUnexpected));

    public static string DiagnosticsTitle => Get(nameof(DiagnosticsTitle));
    public static string DiagnosticsBody => Get(nameof(DiagnosticsBody));
    public static string DiagnosticsRefresh => Get(nameof(DiagnosticsRefresh));
    public static string DiagnosticsCopy => Get(nameof(DiagnosticsCopy));
    public static string DiagnosticsReportLabel => Get(nameof(DiagnosticsReportLabel));
    public static string DiagnosticsApp => Get(nameof(DiagnosticsApp));
    public static string DiagnosticsBroker => Get(nameof(DiagnosticsBroker));
    public static string DiagnosticsNotConfigured => Get(nameof(DiagnosticsNotConfigured));
    public static string DiagnosticsDevelopmentMode => Get(nameof(DiagnosticsDevelopmentMode));
    public static string DiagnosticsDevice => Get(nameof(DiagnosticsDevice));
    public static string DiagnosticsConfiguredMode => Get(nameof(DiagnosticsConfiguredMode));
    public static string DiagnosticsFakeMode => Get(nameof(DiagnosticsFakeMode));
    public static string DiagnosticsTransport => Get(nameof(DiagnosticsTransport));
    public static string DiagnosticsTransportNotRunning => Get(nameof(DiagnosticsTransportNotRunning));
    public static string DiagnosticsTicketKeys => Get(nameof(DiagnosticsTicketKeys));
    public static string DiagnosticsNodes => Get(nameof(DiagnosticsNodes));
    public static string DiagnosticsSessions => Get(nameof(DiagnosticsSessions));
    public static string DiagnosticsErrors => Get(nameof(DiagnosticsErrors));
    public static string DiagnosticsTransportLog => Get(nameof(DiagnosticsTransportLog));
    public static string DiagnosticsNone => Get(nameof(DiagnosticsNone));

    /// <summary>A table entry with <c>{0}</c> filled in for the current culture.</summary>
    public static string Format(string template, object argument) =>
        string.Format(CultureInfo.CurrentCulture, template, argument);

    private static string Get(string key) =>
        Table.GetString(key, CultureInfo.CurrentUICulture) ??
        throw new MissingManifestResourceException($"The string table has no entry '{key}'.");
}
