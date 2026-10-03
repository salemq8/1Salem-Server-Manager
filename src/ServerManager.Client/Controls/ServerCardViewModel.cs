using System.ComponentModel;
using System.Runtime.CompilerServices;
using ServerManager.Contracts;
using ServerManager.Core;

namespace ServerManager.Client.Controls;

/// <summary>
/// What a server card shows. Updated in place on every poll so the visual tree is not rebuilt.
/// Technical identifiers are carried but deliberately surfaced only under Advanced details.
/// </summary>
public sealed class ServerCardViewModel : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private string _gameName = string.Empty;
    private UiStatus _status;
    private string _statusLabel = string.Empty;
    private UiStatusTone _statusTone;
    private string _players = "—";
    private string _uptime = "—";
    private string _memory = "—";
    private string _backupLabel = string.Empty;
    private UiStatusTone _backupTone;
    private bool _remoteAccessOnline;
    private string _remoteAccessLabel = string.Empty;
    private UiStatusTone _remoteAccessTone;
    private string _primaryAction = string.Empty;
    private bool _primaryActionStarts;
    private string? _internetAddress;
    private int? _processId;
    private int? _gameProcessId;
    private int _childProcessCount;
    private string? _gameExecutableName;
    private int _port;
    private ServerDashboardCard? _lastCard;
    private DateTimeOffset _lastCapturedAtUtc;

    public ServerCardViewModel(ServerDashboardCard card, DateTimeOffset capturedAtUtc)
    {
        ServerId = card.ServerId;
        Game = card.Game;
        Update(card, capturedAtUtc);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Guid ServerId { get; }

    public GameType Game { get; }

    /// <summary>
    /// The card exactly as the agent reported it. Server Detail needs the raw record for
    /// action availability and for the technical values it shows under Advanced details;
    /// those must come from Build 5 telemetry rather than be re-derived in the client.
    /// </summary>
    public ServerDashboardCard? Source => _lastCard;

    public string Name
    {
        get => _name;
        private set => SetField(ref _name, value);
    }

    public string GameName
    {
        get => _gameName;
        private set => SetField(ref _gameName, value);
    }

    public UiStatus Status
    {
        get => _status;
        private set => SetField(ref _status, value);
    }

    public string StatusLabel
    {
        get => _statusLabel;
        private set => SetField(ref _statusLabel, value);
    }

    public UiStatusTone StatusTone
    {
        get => _statusTone;
        private set => SetField(ref _statusTone, value);
    }

    public string Players
    {
        get => _players;
        private set => SetField(ref _players, value);
    }

    public string Uptime
    {
        get => _uptime;
        private set => SetField(ref _uptime, value);
    }

    public string Memory
    {
        get => _memory;
        private set => SetField(ref _memory, value);
    }

    public string BackupLabel
    {
        get => _backupLabel;
        private set => SetField(ref _backupLabel, value);
    }

    public UiStatusTone BackupTone
    {
        get => _backupTone;
        private set => SetField(ref _backupTone, value);
    }

    public bool RemoteAccessOnline
    {
        get => _remoteAccessOnline;
        private set => SetField(ref _remoteAccessOnline, value);
    }

    public string RemoteAccessLabel
    {
        get => _remoteAccessLabel;
        private set => SetField(ref _remoteAccessLabel, value);
    }

    public UiStatusTone RemoteAccessTone
    {
        get => _remoteAccessTone;
        private set => SetField(ref _remoteAccessTone, value);
    }

    public string PrimaryAction
    {
        get => _primaryAction;
        private set => SetField(ref _primaryAction, value);
    }

    public bool PrimaryActionStartsServer
    {
        get => _primaryActionStarts;
        private set => SetField(ref _primaryActionStarts, value);
    }

    public string? InternetAddress
    {
        get => _internetAddress;
        private set => SetField(ref _internetAddress, value);
    }

    // --- Advanced details only ---

    public int? ProcessId
    {
        get => _processId;
        private set => SetField(ref _processId, value);
    }

    public int? GameProcessId
    {
        get => _gameProcessId;
        private set => SetField(ref _gameProcessId, value);
    }

    public int ChildProcessCount
    {
        get => _childProcessCount;
        private set => SetField(ref _childProcessCount, value);
    }

    public string? GameExecutableName
    {
        get => _gameExecutableName;
        private set => SetField(ref _gameExecutableName, value);
    }

    public int Port
    {
        get => _port;
        private set => SetField(ref _port, value);
    }

    /// <summary>
    /// Re-derives every label from the card last seen, in the language now in force. Several
    /// of these values are localized sentences baked in at poll time, so without this a card
    /// keeps the language it was built in until the next poll happens to change its data.
    /// </summary>
    /// <summary>
    /// Re-raises the tone properties so their converter-backed brush bindings re-resolve
    /// against the palette now in force. Without this a status badge keeps the previous
    /// theme's fill — dark green on a white card after switching to Light.
    /// </summary>
    public void RefreshThemeBindings()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusTone)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BackupTone)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RemoteAccessTone)));
    }

    public void RefreshLocalizedText()
    {
        if (_lastCard is not { } card)
        {
            return;
        }

        Update(card, _lastCapturedAtUtc);
    }

    public void Update(ServerDashboardCard card, DateTimeOffset capturedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(card);
        _lastCard = card;
        _lastCapturedAtUtc = capturedAtUtc;
        Name = card.Name;
        GameName = ServerPresentation.DescribeGame(card.Game);
        Status = ServerPresentation.MapState(card.State);
        StatusLabel = ServerPresentation.LabelFor(Status);
        StatusTone = ServerPresentation.ToneOf(Status);
        Players = MinecraftPlayersPresentation.Count(card.PlayersOnline, card.MaximumPlayers, card.PlayersStale);
        Uptime = ServerPresentation.FormatUptime(card.Uptime);
        Memory = ServerPresentation.FormatMemory(card.WorkingSetBytes);
        var backup = ServerPresentation.DescribeBackup(card.LastBackupAtUtc, capturedAtUtc);
        BackupLabel = backup.Label;
        BackupTone = backup.Tone;
        RemoteAccessOnline = card.PublicTunnelOnline;
        InternetAddress = card.InternetAddress;
        var remote = ServerPresentation.DescribeRemoteAccess(
            card.PublicTunnelOnline,
            card.InternetAddress);
        RemoteAccessLabel = remote.Label;
        RemoteAccessTone = remote.Tone;
        PrimaryAction = ServerPresentation.PrimaryActionFor(Status);
        PrimaryActionStartsServer = ServerPresentation.PrimaryActionStartsServer(Status);
        ProcessId = card.ProcessId;
        GameProcessId = card.GameProcessId;
        ChildProcessCount = card.ChildProcessCount;
        GameExecutableName = card.GameExecutableName;
        Port = card.Port;
    }

    private void SetField<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
