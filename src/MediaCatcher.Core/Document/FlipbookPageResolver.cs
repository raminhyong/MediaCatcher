using MediaCatcher.Contracts;
using MediaCatcher.Core.Models;
using MediaCatcher.Core.Utils;

namespace MediaCatcher.Core.Document;

public interface IDocumentResolver
{
    Task<DocumentResolution> ResolveAsync(string url, CancellationToken cancellationToken = default);
}

public sealed class FlipbookPageResolver : IDocumentResolver
{
    private readonly HttpClient _client;

    public FlipbookPageResolver(HttpClient? client = null)
    {
        _client = client ?? new HttpClient();
    }

    public async Task<DocumentResolution> ResolveAsync(string url, CancellationToken cancellationToken = default)
    {
        if (!UrlPolicy.TryNormalize(url, out var pageUri, out var error))
        {
            throw new DownloadException(ErrorCodes.UnsupportedUrl, error ?? "Unsupported URL.");
        }

        var html = await GetStringAsync(pageUri, referer: null, cancellationToken).ConfigureAwait(false);

        var configUrl = FlipbookParser.FindConfigScriptUrl(html, pageUri);
        string? configJson = null;
        Uri? configUri = null;
        if (!string.IsNullOrWhiteSpace(configUrl) && Uri.TryCreate(configUrl, UriKind.Absolute, out configUri))
        {
            try
            {
                var configScript = await GetStringAsync(configUri, pageUri.AbsoluteUri, cancellationToken).ConfigureAwait(false);
                configJson = FlipbookParser.ExtractJsonObject(configScript);
            }
            catch (HttpRequestException)
            {
                configJson = null;
            }
        }

        if (configJson is null)
        {
            configJson = FlipbookParser.ExtractJsonObject(html);
        }

        if (configJson is null)
        {
            throw new DownloadException(
                ErrorCodes.ExtractionFailed,
                "No flipbook configuration was found on this page.");
        }

        var baseUri = configUri ?? pageUri;
        var pageUrls = FlipbookParser.ExtractPageUrls(configJson, baseUri);
        var title = FlipbookParser.ExtractTitle(configJson);

        var pages = pageUrls
            .Select((pageUrl, index) => new DocumentPage
            {
                Url = pageUrl,
                Index = index,
                Referer = pageUri.AbsoluteUri
            })
            .ToList();

        return new DocumentResolution(title, pages);
    }

    private async Task<string> GetStringAsync(Uri uri, string? referer, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("Accept", "*/*");
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
