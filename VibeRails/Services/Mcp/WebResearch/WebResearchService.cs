using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace VibeRails.Services.Mcp.WebResearch;

public partial class WebResearchService : IWebResearchService
{
    private const int MaxDownloadBytes = 2_000_000;

    // HttpClient.Timeout with ResponseHeadersRead only bounds header receipt, so a server that
    // sends headers then trickles the body could otherwise keep the read loop — and the calling
    // agent — blocked indefinitely. We bound the whole request (headers + body) with our own
    // deadline instead of relying on the transport-specific HttpClient.Timeout.
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

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
        // Only return real DuckDuckGo result anchors. Previously this fell back to scraping every
        // <a> on the page, which turned a captcha / challenge / markup-change page into a list of
        // nav/footer/ad links presented as "results" — confidently wrong. An empty list is the
        // honest signal that the search yielded nothing usable.
        return ParseDuckDuckGoResults(html, uri, maxResults);
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
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(RequestTimeout);
        var token = timeoutCts.Token;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,text/plain;q=0.8,*/*;q=0.5");

            using var response = await _http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                token);
            response.EnsureSuccessStatusCode();

            if (response.Content.Headers.ContentLength is > MaxDownloadBytes)
            {
                throw new InvalidOperationException($"Response is larger than {MaxDownloadBytes} bytes.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(token);
            using var buffer = new MemoryStream();
            var bytes = new byte[8192];
            while (true)
            {
                var read = await stream.ReadAsync(bytes, token);
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

            return Decode(response, buffer);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Web request to {uri.Host} timed out after {(int)RequestTimeout.TotalSeconds} seconds.");
        }
    }

    // Decode the body using the charset the server actually declared (a leading BOM, then the
    // Content-Type header) instead of assuming UTF-8, which turns ISO-8859-1 / Windows-1252 /
    // UTF-16 pages into mojibake. Reads straight from the MemoryStream's backing buffer to avoid
    // copying the (up to 2 MB) payload a second time.
    private static string Decode(HttpResponseMessage response, MemoryStream buffer)
    {
        var raw = buffer.GetBuffer();
        var length = (int)buffer.Length;

        // A byte-order mark is unambiguous, so it wins over the header.
        if (length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(raw, 3, length - 3);
        }
        if (length >= 2 && raw[0] == 0xFF && raw[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(raw, 2, length - 2);
        }
        if (length >= 2 && raw[0] == 0xFE && raw[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(raw, 2, length - 2);
        }

        return ResolveEncoding(response.Content.Headers.ContentType?.CharSet).GetString(raw, 0, length);
    }

    private static Encoding ResolveEncoding(string? charset)
    {
        if (string.IsNullOrWhiteSpace(charset))
        {
            return Encoding.UTF8;
        }

        charset = charset.Trim().Trim('"', '\'');

        // Windows-1252 needs the CodePages provider (not referenced here); ISO-8859-1 / Latin1 is
        // built in. Both are common legacy web charsets and near-identical, so map them to the
        // built-in Latin1 rather than take a new dependency or crash the fetch.
        if (charset.Equals("windows-1252", StringComparison.OrdinalIgnoreCase)
            || charset.Equals("cp1252", StringComparison.OrdinalIgnoreCase)
            || charset.Equals("iso-8859-1", StringComparison.OrdinalIgnoreCase)
            || charset.Equals("latin1", StringComparison.OrdinalIgnoreCase))
        {
            return Encoding.Latin1;
        }

        try
        {
            return Encoding.GetEncoding(charset);
        }
        catch
        {
            return Encoding.UTF8;
        }
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
        // CleanText already strips remaining tags with TagRegex; don't run the same
        // full-document pass twice.
        return CleanText(withBreaks);
    }

    private static string CleanText(string value)
    {
        var noTags = TagRegex.Replace(value, " ");
        var decoded = WebUtility.HtmlDecode(noTags);
        return WhitespaceRegex.Replace(decoded, " ").Trim();
    }
}
