namespace ServerManager.Contracts;

public sealed record AgentStatusResponse(
    string AgentId,
    string MachineName,
    string Version,
    DateTimeOffset StartedAtUtc,
    bool DatabaseReady,
    string Transport,
    string ApiBinding);

public sealed record GameServerStatusResponse(
    Guid ServerId,
    GameType Game,
    string Name,
    ServerState State,
    string? InstalledVersion,
    int? ProcessId,
    TimeSpan? Uptime,
    long WorkingSetBytes,
    double CpuPercent,
    int Port);

public sealed record HealthResponse(
    string Status,
    string Version,
    DateTimeOffset TimestampUtc);

public sealed record ApiErrorResponse(
    string Code,
    string WhatFailed,
    string? LikelyReason,
    string? PathOrOperation,
    string SuggestedFix,
    bool CanRetry);

public sealed record StartupFailureDetails(
    string Stage,
    string Message,
    string? ErrorCode,
    string? ExecutablePath,
    string? WorkingDirectory,
    int? ExitCode,
    bool PortConflict,
    string? JavaDetection,
    string? MemoryAllocation,
    IReadOnlyList<string> ConsoleLines,
    string SuggestedFix,
    bool CanRetry);

public sealed record ServerActionAvailability(
    bool CanStart,
    bool CanStop,
    bool CanRestart,
    bool CanForceStop,
    bool CanBackup,
    bool CanRestore,
    bool CanUpdate,
    bool CanSendCommand);

public sealed record ServerDashboardCard(
    Guid ServerId,
    GameType Game,
    bool IsInstalled,
    string Name,
    ServerState State,
    string? LocalAddress,
    string? InstalledVersion,
    string? RuntimeVersion,
    int? PlayersOnline,
    int? MaximumPlayers,
    int? ProcessId,
    double CpuPercent,
    long WorkingSetBytes,
    long PrivateMemoryBytes,
    long PeakWorkingSetBytes,
    TimeSpan? Uptime,
    DateTimeOffset? LastBackupAtUtc,
    string? UpdateStatus,
    int Port,
    int? ActiveMinimumMemoryMb,
    int? ActiveMaximumMemoryMb,
    int? PendingMinimumMemoryMb,
    int? PendingMaximumMemoryMb,
    string? LastConsoleLine,
    string? LastError,
    ServerActionAvailability Actions,
    bool AutoStart = false,
    bool AutoRestart = true,
    System.Diagnostics.ProcessPriorityClass Priority =
        System.Diagnostics.ProcessPriorityClass.Normal,
    long? CpuAffinityMask = null,
    int? GameProcessId = null,
    int ChildProcessCount = 0,
    string? RootExecutableName = null,
    string? GameExecutableName = null,
    string? InternetAddress = null,
    string? PlayitState = null,
    bool PublicTunnelOnline = false,
    bool PublicTunnelVerified = false,
    PalworldManagementSnapshot? PalworldManagement = null,
    bool LocalPortOpen = false,
    bool PlayitOnline = false,
    bool RestManagementConnected = false);

public sealed record DashboardSnapshot(
    AgentStatusResponse Agent,
    string? LocalIpv4,
    long TotalMemoryBytes,
    long UsedMemoryBytes,
    long AvailableMemoryBytes,
    double CpuPercent,
    long SystemDriveFreeBytes,
    long AgentMemoryBytes,
    int ActiveServerCount,
    int WarningCount,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<ServerDashboardCard> Servers,
    DateTimeOffset CapturedAtUtc,
    string ResourceProfileSummary = "Active: Balanced · No managed game servers");
