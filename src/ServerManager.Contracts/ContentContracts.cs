namespace ServerManager.Contracts;

/// <summary>
/// The external sites the Content Hub browses. 1Salem Server Manager hosts nothing itself:
/// every project, file and hash in these contracts came from one of these providers.
/// </summary>
public enum ContentProviderId
{
    Modrinth = 1,
    Hangar = 2
}

/// <summary>
/// What a project installs as. Only plugins ship in this milestone; the rest exist so the
/// models, storage and install pipeline do not have to be redesigned to add them later.
/// </summary>
public enum ContentKind
{
    Plugin = 1,
    Modpack = 2,
    DataPack = 3,
    ResourcePack = 4
}

/// <summary>The server software, which decides whether plugins are possible at all.</summary>
public enum ServerPlatform
{
    Unknown = 0,
    Vanilla = 1,
    Paper = 2,
    Purpur = 3,
    Spigot = 4,
    Bukkit = 5,
    Folia = 6
}

public enum ContentReleaseChannel
{
    Release = 1,
    Beta = 2,
    Alpha = 3
}

public enum ContentDependencyKind
{
    Required = 1,
    Optional = 2,
    Incompatible = 3,
    Embedded = 4
}

public enum ContentSortOrder
{
    Relevance = 1,
    Downloads = 2,
    Updated = 3,
    Newest = 4
}

/// <summary>How an installed file stands relative to the server and the provider.</summary>
public enum InstalledContentState
{
    UpToDate = 1,
    UpdateAvailable = 2,
    RestartRequired = 3,
    InstalledManually = 4,
    UnknownVersion = 5,
    MissingFile = 6,
    ModifiedLocally = 7
}

/// <summary>
/// The install pipeline's steps, in order. The UI shows these; it never invents a percentage
/// for a step that has no measurable progress.
/// </summary>
public enum ContentInstallStage
{
    CheckingCompatibility = 1,
    ResolvingDependencies = 2,
    PreparingRestorePoint = 3,
    Downloading = 4,
    VerifyingHash = 5,
    ValidatingArchive = 6,
    Installing = 7,
    RecordingMetadata = 8,
    Completed = 9,
    Failed = 10
}

/// <summary>
/// What a server can accept, derived only from the validated local installation. The install
/// target is built from <see cref="PluginsDirectory"/>, never from anything a provider says.
/// </summary>
public sealed record ServerContentProfile(
    Guid ServerId,
    GameType Game,
    ServerPlatform Platform,
    string? MinecraftVersion,
    string? PlatformVersion,
    string ServerRoot,
    string PluginsDirectory,
    bool SupportsPlugins,
    bool IsRunning,
    string? UnsupportedReason = null);

public sealed record ContentSearchRequest(
    string? Query = null,
    ContentSortOrder Sort = ContentSortOrder.Relevance,
    bool CompatibleOnly = true,
    ContentProviderId? Provider = null,
    int Offset = 0,
    int Limit = 20,
    ContentKind Kind = ContentKind.Plugin);

/// <summary>
/// A project normalized across providers. A field the provider did not return stays null: the
/// UI shows nothing or "Unknown" rather than a value we made up.
/// </summary>
public sealed record ContentProject(
    ContentProviderId Provider,
    string ProjectId,
    string Slug,
    string Name,
    string? Summary,
    string? Author,
    Uri? IconUrl,
    Uri? ProjectUrl,
    long? Downloads,
    IReadOnlyList<string> Platforms,
    IReadOnlyList<string> GameVersions,
    string? License = null,
    ContentKind Kind = ContentKind.Plugin,
    string? Description = null,
    bool IsCompatible = false,
    string? CompatibilitySummary = null);

/// <summary>
/// One downloadable file. <see cref="Sha512"/> and <see cref="Sha1"/> come from Modrinth and
/// <see cref="Sha256"/> from Hangar; whichever is present is what gets verified.
/// </summary>
public sealed record ContentFile(
    string FileName,
    Uri Url,
    long SizeBytes,
    string? Sha512 = null,
    string? Sha256 = null,
    string? Sha1 = null,
    bool IsPrimary = true);

public sealed record ContentDependency(
    ContentDependencyKind Kind,
    ContentProviderId Provider,
    string? ProjectId,
    string? VersionId = null,
    string? Name = null,
    Uri? ExternalUrl = null);

/// <summary>
/// A single release. <see cref="File"/> is null when the provider only offers an
/// <see cref="ExternalDownloadUrl"/>, which the manager will not download on the person's
/// behalf because the provider neither hosts nor hashes it.
/// </summary>
public sealed record ContentVersion(
    ContentProviderId Provider,
    string ProjectId,
    string VersionId,
    string VersionNumber,
    ContentReleaseChannel Channel,
    DateTimeOffset? PublishedAtUtc,
    IReadOnlyList<string> Platforms,
    IReadOnlyList<string> GameVersions,
    ContentFile? File,
    IReadOnlyList<ContentDependency> Dependencies,
    Uri? ExternalDownloadUrl = null,
    string? Changelog = null,
    string? DisplayName = null);

public sealed record ContentSearchResult(
    IReadOnlyList<ContentProject> Projects,
    int Offset,
    int Limit,
    int TotalHits,
    IReadOnlyList<string> ProviderErrors);

/// <summary>What the manager recorded when it installed a file, plus how it stands now.</summary>
public sealed record InstalledContent(
    Guid ServerId,
    string FileName,
    InstalledContentState State,
    ContentKind Kind = ContentKind.Plugin,
    ContentProviderId? Provider = null,
    string? ProjectId = null,
    string? VersionId = null,
    string? ProjectName = null,
    string? InstalledVersion = null,
    string? MinecraftVersionAtInstall = null,
    ServerPlatform PlatformAtInstall = ServerPlatform.Unknown,
    Uri? ProjectUrl = null,
    string? ProviderSha512 = null,
    string? ProviderSha256 = null,
    string? LocalSha256 = null,
    DateTimeOffset? InstalledAtUtc = null,
    bool RestartRequired = false,
    string? PreviousVersionId = null,
    string? PreviousFileName = null,
    string? AvailableVersionId = null,
    string? AvailableVersionNumber = null,
    long SizeBytes = 0,
    bool ManagedByManager = false);

/// <summary>
/// The digests of a local file, used to ask a provider what it is. Modrinth looks up SHA-1
/// and Hangar looks up SHA-256, so both travel together.
/// </summary>
public sealed record ContentFileDigests(string Sha1, string Sha256, string? Sha512 = null);

/// <summary>
/// A provider's answer to "what is this file?", used for manually added plugins. Hangar's
/// hash lookup answers with the project only, so <see cref="VersionId"/> stays null there
/// rather than being invented.
/// </summary>
public sealed record ContentIdentification(
    ContentProviderId Provider,
    string ProjectId,
    string? ProjectName,
    string? VersionId,
    string? VersionNumber,
    Uri? ProjectUrl);

public sealed record ContentInstallRequest(
    Guid ServerId,
    ContentProviderId Provider,
    string ProjectId,
    string? VersionId = null,
    bool IncludeRequiredDependencies = true,
    ContentKind Kind = ContentKind.Plugin);

/// <summary>A project plus the releases that fit this server, for the detail view.</summary>
public sealed record ContentProjectDetail(
    ContentProject Project,
    ContentVersion? LatestCompatible,
    IReadOnlyList<ContentVersion> Versions);

/// <summary>Names one installed file for update, rollback and uninstall requests.</summary>
public sealed record ContentFileRequest(string FileName, string? VersionId = null);

public sealed record ContentInstallPlanItem(
    ContentProviderId Provider,
    string ProjectId,
    string ProjectName,
    string VersionId,
    string VersionNumber,
    string FileName,
    long SizeBytes,
    bool IsDependency);

/// <summary>
/// What an install would do, shown before anything is downloaded. When
/// <see cref="Blocked"/> is set nothing is installed at all: a partial install is never
/// presented as success.
/// </summary>
public sealed record ContentInstallPlan(
    IReadOnlyList<ContentInstallPlanItem> Items,
    IReadOnlyList<string> Warnings,
    bool Blocked = false,
    string? BlockedReason = null);

public sealed record ContentInstallProgress(
    ContentInstallStage Stage,
    string Message,
    string? FileName = null,
    long? BytesReceived = null,
    long? TotalBytes = null);

public sealed record ContentOperationResult(
    bool Success,
    string? ErrorCode = null,
    string? Message = null,
    bool RestartRequired = false,
    IReadOnlyList<InstalledContent>? Installed = null)
{
    public static ContentOperationResult Ok(
        bool restartRequired = false,
        IReadOnlyList<InstalledContent>? installed = null) =>
        new(true, RestartRequired: restartRequired, Installed: installed);

    public static ContentOperationResult Fail(string errorCode, string message) =>
        new(false, errorCode, message);
}
