namespace ServerManager.Contracts;

public sealed record ServerStopRequest(bool Force = false);

public sealed record ServerStartRequest(
    bool OverrideUnsafeBudget = false,
    string? Confirmation = null);

public sealed record ConsoleCommandRequest(string Command);

public sealed record MinecraftMemoryUpdateRequest(
    int MinimumMemoryMb,
    int MaximumMemoryMb,
    bool ApplyAndRestart);

public sealed record ServerAutomationUpdateRequest(
    bool AutoStart,
    bool AutoRestart);

public sealed record ServerSettingsUpdateRequest(
    string Name,
    int Port,
    int MaxPlayers,
    string Motd,
    string GameMode,
    string Difficulty,
    bool WhitelistEnabled,
    int ViewDistance,
    int SimulationDistance,
    bool OnlineMode,
    bool Hardcore,
    bool Pvp,
    string? JavaExecutablePath = null,
    bool ApplyAndRestart = false);

public sealed record NetworkPreferenceRequest(string? AdapterId);

public sealed record PortTestRequest(int Port, string Protocol = "TCP");

public sealed record PortTestResponse(
    int Port,
    string Protocol,
    bool IsAvailable,
    string Message,
    string? OwningProcess = null);

public sealed record MinecraftConfigurationResponse(
    Guid ServerId,
    string Name,
    int Port,
    int MinimumMemoryMb,
    int MaximumMemoryMb,
    string? JavaExecutablePath,
    int MaxPlayers,
    string Motd,
    string GameMode,
    string Difficulty,
    bool WhitelistEnabled,
    int ViewDistance,
    int SimulationDistance,
    bool OnlineMode,
    bool Hardcore,
    bool Pvp,
    bool AutoStart,
    bool AutoRestart,
    string? PreferredAdapterId);

public sealed record MinecraftPlayersSnapshot(
    IReadOnlyList<string> OnlinePlayers,
    IReadOnlyList<string> WhitelistedPlayers,
    IReadOnlyList<string> Operators,
    IReadOnlyList<string> BannedPlayers,
    IReadOnlyList<string> BannedIps,
    DateTimeOffset CapturedAtUtc);
