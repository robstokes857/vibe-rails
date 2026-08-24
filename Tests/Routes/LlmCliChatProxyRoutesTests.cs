using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TokenSaver;
using Xunit;

namespace Tests.Routes;

public sealed class LlmCliChatProxyRoutesTests
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
    public async Task SubscriptionMode_ForwardsToCliChatProxyWithoutOverwritingGrokAuth()
    {
        var result = await SendAsync(
            enabled: true,
            authenticated: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal(1, result.Upstream.CallCount);
        Assert.Equal(
            "https://cli-chat-proxy.grok.com/v1/chat/completions",
            result.Upstream.RequestUri?.AbsoluteUri);
        Assert.Equal("Bearer", result.Upstream.Authorization?.Scheme);
        Assert.Equal("grok-session-token", result.Upstream.Authorization?.Parameter);
        Assert.Equal("xai-grok-cli", result.Upstream.Header("X-XAI-Token-Auth"));
        Assert.DoesNotContain(
            result.Upstream.Headers.Keys,
            key => key.Equals(LlmProxyCliChatConfig.SessionHeaderName, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            result.Upstream.Headers.Keys,
            key => key.Equals(LlmProxyCliChatConfig.TabHeaderName, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ApiMode_ForwardsToApiXaiWithoutOverwritingAuthorization()
    {
        var result = await SendAsync(
            enabled: true,
            authenticated: true,
            TestContext.Current.CancellationToken,
            mode: CodexLlmProxySettings.ModeApi);

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal(
            "https://api.x.ai/v1/chat/completions",
            result.Upstream.RequestUri?.AbsoluteUri);
        Assert.Equal("grok-session-token", result.Upstream.Authorization?.Parameter);
    }

    private static async Task<ProxyResult> SendAsync(
        bool enabled,
        bool authenticated,
        CancellationToken cancellationToken,
        string mode = CodexLlmProxySettings.ModeSubscription)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var upstream = new RecordingUpstreamHandler();
        using var clientFactory = new StubHttpClientFactory(upstream);
        builder.Services.AddSingleton<IHttpClientFactory>(clientFactory);
        builder.Services.AddSingleton<ILlmProxyAuthGate>(new StubAuthGate());
        builder.Services.AddSingleton<ILlmProxyEventSink>(new RecordingEventSink());
        builder.Services.AddSingleton<ILlmProxyExchangeSink>(new RecordingExchangeSink());
        builder.Services.AddSingleton<ILlmProxySettingsService>(new StubSettingsService(
            new LlmProxySettings(
                CodexLlmProxyEnabled: false,
                CodexLlmProxyMode: CodexLlmProxySettings.ModeSubscription,
                ClaudeLlmProxyEnabled: false,
                GrokLlmProxyEnabled: enabled,
                GrokLlmProxyMode: mode)));

        await using var app = builder.Build();
        LlmCliChatProxyRoutes.Map(app);
        await app.StartAsync(cancellationToken);
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                new Uri(new Uri(app.Urls.First()), "/llm/cli-chat/v1/chat/completions"));
            if (authenticated)
            {
                request.Headers.TryAddWithoutValidation(LlmProxyCliChatConfig.SessionHeaderName, SessionToken);
                request.Headers.TryAddWithoutValidation(LlmProxyCliChatConfig.TabHeaderName, TabToken);
            }
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "grok-session-token");
            request.Headers.TryAddWithoutValidation("X-XAI-Token-Auth", "xai-grok-cli");
            request.Content = new StringContent("{\"model\":\"grok-4.6\"}", Encoding.UTF8, "application/json");

            using var response = await SharedClient.SendAsync(request, cancellationToken);
            await response.Content.ReadAsByteArrayAsync(cancellationToken);
            return new ProxyResult(response.StatusCode, upstream);
        }
        finally
        {
            await app.StopAsync(CancellationToken.None);
        }
    }

    private sealed record ProxyResult(HttpStatusCode StatusCode, RecordingUpstreamHandler Upstream);

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
        public void Record(LlmProxyExchange exchange)
        {
        }
    }

    private sealed class RecordingEventSink : ILlmProxyEventSink
    {
        public void ProxyActivity(
            string source,
            string? label,
            string? target,
            string? status,
            long? bytesSaved = null)
        {
        }

        public void SavingsMeasured(LlmProxySavingsReport report)
        {
        }

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

        public string? Header(string name) =>
            Headers.TryGetValue(name, out var values) ? values.FirstOrDefault() : null;

        protected override Task<HttpResponseMessage> SendAsync(
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
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json")
            });
        }
    }
}
