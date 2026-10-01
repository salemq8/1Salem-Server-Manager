using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace ServerManager.Connect.App.Updates;

public enum UpdateDecision
{
    UpToDate,
    UpdateAvailable,

    /// <summary>The release offered is older than this app: never installed (no downgrades).</summary>
    OlderRejected
}

public static class ConnectUpdatePolicy
{
    public static UpdateDecision Decide(ConnectBuild current, ConnectBuild offered)
    {
        var order = offered.CompareTo(current);
        return order > 0 ? UpdateDecision.UpdateAvailable :
            order == 0 ? UpdateDecision.UpToDate :
            UpdateDecision.OlderRejected;
    }
}

public enum UpdateDownloadFailure
{
    Unreachable,
    NotPublished,
    Incomplete,
    TooLarge,
    HashMismatch,
    Refused
}

/// <summary>A download that must not be used. The partial file is already gone when this is thrown.</summary>
public sealed class UpdateDownloadException(UpdateDownloadFailure failure, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public UpdateDownloadFailure Failure { get; } = failure;
}

/// <summary>What the updater needs from the network; faked in tests.</summary>
public interface IUpdateHttp
{
    /// <summary>The manifest of the newest release, or null when that release publishes none.</summary>
    Task<byte[]?> GetManifestAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Downloads a release file to <paramref name="destination"/> and returns only once its size
    /// and SHA-256 match the manifest. Nothing is left at <paramref name="destination"/> otherwise.
    /// </summary>
    Task DownloadAsync(ConnectUpdateFile file, string destination, IProgress<double>? progress, CancellationToken cancellationToken);
}

/// <summary>
/// HTTPS to GitHub only. Redirects are followed by hand so every hop is checked against
/// <see cref="ConnectUpdateSource.IsAllowedHost"/>; a download is streamed to a partial file while
/// it is hashed, held to the manifest's exact size, and moved into place only when the hash
/// matches. A failed or interrupted download leaves nothing behind.
/// </summary>
public sealed class GitHubUpdateHttp : IUpdateHttp, IDisposable
{
    private const int MaxRedirects = 5;
    private static readonly TimeSpan ManifestTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(30);

    private readonly HttpClient _http;
    private readonly Uri _manifestUrl;

    public GitHubUpdateHttp(ConnectBuild current, HttpMessageHandler? handler = null, Uri? manifestUrl = null)
    {
        _manifestUrl = manifestUrl ?? ConnectUpdateSource.LatestManifestUrl;
        if (!ConnectUpdateSource.IsAllowedHost(_manifestUrl))
        {
            throw new ArgumentException("Updates come only from the official GitHub releases over HTTPS.", nameof(manifestUrl));
        }

        _http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        // GitHub refuses requests without a User-Agent.
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(
            "1SalemConnect",
            string.Create(CultureInfo.InvariantCulture, $"{current.ProductVersion}.{current.BuildRevision}")));
    }

    public async Task<byte[]?> GetManifestAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ManifestTimeout);
        try
        {
            using var response = await SendAsync(_manifestUrl, deadline.Token);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            EnsureSuccess(response);
            if (response.Content.Headers.ContentLength > ConnectUpdateManifestReader.MaxBytes)
            {
                throw new UpdateDownloadException(UpdateDownloadFailure.TooLarge, "The update information is too large.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(chunk, deadline.Token)) > 0)
            {
                if (buffer.Length + read > ConnectUpdateManifestReader.MaxBytes)
                {
                    throw new UpdateDownloadException(UpdateDownloadFailure.TooLarge, "The update information is too large.");
                }

                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }
        catch (Exception exception) when (IsNetworkFailure(exception, cancellationToken))
        {
            throw new UpdateDownloadException(UpdateDownloadFailure.Unreachable, "GitHub could not be reached.", exception);
        }
    }

    public async Task DownloadAsync(ConnectUpdateFile file, string destination, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        var partial = destination + ".partial";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        TryDelete(partial);
        TryDelete(destination);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(DownloadTimeout);
        try
        {
            using var response = await SendAsync(file.Url, deadline.Token);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new UpdateDownloadException(UpdateDownloadFailure.NotPublished, $"{file.FileName} is not in the release.");
            }

            EnsureSuccess(response);
            if (response.Content.Headers.ContentLength is { } length && length != file.Size)
            {
                throw new UpdateDownloadException(
                    length > file.Size ? UpdateDownloadFailure.TooLarge : UpdateDownloadFailure.Incomplete,
                    $"{file.FileName} is not the size the release lists.");
            }

            byte[] hash;
            long received = 0;
            using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
                await using (var input = await response.Content.ReadAsStreamAsync(deadline.Token))
                {
                    var buffer = new byte[81920];
                    int read;
                    while ((read = await input.ReadAsync(buffer, deadline.Token)) > 0)
                    {
                        received += read;
                        if (received > file.Size)
                        {
                            throw new UpdateDownloadException(UpdateDownloadFailure.TooLarge, $"{file.FileName} is larger than the release lists.");
                        }

                        sha.AppendData(buffer, 0, read);
                        await output.WriteAsync(buffer.AsMemory(0, read), deadline.Token);
                        progress?.Report((double)received / file.Size);
                    }

                    await output.FlushAsync(deadline.Token);
                }

                hash = sha.GetHashAndReset();
            }

            if (received != file.Size)
            {
                throw new UpdateDownloadException(UpdateDownloadFailure.Incomplete, $"The download of {file.FileName} was incomplete.");
            }

            if (!CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(file.Sha256)))
            {
                throw new UpdateDownloadException(UpdateDownloadFailure.HashMismatch, $"{file.FileName} does not match the SHA-256 the release lists.");
            }

            File.Move(partial, destination, overwrite: true);
        }
        catch (Exception exception) when (IsNetworkFailure(exception, cancellationToken))
        {
            TryDelete(partial);
            throw new UpdateDownloadException(UpdateDownloadFailure.Unreachable, "The download was interrupted.", exception);
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
    }

    public void Dispose() => _http.Dispose();

    /// <summary>GET with redirects followed by hand, each hop checked before it is contacted.</summary>
    private async Task<HttpResponseMessage> SendAsync(Uri address, CancellationToken cancellationToken)
    {
        var current = address;
        for (var hop = 0; ; hop++)
        {
            if (!ConnectUpdateSource.IsAllowedHost(current))
            {
                throw new UpdateDownloadException(UpdateDownloadFailure.Refused, "The download was redirected away from GitHub.");
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!IsRedirect(response.StatusCode))
            {
                return response;
            }

            var location = response.Headers.Location;
            response.Dispose();
            if (location is null || hop >= MaxRedirects)
            {
                throw new UpdateDownloadException(UpdateDownloadFailure.Refused, "GitHub answered with an unusable redirect.");
            }

            current = location.IsAbsoluteUri ? location : new Uri(current, location);
        }
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or
            HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new UpdateDownloadException(
                UpdateDownloadFailure.Unreachable,
                string.Create(CultureInfo.InvariantCulture, $"GitHub answered {(int)response.StatusCode}."));
        }
    }

    private static bool IsNetworkFailure(Exception exception, CancellationToken cancellationToken) =>
        exception is HttpRequestException or IOException ||
        (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
