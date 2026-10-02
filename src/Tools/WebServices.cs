using System.Net;
using System.Diagnostics;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using AngleSharp.Html.Parser;
using HtmlAgilityPack;
using Neuroset.Application;

namespace Neuroset.Tools;

public static class UrlSafetyValidator
{
    public static async Task ValidatePublicHttpUrlAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https")) throw new InvalidOperationException("Only absolute HTTP and HTTPS URLs are allowed.");
        if (!string.IsNullOrEmpty(uri.UserInfo)) throw new InvalidOperationException("URLs containing embedded credentials are not allowed.");
        if (uri.Port is 0 or > 65535) throw new InvalidOperationException("Invalid URL port.");
        var addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, cancellationToken).ConfigureAwait(false);
        if (addresses.Length == 0 || addresses.Any(IsPrivateOrReserved)) throw new InvalidOperationException("The URL resolves to a private, loopback, link-local or otherwise reserved network address.");
    }

    private static bool IsPrivateOrReserved(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6Multicast || address.IsIPv6SiteLocal) return true;
            var bytes = address.GetAddressBytes();
            return (bytes[0] & 0xFE) == 0xFC;
        }
        var b = address.GetAddressBytes();
        return b[0] is 0 or 10 or 127 || b[0] >= 224 || (b[0] == 169 && b[1] == 254) || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 100 && b[1] is >= 64 and <= 127);
    }
}

public sealed class DuckDuckGoSearchProvider(HttpClient httpClient, TimeSpan? attemptTimeout = null) : ISearchProvider
{
    private readonly TimeSpan _attemptTimeout = attemptTimeout ?? TimeSpan.FromSeconds(10);

    public async Task<SearchResponse> SearchAsync(string query, int limit = 5, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        limit = Math.Clamp(limit, 1, 10);
        var attempts = new List<SearchAttempt>();
        var duckDuckGo = await SearchDuckDuckGoAsync(query, limit, attempts, cancellationToken).ConfigureAwait(false);
        if (duckDuckGo.Count > 0) return new(duckDuckGo, attempts);

        var bing = await SearchBingRssAsync(query, limit, attempts, cancellationToken).ConfigureAwait(false);
        return new(bing, attempts);
    }

    private async Task<IReadOnlyList<SearchResult>> SearchDuckDuckGoAsync(string query, int limit, List<SearchAttempt> attempts, CancellationToken cancellationToken)
    {
        var endpoint = new Uri($"https://html.duckduckgo.com/html/?q={Uri.EscapeDataString(query)}");
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var response = await ReadResponseAsync(endpoint, isRss: false, cancellationToken).ConfigureAwait(false);
            var html = response.Content;
            var statusCode = (int)response.StatusCode;
            if (IsDuckDuckGoChallenge(response.StatusCode, html))
            {
                attempts.Add(new("DuckDuckGo", "blocked", 0, statusCode, "DuckDuckGo requested bot verification; Neuroset automatically switched to the fallback provider.", stopwatch.Elapsed.TotalMilliseconds));
                return [];
            }
            if (statusCode is < 200 or >= 300)
            {
                attempts.Add(new("DuckDuckGo", "failed", 0, statusCode, $"HTTP {statusCode} {response.ReasonPhrase}".Trim(), stopwatch.Elapsed.TotalMilliseconds));
                return [];
            }

            var document = new HtmlDocument();
            document.LoadHtml(html);
            IEnumerable<HtmlNode> nodes = document.DocumentNode.SelectNodes("//div[contains(concat(' ', normalize-space(@class), ' '), ' result ')]") ?? Enumerable.Empty<HtmlNode>();
            var results = new List<SearchResult>();
            var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in nodes)
            {
                var link = node.SelectSingleNode(".//a[contains(@class,'result__a')]");
                if (link is null) continue;
                var href = WebUtility.HtmlDecode(link.GetAttributeValue("href", string.Empty));
                var url = DecodeDuckDuckGoUrl(href);
                if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https")) continue;
                if (!seenUrls.Add(parsed.AbsoluteUri)) continue;
                var snippet = node.SelectSingleNode(".//*[contains(@class,'result__snippet')]")?.InnerText ?? string.Empty;
                results.Add(new(NormalizeText(link.InnerText), parsed.AbsoluteUri, NormalizeText(snippet), "DuckDuckGo"));
                if (results.Count >= limit) break;
            }
            attempts.Add(new("DuckDuckGo", results.Count == 0 ? "no_results" : "success", results.Count, statusCode, results.Count == 0 ? "The response contained no recognizable search results; the fallback provider was tried." : null, stopwatch.Elapsed.TotalMilliseconds));
            return results;
        }
        catch (TimeoutException exception)
        {
            attempts.Add(new("DuckDuckGo", "timeout", 0, null, exception.Message, stopwatch.Elapsed.TotalMilliseconds));
            return [];
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or IOException)
        {
            attempts.Add(new("DuckDuckGo", "failed", 0, null, exception.Message, stopwatch.Elapsed.TotalMilliseconds));
            return [];
        }
    }

    private async Task<IReadOnlyList<SearchResult>> SearchBingRssAsync(string query, int limit, List<SearchAttempt> attempts, CancellationToken cancellationToken)
    {
        var endpoint = new Uri($"https://www.bing.com/search?format=rss&count={limit}&q={Uri.EscapeDataString(query)}");
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var response = await ReadResponseAsync(endpoint, isRss: true, cancellationToken).ConfigureAwait(false);
            var xml = response.Content;
            var statusCode = (int)response.StatusCode;
            if (statusCode is < 200 or >= 300)
            {
                attempts.Add(new("Bing RSS", "failed", 0, statusCode, $"HTTP {statusCode} {response.ReasonPhrase}".Trim(), stopwatch.Elapsed.TotalMilliseconds));
                return [];
            }

            using var stringReader = new StringReader(xml);
            using var reader = XmlReader.Create(stringReader, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            var document = XDocument.Load(reader, LoadOptions.None);
            if (document.Root?.Name.LocalName != "rss") throw new XmlException("Bing did not return an RSS search response.");
            var results = new List<SearchResult>();
            var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in document.Descendants().Where(element => element.Name.LocalName == "item"))
            {
                var title = ChildValue(item, "title");
                var url = ChildValue(item, "link");
                var snippet = ChildValue(item, "description");
                if (string.IsNullOrWhiteSpace(title) || !Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https")) continue;
                if (!seenUrls.Add(parsed.AbsoluteUri)) continue;
                results.Add(new(NormalizeText(title), parsed.AbsoluteUri, NormalizeText(snippet), "Bing"));
                if (results.Count >= limit) break;
            }
            attempts.Add(new("Bing RSS", results.Count == 0 ? "no_results" : "success", results.Count, statusCode, results.Count == 0 ? "Bing returned a valid RSS response without result items." : "Used automatically as a fallback after the primary provider returned no usable results.", stopwatch.Elapsed.TotalMilliseconds));
            return results;
        }
        catch (TimeoutException exception)
        {
            attempts.Add(new("Bing RSS", "timeout", 0, null, exception.Message, stopwatch.Elapsed.TotalMilliseconds));
            return [];
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or XmlException or IOException)
        {
            attempts.Add(new("Bing RSS", "failed", 0, null, exception.Message, stopwatch.Elapsed.TotalMilliseconds));
            return [];
        }
    }

    private Task<(HttpStatusCode StatusCode, string? ReasonPhrase, string Content)> ReadResponseAsync(Uri endpoint, bool isRss, CancellationToken cancellationToken) =>
        WebOperation.RunAsync(async token =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.UserAgent.ParseAdd("Neuroset/1.0 (+local desktop search client)");
            if (isRss) request.Headers.Accept.ParseAdd("application/rss+xml, application/xml;q=0.9, text/xml;q=0.8");
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, token).ConfigureAwait(false);
            var content = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            return (response.StatusCode, response.ReasonPhrase, content);
        }, _attemptTimeout, cancellationToken);

    private static bool IsDuckDuckGoChallenge(HttpStatusCode statusCode, string html) =>
        statusCode == HttpStatusCode.Accepted ||
        html.Contains("anomaly-modal", StringComparison.OrdinalIgnoreCase) ||
        html.Contains("challenge-form", StringComparison.OrdinalIgnoreCase) ||
        html.Contains("bot verification", StringComparison.OrdinalIgnoreCase);

    private static string ChildValue(XElement element, string localName) =>
        element.Elements().FirstOrDefault(child => child.Name.LocalName == localName)?.Value ?? string.Empty;

    private static string NormalizeText(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var document = new HtmlDocument();
        document.LoadHtml(value);
        var decoded = WebUtility.HtmlDecode(document.DocumentNode.InnerText);
        var builder = new StringBuilder(decoded.Length);
        var previousWhitespace = false;
        foreach (var character in decoded)
        {
            var whitespace = char.IsWhiteSpace(character);
            if (!whitespace || !previousWhitespace) builder.Append(whitespace ? ' ' : character);
            previousWhitespace = whitespace;
        }
        return builder.ToString().Trim();
    }

    private static string DecodeDuckDuckGoUrl(string value)
    {
        value = WebUtility.HtmlDecode(value).Trim();
        if (value.StartsWith("//", StringComparison.Ordinal)) value = "https:" + value;
        else if (value.StartsWith('/')) value = new Uri(new Uri("https://duckduckgo.com"), value).AbsoluteUri;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return value;
        if (!uri.Host.Equals("duckduckgo.com", StringComparison.OrdinalIgnoreCase) && !uri.Host.EndsWith(".duckduckgo.com", StringComparison.OrdinalIgnoreCase)) return value;
        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Split('=', 2)).FirstOrDefault(x => x.Length == 2 && x[0] == "uddg");
        return query is null ? value : WebUtility.UrlDecode(query[1]);
    }
}

public sealed class WebContentFetcher(HttpClient httpClient) : IWebContentFetcher
{
    private const int MaximumBytes = 5 * 1024 * 1024;

    public async Task<FetchedPage> FetchAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        await UrlSafetyValidator.ValidatePublicHttpUrlAsync(uri, cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri); request.Headers.UserAgent.ParseAdd("Neuroset/1.0");
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaximumBytes) throw new InvalidOperationException("The page exceeds the 5 MB fetch limit.");
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var bounded = new MemoryStream();
        var buffer = new byte[81920]; var total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false); if (read == 0) break;
            total += read; if (total > MaximumBytes) throw new InvalidOperationException("The page exceeds the 5 MB fetch limit.");
            await bounded.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        var mediaType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
        bounded.Position = 0;
        if (mediaType is not ("text/html" or "application/xhtml+xml" or "text/plain" or "application/json" or "text/markdown")) throw new InvalidOperationException($"Unsupported response content type: {mediaType}");
        using var reader = new StreamReader(bounded, response.Content.Headers.ContentType?.CharSet is { } charset ? Encoding.GetEncoding(charset) : Encoding.UTF8, true);
        var content = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        if (mediaType is "text/plain" or "application/json" or "text/markdown") return new(uri.AbsoluteUri, uri.Host, content, mediaType, DateTimeOffset.UtcNow);

        var parser = new HtmlParser(); var document = await parser.ParseDocumentAsync(content, cancellationToken).ConfigureAwait(false);
        foreach (var element in document.QuerySelectorAll("script,style,noscript,svg,nav,footer,header,form,aside")) element.Remove();
        var root = document.QuerySelector("main,article,[role=main]") ?? document.Body;
        var text = NormalizeWhitespace(root?.TextContent ?? string.Empty);
        return new(uri.AbsoluteUri, document.Title?.Trim() ?? uri.Host, text, mediaType, DateTimeOffset.UtcNow);
    }

    private static string NormalizeWhitespace(string value)
    {
        var builder = new StringBuilder(value.Length); var previousWhitespace = false;
        foreach (var character in value)
        {
            var whitespace = char.IsWhiteSpace(character);
            if (!whitespace || !previousWhitespace) builder.Append(whitespace ? ' ' : character);
            previousWhitespace = whitespace;
        }
        return builder.ToString().Trim();
    }
}
