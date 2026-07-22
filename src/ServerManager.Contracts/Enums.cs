namespace ServerManager.Contracts;

public enum GameType
{
    Minecraft = 1,
    Palworld = 2
}

public enum ServerState
{
    NotInstalled = 0,
    Stopped = 1,
    Starting = 2,
    Running = 3,
    Stopping = 4,
    Restarting = 5,
    Updating = 6,
    BackingUp = 7,
    Restoring = 8,
    Crashed = 9,
    Error = 10
}

public enum MinecraftCreationStage
{
    Queued = 0,
    ValidatingDestination = 1,
    ResolvingVersion = 2,
    DetectingJava = 3,
    InstallingJava = 4,
    DownloadingServer = 5,
    VerifyingDownload = 6,
    CreatingFolders = 7,
    WritingProperties = 8,
    WritingMemoryArguments = 9,
    AcceptingEula = 10,
    CreatingFirewallRule = 11,
    RegisteringServer = 12,
    StartingFirstLaunch = 13,
    WaitingForStartup = 14,
    VerifyingPort = 15,
    Completed = 16,
    Failed = 17
}

public enum InstallMode
{
    AllInOne = 1,
    AgentOnly = 2,
    ClientOnly = 3
}

public enum ResourceMode
{
    Balanced = 1,
    MinecraftPriority = 2,
    PalworldPriority = 3,
    OneGameAtATime = 4,
    Custom = 5,
    Safe = 6,
    Performance = 7
}

public enum ImportMode
{
    Copy = 1,
    Move = 2,
    ManageInPlace = 3
}

public enum BackupStatus
{
    Pending = 0,
    Completed = 1,
    Failed = 2,
    Corrupt = 3
}
