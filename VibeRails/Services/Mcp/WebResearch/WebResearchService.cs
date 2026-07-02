using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace VibeRails.Services.Mcp.WebResearch;

public partial class WebResearchService : IWebResearchService
{
    private const int MaxDownloadBytes = 2_000_000;  

    private readonly HttpClient _http;
    private readonly ILogger<WebResearchService> _logger;

    public WebResearchService(HttpClient http, ILogger<WebResearchService> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<IReadOnlyList<WebSearchResult>> SearchAsync(
        string query,
        int maxResults = 5,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new ArgumentException("Query is required.");
        }

        maxResults = Math.Clamp(maxResults, 1, 10);
        var uri = new Uri($"https://duckduckgo.com/html/?q={Uri.EscapeDataString(query.Trim())}");
        var html = await GetStringAsync(uri, cancellationToken);
        var results = ParseDuckDuckGoResults(html, uri, maxResults);
        return results.Count > 0 ? results : ParseLinks(html, uri, maxResults);
    }

    public async Task<WebPageFetchResult> FetchAsync(
        string url,
        int maxChars = 12000,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("URL must be an absolute HTTP or HTTPS URL.", nameof(url));
        }

        var html = await GetStringAsync(uri, cancellationToken);
        maxChars = Math.Clamp(maxChars, 1000, 50000);

        var title = ExtractTitle(html);
        var text = HtmlToText(html);
        if (text.Length > maxChars)
        {
            text = text[..maxChars] + "\n[VibeRails truncated page text]";
        }

        return new WebPageFetchResult(
            url,
            title,
            text,
            ParseLinks(html, uri, 12));
    }

    private async Task<string> GetStringAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,text/plain;q=0.8,*/*;q=0.5");

        using var response = await _http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength is > MaxDownloadBytes)
        {
            throw new InvalidOperationException($"Response is larger than {MaxDownloadBytes} bytes.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var bytes = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(bytes, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > MaxDownloadBytes)
            {
                throw new InvalidOperationException($"Response exceeded {MaxDownloadBytes} bytes.");
            }

            buffer.Write(bytes, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static List<WebSearchResult> ParseDuckDuckGoResults(string html, Uri baseUri, int maxResults)
    {
        var resultMatches = DuckResultRegex.Matches(html);
        var results = new List<WebSearchResult>();

        for (var i = 0; i < resultMatches.Count && results.Count < maxResults; i++)
        {
            var match = resultMatches[i];
            var href = DecodeDuckDuckGoUrl(CleanText(match.Groups["href"].Value), baseUri);
            if (href == null)
            {
                continue;
            }

            var title = CleanText(match.Groups["title"].Value);
            var nextResultIndex = i + 1 < resultMatches.Count ? resultMatches[i + 1].Index : html.Length;
            var snippet = FindSnippetBetweenResults(html, match.Index + match.Length, nextResultIndex);

            if (!string.IsNullOrWhiteSpace(title))
            {
                results.Add(new WebSearchResult(title, href, snippet));
            }
        }

        return results;
    }

    internal static string FindSnippetBetweenResults(string html, int startIndex, int nextResultIndex)
    {
        if (startIndex < 0 || startIndex >= html.Length || nextResultIndex <= startIndex)
        {
            return string.Empty;
        }

        var snippetMatch = DuckSnippetRegex.Match(html, startIndex);
        if (!snippetMatch.Success || snippetMatch.Index >= nextResultIndex)
        {
            return string.Empty;
        }

        return CleanText(snippetMatch.Groups["snippet"].Value);
    }

    private static List<WebSearchResult> ParseLinks(string html, Uri baseUri, int maxResults)
    {
        var results = new List<WebSearchResult>();
        foreach (Match match in AnchorRegex.Matches(html))
        {
            if (results.Count >= maxResults)
            {
                break;
            }

            var href = CleanText(match.Groups["href"].Value);
            if (!Uri.TryCreate(baseUri, href, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                continue;
            }

            var title = CleanText(match.Groups["text"].Value);
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            results.Add(new WebSearchResult(title, uri.ToString(), ""));
        }

        return results;
    }

    private static string? DecodeDuckDuckGoUrl(string href, Uri baseUri)
    {
        if (!Uri.TryCreate(baseUri, href, out var uri))
        {
            return null;
        }

        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in query)
        {
            var pieces = part.Split('=', 2);
            if (pieces.Length == 2 && pieces[0] == "uddg")
            {
                return WebUtility.UrlDecode(pieces[1]);
            }
        }

        return uri.Scheme is "http" or "https" ? uri.ToString() : null;
    }

    private static string ExtractTitle(string html)
    {
        var match = TitleRegex.Match(html);
        return match.Success ? CleanText(match.Groups[1].Value) : "";
    }

    private static string HtmlToText(string html)
    {
        var withoutBlocks = ScriptStyleRegex.Replace(html, " ");
        var withBreaks = withoutBlocks
            .Replace("</p>", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("<br>", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("<br/>", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("<br />", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("</li>", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("</h1>", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("</h2>", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("</h3>", "\n", StringComparison.OrdinalIgnoreCase);
        return CleanText(TagRegex.Replace(withBreaks, " "));
    }

    private static string CleanText(string value)
    {
        var noTags = TagRegex.Replace(value, " ");
        var decoded = WebUtility.HtmlDecode(noTags);
        return WhitespaceRegex.Replace(decoded, " ").Trim();
    }
}
