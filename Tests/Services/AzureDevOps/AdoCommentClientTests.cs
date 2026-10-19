using System.Net;
using System.Text;
using System.Text.Json;
using VibeRails.Services.AzureDevOps;
using Xunit;

namespace Tests.Services.AzureDevOps;

/// <summary>
/// The work item comment POST (VIBE-168): HTML-encoded text, links only for vetted replay URLs,
/// never retried, and no response body, token or exception prose in a result.
/// </summary>
public sealed class AdoCommentClientTests
{
    private static string Link(char key) => "https://viberails.ai/shared/session?key=" + new string(key, 64);
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PostsEncodedHtmlParagraphsAndClickableDeduplicatedSessionLinks()
    {
        using var handler = new Handler(HttpStatusCode.OK, "{\"workItemId\":42,\"id\":7,\"url\":\"https://untrusted.example/\"}");
        using var http = new HttpClient(handler);
        var result = await new AzureDevOpsClient(http).AddCommentAsync("contoso", "Web App", "secret", 42,
            "Fixed <script>alert(1)</script> & \"quotes\"\r\nsecond line\r\n\r\n\r\nTests passed.", [Link('a'), Link('b'), Link('a')], Ct);

        Assert.True(result.Success);
        Assert.False(result.MayHavePosted);
        Assert.Equal("7", result.CommentId);
        Assert.Equal(1, handler.Calls);
        Assert.Equal("POST", handler.Method);
        Assert.Equal("https://dev.azure.com/contoso/Web%20App/_apis/wit/workItems/42/comments?format=html&api-version=7.1-preview.4", handler.Url);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(":secret")), handler.Authorization);
        Assert.Equal("Suppress", handler.FedAuthRedirect);

        using var json = JsonDocument.Parse(handler.Body!);
        var html = json.RootElement.GetProperty("text").GetString()!;
        Assert.StartsWith(
            "<p>Fixed &lt;script&gt;alert(1)&lt;/script&gt; &amp; &quot;quotes&quot;<br>second line</p><p>Tests passed.</p>"
            + "<p>VibeRails sessions used for this work:</p>", html);
        Assert.DoesNotContain("<script", html);
        Assert.Equal(2, CountOf(html, "<a "));
        Assert.Contains($"<p><a href=\"{Link('a')}\">{Link('a')}</a></p><p><a href=\"{Link('b')}\">{Link('b')}</a></p>", html);
        Assert.EndsWith("<p>Public replay links expire after one month. Active sessions become available after they end and upload.</p>", html);
    }

    [Fact]
    public async Task MarkupInTheTextIsLiteralAndOnlyABareReplayUrlBecomesALink()
    {
        using var handler = new Handler(HttpStatusCode.OK, "{\"id\":8}");
        using var http = new HttpClient(handler);
        var client = new AzureDevOpsClient(http);
        Assert.True((await client.AddCommentAsync("contoso", "Web", "secret", 42, "<a href=\"https://evil.example\">click</a>", [], Ct)).Success);
        var html = JsonDocument.Parse(handler.Body!).RootElement.GetProperty("text").GetString()!;
        Assert.Equal("<p>&lt;a href=&quot;https://evil.example&quot;&gt;click&lt;/a&gt;</p>", html);

        // A replay URL carrying anything after its key could smuggle attribute text; it is refused unsent.
        foreach (var link in new[] { Link('c') + "&x=\"1", Link('c') + "#top", Link('c') + "\" onmouseover=\"x" })
            await Assert.ThrowsAsync<AdoConfigException>(() => client.AddCommentAsync("contoso", "Web", "secret", 42, "Done", [link], Ct));
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(400, false, "rejected")]
    [InlineData(401, false, "personal access token")]
    [InlineData(203, false, "personal access token")]
    [InlineData(302, false, "personal access token")]
    [InlineData(403, false, "Work Items")]
    [InlineData(404, false, "not found")]
    [InlineData(429, false, "rate limited")]
    [InlineData(500, true, "before retrying")]
    [InlineData(503, true, "before retrying")]
    public async Task FailuresAreSanitizedAndNeverRetried(int status, bool uncertain, string message)
    {
        using var handler = new Handler((HttpStatusCode)status, "SECRET upstream response");
        using var http = new HttpClient(handler);
        var result = await new AzureDevOpsClient(http).AddCommentAsync("contoso", "Web", "secret", 12, "Done", [], Ct);
        Assert.False(result.Success);
        Assert.Equal(uncertain, result.MayHavePosted);
        Assert.Contains(message, result.Message);
        Assert.DoesNotContain("SECRET", result.Message);
        Assert.DoesNotContain("secret", result.Message);
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>A refused token can come back as a 200 sign-in page: nothing was posted.</summary>
    [Fact]
    public async Task ANonJsonSuccessIsTheSignInPageAndPostedNothing()
    {
        using var handler = new Handler(HttpStatusCode.OK, "<html>Sign in</html>", "text/html");
        using var http = new HttpClient(handler);
        var result = await new AzureDevOpsClient(http).AddCommentAsync("contoso", "Web", "secret", 12, "Done", [], Ct);
        Assert.False(result.Success);
        Assert.False(result.MayHavePosted);
        Assert.Contains("personal access token", result.Message);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{\"id\":\"7\"}")]
    [InlineData("{\"id\":0}")]
    [InlineData("{}")]
    public async Task UnreadableSuccessWarnsThatTheCommentMayExist(string response)
    {
        using var handler = new Handler(HttpStatusCode.OK, response);
        using var http = new HttpClient(handler);
        var result = await new AzureDevOpsClient(http).AddCommentAsync("contoso", "Web", "secret", 12, "Done", [], Ct);
        Assert.False(result.Success);
        Assert.True(result.MayHavePosted);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task BoundsTheSuccessfulResponseButAllowsAzureDevOpsToEchoALongComment()
    {
        using var handler = new Handler(HttpStatusCode.OK, "{\"id\":12,\"text\":\"" + new string('x', 20000) + "\"}");
        using var http = new HttpClient(handler);
        var client = new AzureDevOpsClient(http);
        Assert.True((await client.AddCommentAsync("contoso", "Web", "secret", 12, new string('x', 20000), [], Ct)).Success);
        handler.ResponseBody = "{\"id\":12,\"text\":\"" + new string('x', 256 * 1024) + "\"}";
        var bounded = await client.AddCommentAsync("contoso", "Web", "secret", 12, "Done", [], Ct);
        Assert.False(bounded.Success);
        Assert.True(bounded.MayHavePosted);
    }

    [Theory]
    [InlineData("contoso", "Web", 0, "Done", null)]
    [InlineData("contoso", "Web", -3, "Done", null)]
    [InlineData("-contoso", "Web", 12, "Done", null)]
    [InlineData("contoso", "../Web", 12, "Done", null)]
    [InlineData("contoso", "Web", 12, " ", null)]
    [InlineData("contoso", "Web", 12, "Done", "https://evil.example/shared/session?key=abc")]
    [InlineData("contoso", "Web", 12, "Done", "javascript:alert(1)")]
    public async Task InvalidInputMakesNoNetworkRequest(string organization, string project, int id, string body, string? link)
    {
        using var handler = new Handler(HttpStatusCode.OK, "{\"id\":1}");
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<AdoConfigException>(() => new AzureDevOpsClient(http).AddCommentAsync(organization, project, "secret", id, body,
            link is null ? [] : [link], Ct));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task OversizedTextAndTooManyLinksAreRefusedBeforeSending()
    {
        using var handler = new Handler(HttpStatusCode.OK, "{\"id\":1}");
        using var http = new HttpClient(handler);
        var client = new AzureDevOpsClient(http);
        await Assert.ThrowsAsync<AdoConfigException>(() => client.AddCommentAsync("contoso", "Web", "secret", 12, new string('x', 20001), [], Ct));
        await Assert.ThrowsAsync<AdoConfigException>(() => client.AddCommentAsync("contoso", "Web", "secret", 12, "Done",
            Enumerable.Range(0, 21).Select(i => Link((char)('a' + i))).ToList(), Ct));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task NetworkFailureIsUnconfirmedAndDoesNotExposeTheException()
    {
        using var handler = new Handler(HttpStatusCode.OK, "") { Throw = true };
        using var http = new HttpClient(handler);
        var result = await new AzureDevOpsClient(http).AddCommentAsync("contoso", "Web", "secret", 12, "Done", [], Ct);
        Assert.False(result.Success);
        Assert.True(result.MayHavePosted);
        Assert.DoesNotContain("SECRET", result.Message);
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>A body that stalls after the headers is cut by the request deadline and reported as unconfirmed.</summary>
    [Fact]
    public async Task AStalledBodyIsUnconfirmedAtTheDeadline()
    {
        using var handler = new Handler(HttpStatusCode.OK, "") { Stall = true };
        using var http = new HttpClient(handler);
        var result = await new AzureDevOpsClient(http, TimeSpan.FromMilliseconds(200)).AddCommentAsync("contoso", "Web", "secret", 12, "Done", [], Ct);
        Assert.False(result.Success);
        Assert.True(result.MayHavePosted);
    }

    private static int CountOf(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + 1, StringComparison.Ordinal))
            count++;
        return count;
    }

    private sealed class Handler(HttpStatusCode status, string body, string mediaType = "application/json") : HttpMessageHandler
    {
        public string ResponseBody { get; set; } = body;
        public bool Throw { get; init; }
        public bool Stall { get; init; }
        public int Calls { get; private set; }
        public string? Body { get; private set; }
        public string? Method { get; private set; }
        public string? Url { get; private set; }
        public string? Authorization { get; private set; }
        public string? FedAuthRedirect { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Method = request.Method.Method;
            Url = request.RequestUri!.AbsoluteUri;
            Authorization = request.Headers.Authorization?.ToString();
            FedAuthRedirect = request.Headers.TryGetValues("X-TFS-FedAuthRedirect", out var values) ? values.Single() : null;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            if (Throw) throw new HttpRequestException("SECRET network detail");
            var response = new HttpResponseMessage(status)
            {
                Content = Stall ? new StreamContent(new StallingStream()) : new StringContent(ResponseBody, Encoding.UTF8, mediaType)
            };
            if (Stall) response.Content.Headers.ContentType = new("application/json");
            return response;
        }
    }

    private sealed class StallingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
