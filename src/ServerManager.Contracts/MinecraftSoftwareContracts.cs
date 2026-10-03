namespace ServerManager.Contracts;

public sealed record MinecraftSoftwareOption(
    ServerPlatform Platform, string MinecraftVersion, bool Available, string Code, string Message,
    bool RequiresWorldReset = false);

public sealed record MinecraftSoftwareStatus(
    Guid ServerId, ServerPlatform CurrentPlatform, string? MinecraftVersion,
    string LevelName, IReadOnlyList<MinecraftSoftwareOption> Options);

public sealed record MinecraftSoftwareMigrationRequest(ServerPlatform TargetPlatform, string MinecraftVersion,
    bool ConfirmWorldDeletion = false);

public sealed record MinecraftSoftwareMigrationResult(
    bool Success, string Code, string Message, Guid? BackupId = null,
    bool RuntimeRolledBack = false, bool StartupVerified = false);
