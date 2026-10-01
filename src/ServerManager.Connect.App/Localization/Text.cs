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
    public static string StatusSetupFailed => Get(nameof(StatusSetupFailed));
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

    public static string NavSettings => Get(nameof(NavSettings));
    public static string SettingsTitle => Get(nameof(SettingsTitle));
    public static string SettingsBody => Get(nameof(SettingsBody));
    public static string UpdatesTitle => Get(nameof(UpdatesTitle));
    public static string UpdatesVersionLabel => Get(nameof(UpdatesVersionLabel));
    public static string UpdatesBuildLabel => Get(nameof(UpdatesBuildLabel));
    public static string UpdatesStatusLabel => Get(nameof(UpdatesStatusLabel));
    public static string UpdatesLastCheckedLabel => Get(nameof(UpdatesLastCheckedLabel));
    public static string UpdatesChannelLabel => Get(nameof(UpdatesChannelLabel));
    public static string UpdatesChannelStable => Get(nameof(UpdatesChannelStable));
    public static string UpdatesNever => Get(nameof(UpdatesNever));
    public static string UpdatesCheck => Get(nameof(UpdatesCheck));
    public static string UpdatesUpdateNow => Get(nameof(UpdatesUpdateNow));
    public static string UpdatesStatusNotChecked => Get(nameof(UpdatesStatusNotChecked));
    public static string UpdatesStatusChecking => Get(nameof(UpdatesStatusChecking));
    public static string UpdatesStatusUpToDate => Get(nameof(UpdatesStatusUpToDate));
    public static string UpdatesStatusAvailable => Get(nameof(UpdatesStatusAvailable));
    public static string UpdatesStatusUnavailable => Get(nameof(UpdatesStatusUnavailable));
    public static string UpdatesStatusDownloading => Get(nameof(UpdatesStatusDownloading));
    public static string UpdatesStatusInstalling => Get(nameof(UpdatesStatusInstalling));
    public static string UpdatesStatusFailed => Get(nameof(UpdatesStatusFailed));
    public static string UpdatesAvailableVersion => Get(nameof(UpdatesAvailableVersion));
    public static string UpdatesAvailableBuild => Get(nameof(UpdatesAvailableBuild));
    public static string UpdatesProblemUnreachable => Get(nameof(UpdatesProblemUnreachable));
    public static string UpdatesProblemNoInformation => Get(nameof(UpdatesProblemNoInformation));
    public static string UpdatesProblemInvalid => Get(nameof(UpdatesProblemInvalid));
    public static string UpdatesProblemDownload => Get(nameof(UpdatesProblemDownload));
    public static string UpdatesProblemHash => Get(nameof(UpdatesProblemHash));
    public static string UpdatesProblemCancelled => Get(nameof(UpdatesProblemCancelled));
    public static string UpdatesProblemLaunch => Get(nameof(UpdatesProblemLaunch));
    public static string UpdatesAdminNote => Get(nameof(UpdatesAdminNote));
    public static string UpdatesDisconnectFirst => Get(nameof(UpdatesDisconnectFirst));
    public static string UpdatesDisconnectDetail => Get(nameof(UpdatesDisconnectDetail));
    public static string UpdatesDisconnectAndUpdate => Get(nameof(UpdatesDisconnectAndUpdate));
    public static string UpdatesLater => Get(nameof(UpdatesLater));
    public static string UpdatesDisconnectFailed => Get(nameof(UpdatesDisconnectFailed));
    public static string UpdatesPortableAvailable => Get(nameof(UpdatesPortableAvailable));
    public static string UpdatesPortableDetail => Get(nameof(UpdatesPortableDetail));
    public static string UpdatesPortableDownload => Get(nameof(UpdatesPortableDownload));
    public static string UpdatesPortableNote => Get(nameof(UpdatesPortableNote));
    public static string NoticeUpdateAvailable => Get(nameof(NoticeUpdateAvailable));
    public static string NoticeUpdate => Get(nameof(NoticeUpdate));
    public static string NoticeDismiss => Get(nameof(NoticeDismiss));
    public static string NoticeUpdated => Get(nameof(NoticeUpdated));
    public static string NoticeUpdateFailed => Get(nameof(NoticeUpdateFailed));
    public static string NoticeUpdateBlocked => Get(nameof(NoticeUpdateBlocked));
    public static string NoticeUpdateInProgress => Get(nameof(NoticeUpdateInProgress));
    public static string AppearanceTitle => Get(nameof(AppearanceTitle));
    public static string AppearanceTheme => Get(nameof(AppearanceTheme));
    public static string ThemeSystem => Get(nameof(ThemeSystem));
    public static string ThemeDark => Get(nameof(ThemeDark));
    public static string ThemeLight => Get(nameof(ThemeLight));

    /// <summary>A table entry with <c>{0}</c> filled in for the current culture.</summary>
    public static string Format(string template, object argument) =>
        string.Format(CultureInfo.CurrentCulture, template, argument);

    /// <summary>
    /// Whether this culture (or a parent of it, such as "ar" for "ar-SA") has its own string
    /// table. A right-to-left layout is only right when the words shown are right-to-left too;
    /// without a translation the app shows English and must stay left to right.
    /// </summary>
    public static bool HasOwnTable(CultureInfo culture)
    {
        for (var current = culture; !current.Equals(CultureInfo.InvariantCulture); current = current.Parent)
        {
            if (Table.GetResourceSet(current, createIfNotExists: true, tryParents: false) is not null)
            {
                return true;
            }
        }

        return false;
    }

    private static string Get(string key) =>
        Table.GetString(key, CultureInfo.CurrentUICulture) ??
        throw new MissingManifestResourceException($"The string table has no entry '{key}'.");
}
