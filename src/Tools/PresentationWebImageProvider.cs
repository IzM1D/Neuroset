using System.Net;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Neuroset.Application;

namespace Neuroset.Tools;

/// <summary>Downloads actual public assets; source metadata travels with the embedded PNG.</summary>
public sealed class PresentationWebImageProvider(HttpClient httpClient, IPresentationImageDecoder decoder,
    ISearchProvider search, Func<Uri, CancellationToken, Task>? validateUrl = null, TimeSpan? minimumCommonsInterval = null) : IPresentationWebImageProvider, IDisposable
{
    private const int MaximumBytes = 32 * 1024 * 1024;
    private static readonly string[] FileExtensions = ["svg", "png", "jpg"];
    private static readonly string[] MetadataFields = ["Artist", "Credit", "LicenseShortName", "LicenseUrl"];
    private static readonly string[] StockHosts = ["shutterstock.com", "gettyimages.com", "istockphoto.com", "alamy.com", "depositphotos.com", "stock.adobe.com"];
    private readonly ConcurrentDictionary<string, DateTimeOffset> _unavailableUrls = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _commonsGate = new(1, 1);
    private DateTimeOffset _nextCommonsRequest;
    private static readonly HashSet<string> GenericWords = new(["file", "of", "the", "and", "in", "a", "an", "coat", "arms", "flag", "map", "photo", "photograph", "old", "town", "city", "view"], StringComparer.OrdinalIgnoreCase);

    public async Task<PresentationWebImage> ResolveAsync(PresentationWebImageRequest request, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(75));
        try { return await ResolveCoreAsync(request, timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Internet image lookup timed out for '{request.Query}'. The pending presentation can be resumed.");
        }
    }

    private async Task<PresentationWebImage> ResolveCoreAsync(PresentationWebImageRequest request, CancellationToken token)
    {
        var failures = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var variants = PresentationImageSearch.Queries(request.Query);
        var queries = variants.Where(query => query.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 6)
            .Concat(variants.Where(query => query.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 6)).ToArray();
        if (Uri.TryCreate(request.Url, UriKind.Absolute, out var supplied))
        {
            var result = await TryCandidateAsync(new(supplied, supplied, "Source supplied for " + request.Query), failures, visited, token).ConfigureAwait(false);
            if (result is not null) return result;
        }

        // Commons exposes raster thumbnails for SVG maps, flags and coats of arms too.
        var symbolTitle = PresentationImageSearch.SymbolTitle(request.Query);
        var exactTitle = symbolTitle ?? CanonicalTitle(request.Query);
        var exactNames = new[] { exactTitle, request.Query }.Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(title => FileExtensions.Select(extension => "File:" + title + "." + extension));
        var exactEndpoints = symbolTitle is not null || request.Query.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 4
            ? new[] { new Uri("https://commons.wikimedia.org/w/api.php?action=query&format=json&titles=" + Uri.EscapeDataString(string.Join('|', exactNames)) + "&prop=imageinfo&iiprop=url%7Cextmetadata&iiurlwidth=1280") } : [];
        var endpoints = exactEndpoints
            .Concat(queries.Select(query => new Uri("https://commons.wikimedia.org/w/api.php?action=query&format=json&generator=search&gsrnamespace=6&gsrlimit=8&gsrsearch=" +
                Uri.EscapeDataString(query) + "&prop=imageinfo&iiprop=url%7Cextmetadata&iiurlwidth=1280")));
        foreach (var endpoint in endpoints)
        {
            try
            {
                var response = await ReadAsync(endpoint, token).ConfigureAwait(false);
                using var document = JsonDocument.Parse(response.Bytes);
                if (document.RootElement.TryGetProperty("error", out var apiError))
                    throw new InvalidDataException(String(apiError, "info") ?? "Commons API returned an error.");
                if (document.RootElement.TryGetProperty("query", out var query) && query.TryGetProperty("pages", out var pages))
                    foreach (var page in pages.EnumerateObject().Select(property => property.Value)
                        .OrderByDescending(page => Relevance(request.Query, String(page, "title") ?? ""))
                        .ThenBy(page => page.TryGetProperty("index", out var rank) ? rank.GetInt32() : int.MaxValue))
                    {
                        if (!page.TryGetProperty("imageinfo", out var infos) || infos.GetArrayLength() == 0) continue;
                        if (!PresentationImageSearch.MatchesKind(request.Query, String(page, "title") ?? "")) continue;
                        if (Relevance(request.Query, String(page, "title") ?? "") == 0) continue;
                        var info = infos[0];
                        var url = String(info, "thumburl") ?? String(info, "url");
                        var source = String(info, "descriptionurl");
                        if (!Uri.TryCreate(url, UriKind.Absolute, out var imageUrl) || !Uri.TryCreate(source, UriKind.Absolute, out var sourcePage)) continue;
                        var attribution = String(page, "title") ?? request.Query;
                        if (info.TryGetProperty("extmetadata", out var metadata))
                            foreach (var key in MetadataFields)
                                if (metadata.TryGetProperty(key, out var entry) && String(entry, "value") is { } value) attribution += "; " + PlainText(value);
                        var result = await TryCandidateAsync(new(imageUrl, sourcePage, attribution), failures, visited, token).ConfigureAwait(false);
                        if (result is not null) return result;
                    }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { failures.Add("Wikimedia Commons request timed out."); }
            catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.TooManyRequests)
            {
                failures.Add("Wikimedia Commons temporarily limited requests. " + exception.Message);
                break;
            }
            catch (Exception exception) when (exception is not OperationCanceledException && exception is not OutOfMemoryException)
            {
                failures.Add("Wikimedia Commons: " + exception.Message);
            }
        }

        // An independent image index avoids relying on the same rate-limited source.
        foreach (var imageQuery in new[] { variants[^1], PresentationImageSearch.SubjectQuery(request.Query) }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var endpoint = new Uri("https://api.openverse.org/v1/images/?q=" + Uri.EscapeDataString(imageQuery) + "&page_size=8");
                var response = await ReadAsync(endpoint, token).ConfigureAwait(false);
                using var document = JsonDocument.Parse(response.Bytes);
                if (!document.RootElement.TryGetProperty("results", out var results)) continue;
                foreach (var entry in results.EnumerateArray().OrderByDescending(entry => Relevance(request.Query, String(entry, "title") ?? "")))
                {
                    var title = String(entry, "title") ?? "";
                    if (!PresentationImageSearch.MatchesSubject(request.Query, title) || !PresentationImageSearch.MatchesKind(request.Query, title)) continue;
                    if (!Uri.TryCreate(String(entry, "foreign_landing_url"), UriKind.Absolute, out var source)) continue;
                    var attribution = title + "; " + String(entry, "creator") + "; " + String(entry, "license") + " " + String(entry, "license_version") + "; " + String(entry, "license_url");
                    foreach (var url in new[] { String(entry, "url"), String(entry, "thumbnail") }.Distinct())
                    {
                        if (!Uri.TryCreate(url, UriKind.Absolute, out var imageUrl)) continue;
                        var result = await TryCandidateAsync(new(imageUrl, source, attribution), failures, visited, token).ConfigureAwait(false);
                        if (result is not null) return result;
                    }
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { failures.Add("Openverse request timed out."); }
            catch (Exception exception) when (exception is not OperationCanceledException && exception is not OutOfMemoryException)
            {
                failures.Add("Openverse: " + exception.Message);
            }
        }

        // Fall back to relevant public pages, rather than silently substituting a generated image.
        var searchQueries = new[] { variants[^1] + " site:commons.wikimedia.org", variants[^1] + " photo -site:shutterstock.com -site:gettyimages.com -site:istockphoto.com -site:alamy.com" };
        foreach (var searchQuery in searchQueries)
        {
            var found = await search.SearchAsync(searchQuery, 6, token).ConfigureAwait(false);
            foreach (var page in found.Results.Take(6))
            {
                if (!Uri.TryCreate(page.Url, UriKind.Absolute, out var uri) || StockHosts.Any(host => uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase))) continue;
                if (!PresentationImageSearch.MatchesSubject(request.Query, page.Title + " " + page.Snippet) || !PresentationImageSearch.MatchesKind(request.Query, page.Title)) continue;
                var result = await TryCandidateAsync(new(uri, uri, page.Title), failures, visited, token).ConfigureAwait(false);
                if (result is not null) return result;
            }
        }
        throw new InvalidDataException($"No usable internet image was downloaded for '{request.Query}'. Tried Commons queries: {string.Join(" | ", queries)}. " + string.Join("; ", failures.Distinct().Take(5)));
    }

    private async Task<PresentationWebImage?> TryCandidateAsync(Candidate candidate, List<string> failures, HashSet<string> visited, CancellationToken token)
    {
        if (!visited.Add(candidate.Image.AbsoluteUri)) return null;
        try
        {
            var source = candidate.Source;
            if (candidate.Image.Host.Equals("commons.wikimedia.org", StringComparison.OrdinalIgnoreCase) &&
                Uri.UnescapeDataString(candidate.Image.AbsolutePath) is { } filePath && filePath.StartsWith("/wiki/File:", StringComparison.OrdinalIgnoreCase))
            {
                var api = new Uri("https://commons.wikimedia.org/w/api.php?action=query&format=json&titles=" + Uri.EscapeDataString(filePath[6..]) + "&prop=imageinfo&iiprop=url%7Cextmetadata&iiurlwidth=1280");
                var infoDownload = await ReadAsync(api, token).ConfigureAwait(false);
                using var infoDocument = JsonDocument.Parse(infoDownload.Bytes);
                var page = infoDocument.RootElement.GetProperty("query").GetProperty("pages").EnumerateObject().Select(property => property.Value)
                    .FirstOrDefault(page => page.TryGetProperty("imageinfo", out _));
                if (page.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException("The Commons file page has no image data.");
                var info = page.GetProperty("imageinfo")[0];
                var image = new Uri(String(info, "thumburl") ?? String(info, "url") ?? throw new InvalidDataException("The Commons file has no image URL."));
                var attribution = String(page, "title") ?? candidate.Attribution;
                if (info.TryGetProperty("extmetadata", out var metadata))
                    foreach (var key in MetadataFields)
                        if (metadata.TryGetProperty(key, out var entry) && String(entry, "value") is { } value) attribution += "; " + PlainText(value);
                return await TryCandidateAsync(new(image, source, attribution), failures, visited, token).ConfigureAwait(false);
            }
            var downloaded = await ReadAsync(candidate.Image, token).ConfigureAwait(false);
            if (downloaded.MediaType.Contains("html", StringComparison.OrdinalIgnoreCase))
            {
                var html = new HtmlDocument();
                html.LoadHtml(Encoding.UTF8.GetString(downloaded.Bytes));
                var ogImage = html.DocumentNode.SelectSingleNode("//meta[@property='og:image' or @name='twitter:image']")?.GetAttributeValue("content", "");
                if (string.IsNullOrWhiteSpace(ogImage) || !Uri.TryCreate(downloaded.Url, WebUtility.HtmlDecode(ogImage), out var image))
                    throw new InvalidDataException("The source page did not expose a downloadable image.");
                source = downloaded.Url;
                downloaded = await ReadAsync(image, token).ConfigureAwait(false);
            }
            if (!downloaded.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) &&
                downloaded.MediaType is not ("application/octet-stream" or ""))
                throw new InvalidDataException("The image URL returned " + downloaded.MediaType);
            var png = await decoder.ToPngAsync(downloaded.Bytes, token).ConfigureAwait(false);
            return new(png, downloaded.Url.AbsoluteUri, source.AbsoluteUri, candidate.Attribution[..Math.Min(candidate.Attribution.Length, 3000)]);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            failures.Add(candidate.Image.Host + ": download timed out.");
            return null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException && exception is not OutOfMemoryException)
        {
            failures.Add(candidate.Image.Host + ": " + exception.Message);
            return null;
        }
    }

    private async Task<Download> ReadAsync(Uri uri, CancellationToken token)
    {
        using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        requestTimeout.CancelAfter(TimeSpan.FromSeconds(12));
        token = requestTimeout.Token;
        for (var redirect = 0; redirect <= 5; redirect++)
        {
            await (validateUrl ?? UrlSafetyValidator.ValidatePublicHttpUrlAsync)(uri, token).ConfigureAwait(false);
            if (uri.Host.Equals("commons.wikimedia.org", StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath == "/w/api.php")
            {
                await _commonsGate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    var wait = _nextCommonsRequest - DateTimeOffset.UtcNow;
                    if (wait > TimeSpan.FromSeconds(8)) throw new HttpRequestException("Respecting Wikimedia Retry-After; use an alternative known image URL or resume later.", null, HttpStatusCode.TooManyRequests);
                    if (wait > TimeSpan.Zero) await Task.Delay(wait, token).ConfigureAwait(false);
                    _nextCommonsRequest = DateTimeOffset.UtcNow + (minimumCommonsInterval ?? TimeSpan.FromMilliseconds(800));
                }
                finally { _commonsGate.Release(); }
            }
            if (_unavailableUrls.TryGetValue(uri.AbsoluteUri, out var unavailableUntil) && unavailableUntil > DateTimeOffset.UtcNow)
                throw new InvalidDataException("This URL recently returned 403/404; choose another source.");
            using var message = new HttpRequestMessage(HttpMethod.Get, uri);
            message.Headers.UserAgent.ParseAdd("Neuroset/1.0 (desktop presentation image downloader)");
            using var response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests && uri.Host.Equals("commons.wikimedia.org", StringComparison.OrdinalIgnoreCase))
                _nextCommonsRequest = response.Headers.RetryAfter?.Date ?? DateTimeOffset.UtcNow + (response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5));
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                continue;
            }
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                if (_unavailableUrls.Count > 2048) _unavailableUrls.Clear();
                _unavailableUrls[uri.AbsoluteUri] = DateTimeOffset.UtcNow.AddMinutes(15);
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaximumBytes) throw new InvalidDataException("Internet image exceeds 32 MB.");
            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var bytes = new MemoryStream();
            var buffer = new byte[32 * 1024];
            int count;
            while ((count = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                if (bytes.Length + count > MaximumBytes) throw new InvalidDataException("Internet image exceeds 32 MB.");
                bytes.Write(buffer, 0, count);
            }
            return new(bytes.ToArray(), response.Content.Headers.ContentType?.MediaType ?? "", uri);
        }
        throw new InvalidDataException("Too many redirects while downloading an image.");
    }

    private static string? String(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string CanonicalTitle(string query)
    {
        var title = query.Trim();
        if (title.EndsWith(" flag", StringComparison.OrdinalIgnoreCase)) return "Flag of " + title[..^5];
        if (title.StartsWith("Coat of arms ", StringComparison.OrdinalIgnoreCase) && !title.StartsWith("Coat of arms of ", StringComparison.OrdinalIgnoreCase))
            return "Coat of arms of " + title[13..];
        if (title.EndsWith(" coat of arms", StringComparison.OrdinalIgnoreCase)) return "Coat of arms of " + title[..^13];
        return title;
    }
    private static int Relevance(string query, string title)
    {
        static string Normalize(string value) => new(value.Normalize(NormalizationForm.FormD).Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark).ToArray());
        var words = Regex.Matches(Normalize(query), @"[\p{L}\p{N}]+", RegexOptions.CultureInvariant).Select(match => match.Value).Where(word => word.Length > 2 && !GenericWords.Contains(word)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var normalizedTitle = Normalize(title);
        return words.Length == 0 ? 1 : words.Count(word => normalizedTitle.Contains(word, StringComparison.OrdinalIgnoreCase));
    }
    private static string PlainText(string html)
    {
        var document = new HtmlDocument();
        document.LoadHtml(html);
        return WebUtility.HtmlDecode(document.DocumentNode.InnerText);
    }
    private sealed record Candidate(Uri Image, Uri Source, string Attribution);
    private sealed record Download(byte[] Bytes, string MediaType, Uri Url);
    public void Dispose() => _commonsGate.Dispose();
}
