using ServerManager.Contracts;

namespace ServerManager.Core.Content;

/// <summary>
/// One external site the Content Hub can browse. Everything above this interface works in
/// normalized models, so no page and no install step knows whether an item came from Hangar
/// or Modrinth.
/// </summary>
public interface IContentProvider
{
    ContentProviderId Id { get; }

    /// <summary>
    /// Whether this provider can serve this server at all. A provider that cannot prove
    /// compatibility with the platform says no here rather than offering files that may not
    /// load.
    /// </summary>
    bool CanServe(ServerContentProfile profile);

    /// <summary>
    /// Whether this provider can serve this content type to this server. Hangar has plugins
    /// only, so anything else is answered here rather than by returning empty results.
    /// </summary>
    bool CanServe(ServerContentProfile profile, ContentKind kind) =>
        kind == ContentKind.Plugin && CanServe(profile);

    Task<ContentSearchResult> SearchAsync(
        ContentSearchRequest request,
        ServerContentProfile profile,
        CancellationToken cancellationToken = default);

    Task<ContentProject?> GetProjectAsync(
        string projectId,
        ServerContentProfile profile,
        ContentKind kind = ContentKind.Plugin,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ContentVersion>> GetVersionsAsync(
        string projectId,
        ServerContentProfile profile,
        ContentKind kind = ContentKind.Plugin,
        CancellationToken cancellationToken = default);

    /// <summary>The newest release that fits this exact server, or null when none does.</summary>
    Task<ContentVersion?> ResolveCompatibleVersionAsync(
        string projectId,
        ServerContentProfile profile,
        bool allowPrerelease = false,
        ContentKind kind = ContentKind.Plugin,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks the provider to identify a file already on disk by its hashes. Only a confident
    /// match is returned; a name that merely looks similar is never treated as identity.
    /// </summary>
    Task<ContentIdentification?> IdentifyAsync(
        ContentFileDigests digests,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads a version's file to a staging path the manager chose. Implementations only
    /// accept a URL that came from this provider's own response.
    /// </summary>
    Task DownloadAsync(
        ContentVersion version,
        string stagingFilePath,
        IProgress<ContentInstallProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A provider problem in a form the UI can phrase for a person. Raw exception text never
/// reaches the screen.
/// </summary>
public sealed class ContentProviderException(
    ContentProviderId provider,
    string errorCode,
    string message,
    Exception? innerException = null)
    : Exception(message, innerException)
{
    /// <summary>The network never reached the provider.</summary>
    public const string OfflineCode = "Offline";

    /// <summary>The provider asked us to slow down (HTTP 429).</summary>
    public const string RateLimitedCode = "RateLimited";

    /// <summary>The provider is reachable but failing (HTTP 5xx).</summary>
    public const string UnavailableCode = "ProviderUnavailable";

    /// <summary>The API version we use has been retired (HTTP 410). The app needs updating.</summary>
    public const string ApiRetiredCode = "ProviderApiRetired";

    public const string NotFoundCode = "NotFound";

    /// <summary>The response parsed, but not into anything we recognise.</summary>
    public const string SchemaCode = "ProviderSchema";

    public ContentProviderId Provider { get; } = provider;

    public string ErrorCode { get; } = errorCode;

    public TimeSpan? RetryAfter { get; init; }
}
