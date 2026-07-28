using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TokenSaver;
using TokenSaver.Minify;
using TokenSaver.Pipeline;
using Xunit;

namespace Tests.Routes;

public sealed class LlmZaiProxyRoutesTests
{
    private const string SessionToken = "test-session-token";
    private const string TabToken = "test-tab-token";
    private static readonly HttpClient SharedClient = new();

    [Fact]
    public async Task DisabledProxy_ReturnsNotFoundWithoutCallingUpstream()
    {
        var result = await SendAsync(
            enabled: false,
            authenticated: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, result.StatusCode);
        Assert.Equal(0, result.Upstream.CallCount);
    }

    [Fact]
    public async Task MissingProxyCredentials_ReturnsUnauthorizedWithoutCallingUpstream()
    {
        var result = await SendAsync(
            enabled: true,
            authenticated: false,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, result.StatusCode);
        Assert.Equal(0, result.Upstream.CallCount);
    }

    [Fact]
    public async Task AuthenticatedRequest_RewritesAndRelaysWithoutLeakingLocalCredentials()
    {
        var result = await SendAsync(
            enabled: true,
            authenticated: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal(1, result.Upstream.CallCount);
        Assert.Equal(
            "https://api.z.ai/api/paas/v4/chat/completions?api-version=test",
            result.Upstream.RequestUri?.AbsoluteUri);
        Assert.Equal("Bearer", result.Upstream.Authorization?.Scheme);
        Assert.Equal("upstream-secret", result.Upstream.Authorization?.Parameter);
        Assert.DoesNotContain(
            result.Upstream.Headers.Keys,
            key => key.Equals(LlmProxyZaiConfig.SessionHeaderName, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            result.Upstream.Headers.Keys,
            key => key.Equals(LlmProxyZaiConfig.TabHeaderName, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            result.Upstream.Headers.Keys,
            key => key.Equals("Cookie", StringComparison.OrdinalIgnoreCase));

        var forwarded = Encoding.UTF8.GetString(result.Upstream.RequestBody);
        Assert.Contains("hi\\ndone", forwarded, StringComparison.Ordinal);
        Assert.DoesNotContain("u001b", forwarded, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(result.Upstream.RequestBody.Length, result.Upstream.ContentLength);

        var savings = Assert.Single(result.Events.Savings);
        Assert.Equal("zai", savings.Provider);
        Assert.True(savings.BytesSaved > 0);
        var activity = Assert.Single(result.Events.Activities);
        Assert.Equal("OpenCode proxy", activity.Source);
        Assert.Equal("https://api.z.ai/api/paas/v4/chat/completions", activity.Target);
        Assert.DoesNotContain("api-version", activity.Target, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TokenSaverDisabled_StillRecordsTheExchange()
    {
        var result = await SendAsync(
            enabled: true,
            authenticated: true,
            TestContext.Current.CancellationToken,
            tokenSaverEnabled: false);

        var exchange = Assert.Single(result.Exchanges);
        Assert.Equal("zai", exchange.Provider);
        Assert.Equal("/llm/zai/api/paas/v4/chat/completions", exchange.Path);
        Assert.Equal(200, exchange.StatusCode);
        Assert.Equal("{\"ok\":true}", exchange.Response);
    }

    private static async Task<ProxyResult> SendAsync(
        bool enabled,
        bool authenticated,
        CancellationToken cancellationToken,
        bool tokenSaverEnabled = true)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var upstream = new RecordingUpstreamHandler();
        using var clientFactory = new StubHttpClientFactory(upstream);
        var events = new RecordingEventSink();
        var exchanges = new RecordingExchangeSink();
        var plan = CompressionPlan.FromLegacy(
            MinifyFlags.Default,
            default,
            zaiAllowlist: ["bash"]);
        builder.Services.AddSingleton<IHttpClientFactory>(clientFactory);
        builder.Services.AddSingleton<ILlmProxyAuthGate>(new StubAuthGate());
        builder.Services.AddSingleton<ILlmProxyEventSink>(events);
        builder.Services.AddSingleton<ILlmProxyExchangeSink>(exchanges);
        builder.Services.AddSingleton<ILlmProxySettingsService>(new StubSettingsService(
            new LlmProxySettings(
                CodexLlmProxyEnabled: false,
                CodexLlmProxyMode: CodexLlmProxySettings.ModeSubscription,
                ClaudeLlmProxyEnabled: false,
                OpenCodeLlmProxyEnabled: enabled,
                OpenCodeTokenSaverEnabled: enabled && tokenSaverEnabled,
                TokenSaverPlan: plan)));

        await using var app = builder.Build();
        LlmZaiProxyRoutes.Map(app);
        await app.StartAsync(cancellationToken);
        try
        {
            const string body = """
                {"model":"glm-5.2","messages":[
                  {"role":"assistant","content":null,"tool_calls":[{"id":"call_1","type":"function","function":{"name":"bash","arguments":"{\"command\":\"printf hi\"}"}}]},
                  {"role":"tool","tool_call_id":"call_1","content":"\u001b[1mhi\u001b[0m  \r\ndone"}
                ]}
                """;
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                new Uri(new Uri(app.Urls.First()),
                    "/llm/zai/api/paas/v4/chat/completions?api-version=test"));
            if (authenticated)
            {
                request.Headers.TryAddWithoutValidation(LlmProxyZaiConfig.SessionHeaderName, SessionToken);
                request.Headers.TryAddWithoutValidation(LlmProxyZaiConfig.TabHeaderName, TabToken);
            }
            request.Headers.TryAddWithoutValidation("Cookie", "viberails_session=cookie-secret");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "upstream-secret");
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

            using var response = await SharedClient.SendAsync(request, cancellationToken);
            await response.Content.ReadAsByteArrayAsync(cancellationToken);
            return new ProxyResult(response.StatusCode, upstream, events, [.. exchanges.Records]);
        }
        finally
        {
            await app.StopAsync(CancellationToken.None);
        }
    }

    private sealed record ProxyResult(
        HttpStatusCode StatusCode,
        RecordingUpstreamHandler Upstream,
        RecordingEventSink Events,
        List<LlmProxyExchange> Exchanges);

    private sealed class StubSettingsService(LlmProxySettings settings) : ILlmProxySettingsService
    {
        public LlmProxySettings GetSettings() => settings;
    }

    private sealed class StubAuthGate : ILlmProxyAuthGate
    {
        public bool ValidateSessionToken(string? token) => token == SessionToken;

        public bool ValidateTabToken(string? token) => token == TabToken;
    }

    private sealed class RecordingExchangeSink : ILlmProxyExchangeSink
    {
        public List<LlmProxyExchange> Records { get; } = [];

        public void Record(LlmProxyExchange exchange) => Records.Add(exchange);
    }

    private sealed class RecordingEventSink : ILlmProxyEventSink
    {
        public List<LlmProxySavingsReport> Savings { get; } = [];
        public List<(string Source, string? Target)> Activities { get; } = [];

        public void ProxyActivity(
            string source,
            string? label,
            string? target,
            string? status,
            long? bytesSaved = null) => Activities.Add((source, target));

        public void SavingsMeasured(LlmProxySavingsReport report) => Savings.Add(report);

        public void Diagnostic(string source, string message, Exception? exception = null)
        {
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory, IDisposable
    {
        private readonly HttpClient _client = new(handler);

        public HttpClient CreateClient(string name) => _client;

        public void Dispose() => _client.Dispose();
    }

    private sealed class RecordingUpstreamHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public Uri? RequestUri { get; private set; }
        public AuthenticationHeaderValue? Authorization { get; private set; }
        public Dictionary<string, string[]> Headers { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
        public byte[] RequestBody { get; private set; } = [];
        public long? ContentLength { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            RequestUri = request.RequestUri;
            Authorization = request.Headers.Authorization;
            Headers = request.Headers.ToDictionary(
                header => header.Key,
                header => header.Value.ToArray(),
                StringComparer.OrdinalIgnoreCase);
            ContentLength = request.Content?.Headers.ContentLength;
            RequestBody = request.Content is null
                ? []
                : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json")
            };
        }
    }
}
