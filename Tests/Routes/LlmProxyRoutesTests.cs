using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using TokenSaver;
using VibeRails.Auth;
using VibeRails.DTOs;
using VibeRails.Interfaces;
using VibeRails.Services;
using VibeRails.Services.LlmProxy;
using VibeRails.Utils;
using Xunit;

namespace Tests.Routes;

public class LlmProxyRoutesTests
{
    private const string SessionToken = "test-session-token";
    private const string TabToken = "test-tab-token";

    // ONE client for every test in this class: a per-test HttpClient leaves its sockets in
    // TIME_WAIT after dispose, and enough of those clogs the machine's ephemeral TCP ports.
    private static readonly HttpClient SharedClient = new();

    [Fact]
    public void BuildOpenAiUri_SubscriptionModeMirrorsChatGptCodexPath()
    {
        var request = CreateRequest(
            "/llm/openai/backend-api/codex/responses",
            "?conversation_mode=default");

        var target = LlmProxyRoutes.BuildOpenAiUri(
            request,
            CodexLlmProxySettings.ModeSubscription);

        Assert.Equal(
            "https://chatgpt.com/backend-api/codex/responses?conversation_mode=default",
            target.AbsoluteUri);
    }

    [Fact]
    public void BuildOpenAiUri_ApiModeMirrorsOpenAiV1Path()
    {
        var request = CreateRequest("/llm/openai/v1/responses", "?stream=true");

        var target = LlmProxyRoutes.BuildOpenAiUri(request, CodexLlmProxySettings.ModeApi);

        Assert.Equal("https://api.openai.com/v1/responses?stream=true", target.AbsoluteUri);
    }

    [Fact]
    public async Task OpenAiProxy_AuthenticatedEnabledRelay_PublishesSanitizedProxyActivity()
    {
        var result = await SendThroughProxyAsync(
            enabled: true,
            authenticated: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal("relay-ok", result.ResponseBody);
        Assert.Equal(1, result.UpstreamCallCount);
        Assert.Equal(
            "https://api.openai.com/v1/responses?api_key=query-secret",
            result.UpstreamUri?.AbsoluteUri);

        var appEvent = Assert.Single(result.Events);
        Assert.Equal("proxy_activity", appEvent.Type);
        Assert.Equal("Codex proxy", appEvent.Payload.GetProperty("source").GetString());
        Assert.Equal("POST", appEvent.Payload.GetProperty("label").GetString());
        Assert.Equal(
            "https://api.openai.com/v1/responses",
            appEvent.Payload.GetProperty("target").GetString());
        Assert.Equal("200", appEvent.Payload.GetProperty("status").GetString());

        var eventJson = appEvent.Payload.GetRawText();
        Assert.DoesNotContain("query-secret", eventJson);
        Assert.DoesNotContain(SessionToken, eventJson);
        Assert.DoesNotContain(TabToken, eventJson);
        Assert.DoesNotContain("cookie-secret", eventJson);
        Assert.DoesNotContain("upstream-auth-secret", eventJson);
        Assert.DoesNotContain("body-secret", eventJson);
    }

    [Theory]
    [InlineData(true, false, HttpStatusCode.Unauthorized)]
    [InlineData(false, true, HttpStatusCode.NotFound)]
    public async Task OpenAiProxy_RejectedOrDisabledRequest_DoesNotPublishActivity(
        bool enabled,
        bool authenticated,
        HttpStatusCode expectedStatus)
    {
        var result = await SendThroughProxyAsync(
            enabled,
            authenticated,
            TestContext.Current.CancellationToken);

        Assert.Equal(expectedStatus, result.StatusCode);
        Assert.Equal(0, result.UpstreamCallCount);
        Assert.Empty(result.Events);
    }

    [Fact]
    public async Task OpenAiProxy_UpstreamBodyFaultsBeforeFirstByte_Returns502NotFabricated200()
    {
        // The upstream returned 200 headers but its body died before the first relayed byte. The
        // client is still connected, nothing was flushed — the CLI must see a retryable gateway
        // error, never a cleanly-terminated empty "success" (adversarial-review finding,
        // 2026-07-12). No unhandled 500 either. The activity ping precedes the body copy, so it
        // still fires.
        var result = await SendThroughProxyAsync(
            enabled: true,
            authenticated: true,
            TestContext.Current.CancellationToken,
            faultResponseBody: true);

        Assert.Equal(HttpStatusCode.BadGateway, result.StatusCode);
        Assert.Equal(1, result.UpstreamCallCount);
        Assert.Single(result.Events);
    }

    [Fact]
    public async Task OpenAiProxy_UpstreamDiesDuringSend_Returns502AndNoActivityPing()
    {
        // A post-connect transport failure (e.g. NAT/LB reset while uploading the body or awaiting
        // headers) surfaces as HttpRequestException { InnerException: IOException } — the same
        // shape as a client disconnect. With the client still connected, the relay must answer
        // 502, not let Kestrel finalize a default empty 200.
        var result = await SendThroughProxyAsync(
            enabled: true,
            authenticated: true,
            TestContext.Current.CancellationToken,
            failSendWithTransportError: true);

        Assert.Equal(HttpStatusCode.BadGateway, result.StatusCode);
        Assert.Equal(1, result.UpstreamCallCount);
        Assert.Empty(result.Events); // the ping only fires for requests that reached the upstream
    }

    private static HttpRequest CreateRequest(string path, string query)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.QueryString = new QueryString(query);
        return context.Request;
    }

    private static async Task<ProxyResult> SendThroughProxyAsync(
        bool enabled,
        bool authenticated,
        CancellationToken cancellationToken,
        bool faultResponseBody = false,
        bool failSendWithTransportError = false)
    {
        // Port 0 = kernel-assigned: two Kestrel-hosted test classes running in parallel can race a
        // find-then-rebind port picker, so bind ephemeral and read the real address after start.
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var authService = new Mock<IAuthService>(MockBehavior.Strict);
        authService.Setup(service => service.ValidateToken(SessionToken)).Returns(authenticated);
        authService.Setup(service => service.ValidateTabToken(TabToken)).Returns(authenticated);

        var eventBus = new AppEventBus();
        var events = new List<AppEvent>();
        using var subscription = eventBus.Subscribe(events.Add);
        var upstreamHandler = new RecordingUpstreamHandler(faultResponseBody, failSendWithTransportError);
        using var clientFactory = new StubHttpClientFactory(upstreamHandler);

        // The proxy resolves the library's seam interfaces; register the REAL host adapters over
        // the mocked auth service and real event bus so the tests also cover the adapter path
        // (including the payload sanitization asserted below).
        builder.Services.AddSingleton<ILlmProxyAuthGate>(new LlmProxyAuthGateAdapter(authService.Object));
        builder.Services.AddSingleton<ILlmProxyEventSink>(
            new LlmProxyEventSinkAdapter(eventBus, new StubTokenSavingsStore()));
        builder.Services.AddSingleton<IHttpClientFactory>(clientFactory);
        builder.Services.AddSingleton<ILlmProxySettingsService>(
            new StubProxySettingsService(new LlmProxySettings(
                CodexLlmProxyEnabled: enabled,
                CodexLlmProxyMode: CodexLlmProxySettings.ModeApi,
                ClaudeLlmProxyEnabled: false)));

        await using var app = builder.Build();
        LlmProxyRoutes.Map(app);
        await app.StartAsync(cancellationToken);

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                new Uri(new Uri(app.Urls.First()), "/llm/openai/v1/responses?api_key=query-secret"));
            request.Headers.TryAddWithoutValidation(LlmProxyCodexConfig.SessionHeaderName, SessionToken);
            request.Headers.TryAddWithoutValidation(LlmProxyCodexConfig.TabHeaderName, TabToken);
            request.Headers.TryAddWithoutValidation("Cookie", "session=cookie-secret");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "upstream-auth-secret");
            request.Content = new StringContent(
                "{\"input\":\"body-secret\"}",
                Encoding.UTF8,
                "application/json");

            using var response = await SharedClient.SendAsync(request, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

            return new ProxyResult(
                response.StatusCode,
                responseBody,
                upstreamHandler.CallCount,
                upstreamHandler.RequestUri,
                [.. events]);
        }
        finally
        {
            await app.StopAsync(CancellationToken.None);
        }
    }

    private sealed record ProxyResult(
        HttpStatusCode StatusCode,
        string ResponseBody,
        int UpstreamCallCount,
        Uri? UpstreamUri,
        List<AppEvent> Events);

    private sealed class StubProxySettingsService(LlmProxySettings settings)
        : ILlmProxySettingsService
    {
        public LlmProxySettings GetSettings() => settings;
    }

    // Keeps the sink adapter off the real state.db: these tests assert relay/event behavior, not
    // the tally. The recorded values are exposed for tests that do assert savings reporting.
    private sealed class StubTokenSavingsStore : VibeRails.DB.ITokenSavingsStore
    {
        public readonly List<(string Provider, int BytesBefore, int BytesAfter)> Records = [];

        public void Record(string provider, int bytesBefore, int bytesAfter) =>
            Records.Add((provider, bytesBefore, bytesAfter));

        public VibeRails.DB.TokenSavingsTotals GetTotals()
        {
            long before = 0, after = 0;
            foreach (var (_, b, a) in Records)
            {
                before += b;
                after += a;
            }

            return new VibeRails.DB.TokenSavingsTotals(before, after);
        }
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory, IDisposable
    {
        private readonly HttpClient _client;

        public StubHttpClientFactory(HttpMessageHandler handler)
        {
            _client = new HttpClient(handler);
        }

        public HttpClient CreateClient(string name) => _client;

        public void Dispose() => _client.Dispose();
    }

    private sealed class RecordingUpstreamHandler(
        bool faultResponseBody = false,
        bool failSendWithTransportError = false) : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount += 1;
            RequestUri = request.RequestUri;

            // The shape SocketsHttpHandler throws for a post-connect transport failure (reset
            // while uploading the body / awaiting headers): HttpRequestException wrapping IO.
            if (failSendWithTransportError)
                throw new HttpRequestException(
                    "simulated upstream reset", new IOException("connection reset by peer"));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = faultResponseBody
                    ? new ThrowingContent()
                    : new StringContent("relay-ok")
            });
        }
    }

    // Response body that faults as it is copied — the shape of a mid-stream transport teardown (the
    // CLI closing its SSE connection), where the aborted socket read surfaces as an IOException out
    // of CopyToAsync. Lets the test drive the proxy's disconnect-tolerant streaming path.
    private sealed class ThrowingContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => throw new IOException("simulated mid-stream transport teardown");

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
