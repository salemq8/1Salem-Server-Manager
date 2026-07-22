namespace ServerManager.Contracts;

public sealed record MinecraftVersionDescriptor(
    string Id,
    string Type,
    Uri MetadataUrl,
    Uri ServerDownloadUrl,
    string Sha1,
    long SizeBytes,
    int RequiredJavaMajor);

public sealed record MinecraftServerSettings(
    string Motd,
    int MaxPlayers,
    string Difficulty,
    string GameMode,
    bool OnlineMode,
    int ViewDistance,
    int SimulationDistance,
    bool WhitelistEnabled,
    bool Hardcore = false,
    bool Pvp = true);

public sealed record MinecraftInstallRequest(
    string Name,
    string DestinationPath,
    string Version,
    int Port,
    int MinimumMemoryMb,
    int MaximumMemoryMb,
    MinecraftServerSettings Settings,
    bool EulaAccepted,
    bool CreateFirewallRule = true,
    bool InstallJavaIfMissing = false,
    string? JavaExecutablePath = null,
    string? PreferredAdapterId = null,
    bool AutoStart = false,
    bool AutoRestart = true);

public sealed record MinecraftInstallResult(
    Guid ServerId,
    string RootPath,
    string Version,
    string JarSha1,
    DateTimeOffset InstalledAtUtc,
    string? JavaExecutablePath = null,
    int JavaMajorVersion = 0,
    string? LocalAddress = null,
    bool Started = false,
    bool PortReady = false);

public sealed record MinecraftServerMetadata(
    string Product,
    string Version,
    string JarFile,
    string JarSha1,
    int RequiredJavaMajor,
    DateTimeOffset InstalledAtUtc);

public sealed record MinecraftCreationStartResponse(Guid OperationId);

public sealed record MinecraftCreationProgress(
    Guid OperationId,
    MinecraftCreationStage Stage,
    string StageLabel,
    string Message,
    int Percent,
    bool IsComplete,
    bool Succeeded,
    MinecraftInstallResult? Result = null,
    StartupFailureDetails? Failure = null,
    DateTimeOffset? UpdatedAtUtc = null);

public sealed record MinecraftCreationPlan(
    MinecraftVersionDescriptor Version,
    string DestinationPath,
    int Port,
    bool PortAvailable,
    string? LocalAddress,
    long TotalMemoryBytes,
    long AvailableMemoryBytes,
    long RecommendedMinimumMemoryBytes,
    long RecommendedMaximumMemoryBytes,
    long WindowsReserveBytes,
    JavaRuntimeDescriptor? Java,
    IReadOnlyList<string> Warnings);

public sealed record MinecraftPlanRequest(
    string DestinationPath,
    string Version,
    int Port,
    int MinimumMemoryMb,
    int MaximumMemoryMb,
    string? JavaExecutablePath = null,
    string? PreferredAdapterId = null);

public sealed record JavaRuntimeDescriptor(
    string? ExecutablePath,
    int InstalledMajorVersion,
    int RequiredMajorVersion,
    string? VersionText,
    bool IsCompatible,
    bool CanInstallAutomatically);
