using MediaCatcher.Contracts;
using MediaCatcher.Core.Models;
using MediaCatcher.Core.Utils;

namespace MediaCatcher.Core.Document;

public interface IDocumentResolver
{
    Task<DocumentResolution> ResolveAsync(string url, string? userAgent = null, CancellationToken cancellationToken = default);
}

public sealed class FlipbookPageResolver : IDocumentResolver
{
    private const string DefaultUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/153.0.0.0 Safari/537.36";

    private readonly HttpClient _client;

    public FlipbookPageResolver(HttpClient? client = null)
    {
        _client = client ?? new HttpClient();
    }

    public async Task<DocumentResolution> ResolveAsync(string url, string? userAgent = null, CancellationToken cancellationToken = default)
    {
        if (!UrlPolicy.TryNormalize(url, out var pageUri, out var error))
        {
            throw new DownloadException(ErrorCodes.UnsupportedUrl, error ?? "Unsupported URL.");
        }

        var effectiveUserAgent = string.IsNullOrWhiteSpace(userAgent) ? DefaultUserAgent : userAgent;
        var html = await GetStringAsync(pageUri, referer: null, effectiveUserAgent, cancellationToken).ConfigureAwait(false);

        var (configJson, configUri) = await TryExtractConfigAsync(html, pageUri, effectiveUserAgent, cancellationToken).ConfigureAwait(false);
        var effectivePageUri = pageUri;

        if (configJson is null)
        {
            var embeddedUrl = FlipbookParser.FindEmbeddedBookUrl(html);
            if (!string.IsNullOrWhiteSpace(embeddedUrl) &&
                Uri.TryCreate(embeddedUrl, UriKind.Absolute, out var embeddedUri) &&
                !string.Equals(embeddedUri.AbsoluteUri, pageUri.AbsoluteUri, StringComparison.OrdinalIgnoreCase))
            {
                var embeddedHtml = await GetStringAsync(embeddedUri, pageUri.AbsoluteUri, effectiveUserAgent, cancellationToken).ConfigureAwait(false);
                (configJson, configUri) = await TryExtractConfigAsync(embeddedHtml, embeddedUri, effectiveUserAgent, cancellationToken).ConfigureAwait(false);
                effectivePageUri = embeddedUri;
            }
        }

        if (configJson is null)
        {
            throw new DownloadException(
                ErrorCodes.ExtractionFailed,
                "No flipbook configuration was found on this page.");
        }

        var baseUri = effectivePageUri;
        var pageUrls = FlipbookParser.ExtractPageUrls(configJson, baseUri);
        var title = FlipbookParser.ExtractTitle(configJson);

        var pages = pageUrls
            .Select((pageUrl, index) => new DocumentPage
            {
                Url = pageUrl,
                Index = index,
                Referer = effectivePageUri.AbsoluteUri
            })
            .ToList();

        return new DocumentResolution(title, pages);
    }

    private async Task<(string? ConfigJson, Uri? ConfigUri)> TryExtractConfigAsync(
        string html,
        Uri pageUri,
        string userAgent,
        CancellationToken cancellationToken)
    {
        var configUrl = FlipbookParser.FindConfigScriptUrl(html, pageUri);
        if (!string.IsNullOrWhiteSpace(configUrl) && Uri.TryCreate(configUrl, UriKind.Absolute, out var configUri))
        {
            try
            {
                var configScript = await GetStringAsync(configUri, pageUri.AbsoluteUri, userAgent, cancellationToken).ConfigureAwait(false);
                var configJson = FlipbookParser.ExtractJsonObject(configScript);
                if (configJson is not null)
                {
                    return (configJson, configUri);
                }
            }
            catch (HttpRequestException)
            {
                // Fall through to inline JSON extraction.
            }
        }

        return (FlipbookParser.ExtractJsonObject(html), null);
    }

    private async Task<string> GetStringAsync(Uri uri, string? referer, string? userAgent, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("Accept", "*/*");
        if (!string.IsNullOrWhiteSpace(userAgent))
        {
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        }
        if (!string.IsNullOrWhiteSpace(referer))
        {
            request.Headers.TryAddWithoutValidation("Referer", referer);
        }

        using var response = await _client
            .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }
}
