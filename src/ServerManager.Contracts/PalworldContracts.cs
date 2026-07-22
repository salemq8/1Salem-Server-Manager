namespace ServerManager.Contracts;

public sealed record PalworldServerSettings(
    string ServerName,
    string Description,
    string ServerPassword,
    string AdminPassword,
    int MaxPlayers,
    int Port,
    bool CommunityServer,
    bool RconEnabled = false,
    int RconPort = 25575,
    bool RestApiEnabled = false,
    int RestApiPort = 8212);

public sealed record PalworldInstallRequest(
    string DestinationPath,
    PalworldServerSettings Settings,
    int AppId = 2_394_010);

public sealed record PalworldInstallResult(
    Guid ServerId,
    string RootPath,
    string? BuildId,
    DateTimeOffset InstalledAtUtc);

public sealed record PalworldServerMetadata(
    string Product,
    int AppId,
    string? BuildId,
    string ProtectedServerPassword,
    string ProtectedAdminPassword,
    PalworldServerSettingsTemplate Settings,
    DateTimeOffset InstalledAtUtc);

public sealed record PalworldServerSettingsTemplate(
    string ServerName,
    string Description,
    int MaxPlayers,
    int Port,
    bool CommunityServer,
    bool RconEnabled,
    int RconPort,
    bool RestApiEnabled = false,
    int RestApiPort = 8212);

public sealed record PalworldConfigurationResponse(
    Guid ServerId,
    string ServerName,
    string Description,
    int MaxPlayers,
    int Port,
    bool CommunityServer,
    bool RconEnabled,
    int RconPort,
    bool HasServerPassword,
    bool HasAdminPassword,
    bool AutoStart,
    bool AutoRestart,
    bool RestApiEnabled = false,
    int RestApiPort = 8212,
    bool IniRestApiEnabled = false,
    int IniRestApiPort = 8212,
    bool ManagementStateMismatch = false,
    string? LiveConfigurationPath = null);

public sealed record PalworldSettingsUpdateRequest(
    string ServerName,
    string Description,
    int MaxPlayers,
    int Port,
    bool CommunityServer,
    bool RconEnabled,
    int RconPort,
    string? NewServerPassword = null,
    string? NewAdminPassword = null,
    bool ApplyAndRestart = false);

public enum PalworldManagementState
{
    Disabled = 0,
    Connecting = 1,
    Online = 2,
    Unavailable = 3
}

public enum PalworldManagementActivationStage
{
    None = 0,
    ResolvingServer = 1,
    ValidatingConfiguration = 2,
    CreatingSafetyBackup = 3,
    SelectingLocalPort = 4,
    ProtectingCredentials = 5,
    WritingConfiguration = 6,
    RestartingServer = 7,
    WaitingForGameProcess = 8,
    TestingRestApi = 9,
    Connected = 10,
    RollingBack = 11,
    Failed = 12,
    Disabled = 13
}

public sealed record PalworldPlayerInfo(
    string Name,
    string? AccountName,
    string? PlayerId,
    string? UserId,
    string? IpAddress,
    double? PingMilliseconds,
    int? Level,
    double? LocationX,
    double? LocationY);

public sealed record PalworldManagementSnapshot(
    Guid ServerId,
    PalworldManagementState State,
    bool RestApiEnabled,
    int RestApiPort,
    string StatusMessage,
    string? ServerName,
    string? Description,
    string? ServerVersion,
    string? WorldGuid,
    double? ServerFps,
    double? ServerFrameTimeMilliseconds,
    int? PlayersOnline,
    int? MaximumPlayers,
    long? UptimeSeconds,
    IReadOnlyList<PalworldPlayerInfo> Players,
    DateTimeOffset CapturedAtUtc,
    int ConsecutiveFailures = 0,
    bool IsStale = false);

public sealed record PalworldRestEnableRequest(
    bool ConfirmRestart,
    int? PreferredPort = null);

public sealed record PalworldRestEnableResponse(
    bool Success,
    string Message,
    bool Restarted,
    bool Verified,
    PalworldManagementSnapshot Status,
    PalworldManagementActivationStage Stage =
        PalworldManagementActivationStage.None,
    string? ErrorCode = null,
    string? ConfigurationBackupPath = null,
    bool RolledBack = false);

public sealed record PalworldManagementActionRequest(
    bool ConfirmRestart = false,
    int? PreferredPort = null);

public sealed record PalworldRestOperationResult(
    bool Success,
    string Code,
    string Message,
    DateTimeOffset CompletedAtUtc);

public sealed record PalworldRestSettingsResult(
    bool Success,
    string Code,
    string Message,
    IReadOnlyDictionary<string, string> Settings,
    DateTimeOffset CompletedAtUtc);

public enum PalworldSettingValueType
{
    Number = 1,
    Integer = 2,
    Boolean = 3,
    Text = 4,
    Enumeration = 5,
    List = 6
}

public sealed record PalworldWorldSettingDescriptor(
    string Name,
    string Category,
    string DisplayName,
    string ArabicDisplayName,
    PalworldSettingValueType ValueType,
    string CurrentValue,
    string DefaultValue,
    string Unit,
    string Description,
    string ArabicDescription,
    double? Minimum,
    double? Maximum,
    IReadOnlyList<string> AllowedValues,
    bool RestartRequired,
    string? PerformanceWarning = null);

public sealed record PalworldWorldSettingsResponse(
    Guid ServerId,
    string ConfigurationPath,
    IReadOnlyList<PalworldWorldSettingDescriptor> Settings,
    IReadOnlyList<string> Categories,
    IReadOnlyList<string> Presets,
    bool HasUnsavedServerRestart = false,
    DateTimeOffset? LastChangedAtUtc = null);

public sealed record PalworldWorldSettingsUpdateRequest(
    IReadOnlyDictionary<string, string> Changes,
    bool ApplyAndRestart,
    bool ConfirmRestart,
    int AnnouncementSeconds = 0,
    string? AnnouncementMessage = null);

public sealed record PalworldWorldSettingsUpdateResponse(
    bool Success,
    string Message,
    IReadOnlyDictionary<string, string> AppliedChanges,
    string? ConfigurationBackupPath,
    bool Restarted,
    bool Verified,
    bool RolledBack,
    int ConnectedPlayers,
    string? ErrorCode = null);

public sealed record PalworldWorldSettingsPresetRequest(string Preset);

public sealed record PalworldWorldSettingsPresetResponse(
    string Preset,
    IReadOnlyDictionary<string, string> Values);

public sealed record PalworldConfigurationHistoryItem(
    string Id,
    DateTimeOffset CreatedAtUtc,
    string Reason,
    string BackupPath,
    long SizeBytes);

public sealed record PalworldRestoreConfigurationRequest(
    string HistoryId,
    bool ConfirmRestart);
