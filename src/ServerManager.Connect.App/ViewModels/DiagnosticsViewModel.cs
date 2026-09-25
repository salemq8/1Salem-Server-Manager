using System.Globalization;
using ServerManager.Connect.App.Configuration;
using ServerManager.Connect.App.Localization;
using ServerManager.Connect.App.Services;
using ServerManager.Connect.App.Transport;

namespace ServerManager.Connect.App.ViewModels;

/// <summary>
/// The only page with network vocabulary: mode, node ids, versions, ticket key ids, sessions and
/// recent errors. It asks a running transport for its <c>diag</c> but never starts one. The report
/// is redacted as a whole before it is shown, so Copy cannot leak a secret either.
/// </summary>
public sealed class DiagnosticsViewModel : ObservableObject, IPageLifetime
{
    private readonly ConnectAppContext _context;
    private string _reportText = string.Empty;

    public DiagnosticsViewModel(ConnectAppContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        RefreshCommand = new AsyncCommand(() => RefreshAsync(CancellationToken.None));
        CopyCommand = new RelayCommand(() => _context.Clipboard.TrySetText(ReportText), () => ReportText.Length > 0);
    }

    public string ReportText
    {
        get => _reportText;
        private set
        {
            if (Set(ref _reportText, value))
            {
                CopyCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public AsyncCommand RefreshCommand { get; }

    public RelayCommand CopyCommand { get; }

    public void OnShown() => RefreshCommand.Execute(null);

    public void OnHidden()
    {
    }

    internal async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var settings = _context.Settings;
        var report = new DiagnosticsReport()
            .Line(Text.DiagnosticsApp, AppVersion.Current)
            .Line(Text.DiagnosticsBroker, settings.BrokerUrl?.ToString() ?? Text.DiagnosticsNotConfigured)
            .Line(Text.DiagnosticsDevelopmentMode, settings.DevelopmentMode ? "true" : "false")
            .Line(Text.DiagnosticsConfiguredMode, settings.TransportMode == TransportMode.Fake
                ? Text.Format(Text.DiagnosticsFakeMode, settings.FakeNodeId ?? string.Empty)
                : "tsnet")
            .Line(Text.DiagnosticsDevice, _context.Services?.Identity.DeviceId);

        IReadOnlyCollection<string> transportLog = [];
        try
        {
            var diagnostics = await _context.Transport.DiagnosticsAsync(cancellationToken);
            report
                .Line(Text.DiagnosticsTransport, $"{diagnostics.Mode} {diagnostics.Version}")
                .Line(Text.DiagnosticsTicketKeys, string.Join(", ", diagnostics.TicketKeyIds))
                .Section(Text.DiagnosticsNodes, diagnostics.Nodes
                    .Select(node => $"{node.Node}  {node.NodeId ?? "-"}  {node.State}")
                    .ToList())
                .Section(Text.DiagnosticsSessions, diagnostics.Sessions
                    .Select(session => string.Join(
                        "  ",
                        session.SessionId,
                        session.Local,
                        session.State,
                        session.HostBridge,
                        session.Connections.ToString(CultureInfo.InvariantCulture)))
                    .ToList());
            transportLog = diagnostics.Log;
        }
        catch (TransportException exception) when (exception.Code == TransportErrorCodes.Unavailable)
        {
            report.Line(Text.DiagnosticsTransport, Text.DiagnosticsTransportNotRunning);
        }
        catch (TransportException exception)
        {
            report.Line(Text.DiagnosticsTransport, exception.Message);
        }

        ReportText = report
            .Section(Text.DiagnosticsErrors, _context.Log.Entries)
            .Section(Text.DiagnosticsTransportLog, transportLog)
            .Build();
    }
}
