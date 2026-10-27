using System.Net;
using Moq;
using VibeRails.Services.LlmProxy;
using Xunit;

namespace Tests.Services.LlmProxy;

public sealed class EnterpriseNetworkProxyProbeTests
{
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    // Claude gateways are handed out with and without /v1; both reach /v1/messages.
    [InlineData("claude", "https://genai.example.test", "https://genai.example.test/v1/messages")]
    [InlineData("claude", "https://genai.example.test/v1/", "https://genai.example.test/v1/messages")]
    [InlineData("claude", "https://gw.example/anthropic", "https://gw.example/anthropic/v1/messages")]
    // OpenAI-style bases are used as entered, the way OPENAI_BASE_URL is.
    [InlineData("codex", "https://genai.example.test/v1", "https://genai.example.test/v1/responses")]
    [InlineData("opencode", "https://gw.example/v1", "https://gw.example/v1/chat/completions")]
    [InlineData("grok", "https://gw.example/v1", "https://gw.example/v1/responses")]
    public async Task SendsOneCredentialFreePostToTheRealEndpoint(string provider, string url, string expected)
    {
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var result = await Probe(handler).TestAsync(provider, url, Ct);

        Assert.True(result.Ok);
        Assert.Equal("reachable", result.Status);
        Assert.Equal(401, result.StatusCode);
        Assert.Contains("credentials", result.Message);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(expected, request.Uri);
        Assert.Equal("POST", request.Method);
        Assert.Equal("{}", request.Body);
        Assert.False(request.HadAuthorization);
    }

    [Fact]
    public async Task AMissingV1IsFoundAndOffered()
    {
        var handler = new Handler(uri => new HttpResponseMessage(
            uri.AbsolutePath == "/v1/responses" ? HttpStatusCode.Unauthorized : HttpStatusCode.NotFound));

        var result = await Probe(handler).TestAsync("codex", "https://genai.example.test", Ct);

        Assert.False(result.Ok);
        Assert.Equal("not-found", result.Status);
        Assert.Equal("https://genai.example.test/v1", result.SuggestedUrl);
        Assert.Equal(["https://genai.example.test/responses", "https://genai.example.test/v1/responses"],
            handler.Requests.Select(r => r.Uri));
    }

    [Fact]
    public async Task NoSuggestionWhenTheAlternateDoesNotAnswerEither()
    {
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var result = await Probe(handler).TestAsync("codex", "https://genai.example.test", Ct);

        Assert.Equal("not-found", result.Status);
        Assert.Null(result.SuggestedUrl);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, true, "reachable")]
    [InlineData(HttpStatusCode.Forbidden, true, "reachable")]
    [InlineData(HttpStatusCode.MethodNotAllowed, false, "not-found")]
    [InlineData(HttpStatusCode.ProxyAuthenticationRequired, false, "proxy-auth")]
    [InlineData(HttpStatusCode.Found, false, "redirect")]
    [InlineData(HttpStatusCode.BadGateway, false, "error")]
    public async Task StatusCodesAreExplained(HttpStatusCode code, bool ok, string status)
    {
        var result = await Probe(new Handler(_ => new HttpResponseMessage(code))).TestAsync("claude", "https://gw.example/v1", Ct);

        Assert.Equal(ok, result.Ok);
        Assert.Equal(status, result.Status);
        Assert.Equal((int)code, result.StatusCode);
    }

    [Theory]
    [InlineData(HttpRequestError.NameResolutionError, "Could not find gw.example")]
    [InlineData(HttpRequestError.SecureConnectionError, "trusted HTTPS connection")]
    [InlineData(HttpRequestError.ConnectionError, "Could not connect")]
    [InlineData(HttpRequestError.ProxyTunnelError, "network proxy refused")]
    public async Task TransportFailuresAreNamed(HttpRequestError error, string expected)
    {
        var handler = new Handler(_ => throw new HttpRequestException(error, "transport failed"));

        var result = await Probe(handler).TestAsync("codex", "https://gw.example/v1", Ct);

        Assert.False(result.Ok);
        Assert.Equal("unreachable", result.Status);
        Assert.Contains(expected, result.Message);
    }

    [Fact]
    public async Task AnUnansweredRequestIsATimeout()
    {
        var handler = new Handler(_ => throw new TaskCanceledException("deadline"));

        var result = await Probe(handler).TestAsync("codex", "https://gw.example/v1", Ct);

        Assert.Equal("timeout", result.Status);
    }

    [Theory]
    [InlineData("  ", "blank")]
    [InlineData("genai.example.test/v1", "invalid")]
    [InlineData("https://user:secret@gw.example/v1", "invalid")]
    public async Task NothingIsSentForABlankOrInvalidUrl(string url, string status)
    {
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK));

        var result = await Probe(handler).TestAsync("codex", url, Ct);

        Assert.Equal(status, result.Status);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("codex", true)]
    [InlineData("CLAUDE", true)]
    [InlineData("opencode", true)]
    [InlineData("grok", true)]
    [InlineData("copilot", false)]
    [InlineData(null, false)]
    public void OnlySettingsProvidersAreAccepted(string? provider, bool known) =>
        Assert.Equal(known, EnterpriseNetworkProxyProbe.IsKnownProvider(provider));

    private static EnterpriseNetworkProxyProbe Probe(Handler handler)
    {
        var clients = new Mock<IHttpClientFactory>();
        clients.Setup(c => c.CreateClient("llm-proxy")).Returns(() => new HttpClient(handler, disposeHandler: false));
        return new EnterpriseNetworkProxyProbe(clients.Object);
    }

    private sealed record Sent(string Uri, string Method, string? Body, bool HadAuthorization);

    private sealed class Handler(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Sent> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new(request.RequestUri!.AbsoluteUri, request.Method.Method, body, request.Headers.Authorization is not null));
            return respond(request.RequestUri);
        }
    }
}
