using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using VibeRails.Services.Mcp.WebResearch;
using Xunit;

namespace Tests.Services.Mcp;

public sealed class WebResearchServiceTests
{
    [Fact]
    public async Task SearchAsync_DoesNotShiftSnippetWhenResultHasNoSnippet()
    {
        const string html = """
            <html><body>
              <a class="result__a" href="/l/?uddg=https%3A%2F%2Fone.example%2F">First result</a>
              <a class="result__a" href="/l/?uddg=https%3A%2F%2Ftwo.example%2F">Second result</a>
              <div class="result__snippet">Second snippet</div>
            </body></html>
            """;

        using var http = new HttpClient(new StaticHtmlHandler(html));
        var service = new WebResearchService(http, NullLogger<WebResearchService>.Instance);

        var results = await service.SearchAsync(
            "query",
            maxResults: 2,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
        Assert.Equal("First result", results[0].Title);
        Assert.Equal("", results[0].Snippet);
        Assert.Equal("Second result", results[1].Title);
        Assert.Equal("Second snippet", results[1].Snippet);
    }

    [Fact]
    public async Task SearchAsync_ReturnsEmpty_WhenNoStructuredResultsFound()
    {
        // A challenge / markup-change page has anchors but no result__a: the service must NOT
        // fall back to scraping arbitrary links and presenting them as search results.
        const string html = """
            <html><body>
              <a href="https://duckduckgo.com/settings">Settings</a>
              <a href="https://duckduckgo.com/about">About</a>
            </body></html>
            """;

        using var http = new HttpClient(new StaticHtmlHandler(html));
        var service = new WebResearchService(http, NullLogger<WebResearchService>.Instance);

        var results = await service.SearchAsync(
            "query",
            maxResults: 5,
            TestContext.Current.CancellationToken);

        Assert.Empty(results);
    }

    [Fact]
    public async Task FetchAsync_DecodesBodyUsingDeclaredCharset()
    {
        // 'é' is a single 0xE9 byte in ISO-8859-1; decoded as UTF-8 it becomes U+FFFD. The
        // service must honor the declared charset instead of assuming UTF-8.
        var body = Encoding.Latin1.GetBytes("<html><body><p>café</p></body></html>");

        using var http = new HttpClient(new ByteContentHandler(body, "text/html; charset=iso-8859-1"));
        var service = new WebResearchService(http, NullLogger<WebResearchService>.Instance);

        var page = await service.FetchAsync(
            "https://example.com",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("café", page.Text);
    }

    private sealed class StaticHtmlHandler(string html) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(html)
            };

            return Task.FromResult(response);
        }
    }

    private sealed class ByteContentHandler(byte[] body, string contentType) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var content = new ByteArrayContent(body);
            content.Headers.ContentType =
                System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType);

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
