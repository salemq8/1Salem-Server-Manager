using System.Net;
using System.Text.Json;
using ServerManager.Contracts;
using ServerManager.Core.Content;

namespace ServerManager.Infrastructure.Content;

/// <summary>
/// The shared request and download plumbing for content providers: one place that turns
/// provider failures into codes the UI can phrase, and one place that enforces the download
/// rules so neither provider can forget them.
/// </summary>
internal static class ContentHttp
{
    public static async Task<JsonDocument> GetJsonAsync(
        HttpClient client,
        ContentProviderId provider,
        string requestUri,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await client.GetAsync(requestUri, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw Offline(provider, exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ContentProviderException(
                provider,
                ContentProviderException.UnavailableCode,
                "The provider did not respond in time.",
                exception);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw Map(provider, response);
            }

            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            }
            catch (JsonException exception)
            {
                throw new ContentProviderException(
                    provider,
                    ContentProviderException.SchemaCode,
                    "The provider returned a response this version does not understand.",
                    exception);
            }
        }
    }

    public static async Task<JsonDocument> PostJsonAsync(
        HttpClient client,
        ContentProviderId provider,
        string requestUri,
        HttpContent content,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await client.PostAsync(requestUri, content, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw Offline(provider, exception);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw Map(provider, response);
            }

            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            }
            catch (JsonException exception)
            {
                throw new ContentProviderException(
                    provider,
                    ContentProviderException.SchemaCode,
                    "The provider returned a response this version does not understand.",
                    exception);
            }
        }
    }

    public static ContentProviderException Map(
        ContentProviderId provider,
        HttpResponseMessage response) =>
        response.StatusCode switch
        {
            HttpStatusCode.NotFound => new ContentProviderException(
                provider,
                ContentProviderException.NotFoundCode,
                "That project is no longer listed by the provider."),
            HttpStatusCode.Gone => new ContentProviderException(
                provider,
                ContentProviderException.ApiRetiredCode,
                "The provider retired the interface this version uses. Update 1Salem Server Manager."),
            HttpStatusCode.TooManyRequests => new ContentProviderException(
                provider,
                ContentProviderException.RateLimitedCode,
                "The provider asked us to slow down.")
            {
                RetryAfter = response.Headers.RetryAfter?.Delta ??
                             (response.Headers.RetryAfter?.Date is { } date
                                 ? date - DateTimeOffset.UtcNow
                                 : null)
            },
            _ => new ContentProviderException(
                provider,
                ContentProviderException.UnavailableCode,
                "The provider is not answering correctly right now.")
        };

    private static ContentProviderException Offline(ContentProviderId provider, Exception inner) =>
        new(
            provider,
            ContentProviderException.OfflineCode,
            "The provider could not be reached. Check the internet connection.",
            inner);

    /// <summary>
    /// Transfers a provider-hosted file to a staging path. Redirects are followed by hand so
    /// every hop is checked, the ceiling is enforced while the bytes arrive rather than
    /// afterwards, and a cancelled or failed transfer leaves no staged file behind.
    /// </summary>
    public static async Task DownloadAsync(
        HttpClient client,
        ContentProviderId provider,
        Uri url,
        string stagingFilePath,
        long expectedSizeBytes,
        string fileName,
        IProgress<ContentInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!ContentDownloadPolicy.IsAllowedDownloadUrl(provider, url))
        {
            throw new ContentProviderException(
                provider,
                ContentProviderException.SchemaCode,
                "The provider offered a download address outside its own site.");
        }

        if (!ContentDownloadPolicy.IsAcceptableSize(expectedSizeBytes))
        {
            throw new ContentProviderException(
                provider,
                ContentProviderException.SchemaCode,
                "The provider reported a file larger than this app will download.");
        }

        var current = url;
        HttpResponseMessage? response = null;
        try
        {
            for (var hop = 0; hop <= ContentDownloadPolicy.MaximumRedirects; hop++)
            {
                response?.Dispose();
                try
                {
                    response = await client.GetAsync(
                        current,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken);
                }
                catch (HttpRequestException exception)
                {
                    throw Offline(provider, exception);
                }

                if (response.StatusCode is HttpStatusCode.Moved or HttpStatusCode.Found or
                    HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or
                    HttpStatusCode.PermanentRedirect)
                {
                    var location = response.Headers.Location;
                    if (location is null)
                    {
                        throw Map(provider, response);
                    }

                    var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                    if (!ContentDownloadPolicy.IsAllowedDownloadUrl(provider, next))
                    {
                        throw new ContentProviderException(
                            provider,
                            ContentProviderException.SchemaCode,
                            "The download was redirected off the provider's own site.");
                    }

                    current = next;
                    continue;
                }

                break;
            }

            if (response is null || !response.IsSuccessStatusCode)
            {
                throw response is null
                    ? new ContentProviderException(
                        provider,
                        ContentProviderException.UnavailableCode,
                        "The download did not start.")
                    : Map(provider, response);
            }

            var declared = response.Content.Headers.ContentLength;
            if (declared is { } length && !ContentDownloadPolicy.IsAcceptableSize(length))
            {
                throw new ContentProviderException(
                    provider,
                    ContentProviderException.SchemaCode,
                    "The download is larger than this app will accept.");
            }

            var total = declared ?? (expectedSizeBytes > 0 ? expectedSizeBytes : (long?)null);
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var destination = new FileStream(
                stagingFilePath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                81920,
                useAsync: true);

            var buffer = new byte[81920];
            long received = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                received += read;
                if (received > ContentDownloadPolicy.MaximumFileBytes)
                {
                    throw new ContentProviderException(
                        provider,
                        ContentProviderException.SchemaCode,
                        "The download exceeded the size this app will accept.");
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                progress?.Report(new ContentInstallProgress(
                    ContentInstallStage.Downloading,
                    "Downloading",
                    fileName,
                    received,
                    total));
            }

            if (received == 0)
            {
                throw new ContentProviderException(
                    provider,
                    ContentProviderException.SchemaCode,
                    "The provider returned an empty file.");
            }
        }
        catch
        {
            TryDelete(stagingFilePath);
            throw;
        }
        finally
        {
            response?.Dispose();
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
