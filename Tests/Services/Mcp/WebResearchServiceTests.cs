using System.Net;
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
}
