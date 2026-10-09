using System.Net;
using System.Text;
using System.Text.Json;
using VibeRails.Services.Jira;
using Xunit;

namespace Tests.Services.Jira;

public sealed class JiraCommentClientTests
{
    private static string Link(char key) => "https://viberails.ai/shared/session?key=" + new string(key, 64);
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PostsAdfWithLiteralTextAndClickableDeduplicatedSessionLinks()
    {
        using var handler = new Handler(HttpStatusCode.Created, "{\"id\":\"123\",\"self\":\"https://untrusted.example/\"}");
        using var http = new HttpClient(handler);
        var result = await new JiraCloudClient(http).AddCommentAsync("https://acme.atlassian.net", "agent@example.com", "secret",
            "10001", "Fixed <script> & \"quotes\"\r\n\r\nTests passed.", [Link('a'), Link('b'), Link('a')], Ct);
        Assert.True(result.Success);
        Assert.Equal("123", result.CommentId);
        Assert.Equal(1, handler.Calls);
        Assert.Equal("POST", handler.Method);
        Assert.Equal("https://acme.atlassian.net/rest/api/3/issue/10001/comment", handler.Url);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("agent@example.com:secret")), handler.Authorization);
        using var json = JsonDocument.Parse(handler.Body!);
        var doc = json.RootElement.GetProperty("body");
        Assert.Equal("doc", doc.GetProperty("type").GetString());
        Assert.Equal(1, doc.GetProperty("version").GetInt32());
        var paragraphs = doc.GetProperty("content").EnumerateArray().ToArray();
        Assert.Equal("Fixed <script> & \"quotes\"", paragraphs[0].GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal(0, paragraphs[1].GetProperty("content").GetArrayLength());
        Assert.Equal("Tests passed.", paragraphs[2].GetProperty("content")[0].GetProperty("text").GetString());
        var urls = paragraphs.SelectMany(p => p.GetProperty("content").EnumerateArray())
            .Where(p => p.TryGetProperty("marks", out _))
            .Select(p => p.GetProperty("marks")[0].GetProperty("attrs").GetProperty("href").GetString()!).ToArray();
        Assert.Equal([Link('a'), Link('b')], urls);
    }

    [Theory]
    [InlineData(400, false, "rejected")]
    [InlineData(401, false, "token")]
    [InlineData(403, false, "permission")]
    [InlineData(404, false, "not found")]
    [InlineData(429, false, "rate limited")]
    [InlineData(302, true, "before retrying")]
    [InlineData(500, true, "before retrying")]
    public async Task FailuresAreSanitizedAndNeverRetried(int status, bool uncertain, string message)
    {
        using var handler = new Handler((HttpStatusCode)status, "SECRET upstream response");
        using var http = new HttpClient(handler);
        var result = await new JiraCloudClient(http).AddCommentAsync("https://acme.atlassian.net", "email", "secret", "12", "Done", [], Ct);
        Assert.False(result.Success);
        Assert.Equal(uncertain, result.MayHavePosted);
        Assert.Contains(message, result.Message);
        Assert.DoesNotContain("SECRET", result.Message);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{\"id\":\"https://evil/\"}")]
    [InlineData("{}")]
    public async Task UnreadableSuccessWarnsThatCommentMayExist(string response)
    {
        using var handler = new Handler(HttpStatusCode.Created, response);
        using var http = new HttpClient(handler);
        var result = await new JiraCloudClient(http).AddCommentAsync("https://acme.atlassian.net", "email", "secret", "12", "Done", [], Ct);
        Assert.False(result.Success);
        Assert.True(result.MayHavePosted);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task BoundsSuccessfulResponseButAllowsJiraToEchoLongComments()
    {
        using var handler = new Handler(HttpStatusCode.Created, "{\"id\":\"12\",\"echo\":\"" + new string('x', 20000) + "\"}");
        using var http = new HttpClient(handler);
        var client = new JiraCloudClient(http);
        Assert.True((await client.AddCommentAsync("https://acme.atlassian.net", "email", "secret", "12", new string('x', 20000), [], Ct)).Success);
        handler.ResponseBody = new string('x', 256 * 1024 + 1);
        Assert.True((await client.AddCommentAsync("https://acme.atlassian.net", "email", "secret", "12", "Done", [], Ct)).MayHavePosted);
    }

    [Theory]
    [InlineData("http://acme.atlassian.net", "12", "Done", null)]
    [InlineData("https://acme.atlassian.net", "../12", "Done", null)]
    [InlineData("https://acme.atlassian.net", "12", " ", null)]
    [InlineData("https://acme.atlassian.net", "12", "Done", "https://evil.example/shared/session?key=abc")]
    public async Task InvalidInputMakesNoNetworkRequest(string site, string id, string body, string? link)
    {
        using var handler = new Handler(HttpStatusCode.Created, "{\"id\":\"1\"}");
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<JiraConfigException>(() => new JiraCloudClient(http).AddCommentAsync(site, "email", "secret", id, body,
            link is null ? [] : [link], Ct));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task NetworkFailureIsUnconfirmedAndDoesNotExposeException()
    {
        using var handler = new Handler(HttpStatusCode.Created, "") { Throw = true };
        using var http = new HttpClient(handler);
        var result = await new JiraCloudClient(http).AddCommentAsync("https://acme.atlassian.net", "email", "secret", "12", "Done", [], Ct);
        Assert.True(result.MayHavePosted);
        Assert.DoesNotContain("SECRET", result.Message);
        Assert.Equal(1, handler.Calls);
    }

    private sealed class Handler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string ResponseBody { get; set; } = body;
        public bool Throw { get; init; }
        public int Calls { get; private set; }
        public string? Body { get; private set; }
        public string? Method { get; private set; }
        public string? Url { get; private set; }
        public string? Authorization { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Method = request.Method.Method;
            Url = request.RequestUri!.AbsoluteUri;
            Authorization = request.Headers.Authorization?.ToString();
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            if (Throw) throw new HttpRequestException("SECRET network detail");
            return new HttpResponseMessage(status) { Content = new StringContent(ResponseBody) };
        }
    }
}
