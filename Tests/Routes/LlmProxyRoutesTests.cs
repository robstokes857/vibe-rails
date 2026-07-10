using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using VibeRails.Auth;
using VibeRails.DTOs;
using VibeRails.Interfaces;
using VibeRails.Routes;
using VibeRails.Services;
using VibeRails.Services.LlmProxy;
using VibeRails.Utils;
using Xunit;

namespace Tests.Routes;

public class LlmProxyRoutesTests
{
    private const string SessionToken = "test-session-token";
    private const string TabToken = "test-tab-token";

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
        CancellationToken cancellationToken)
    {
        var port = PortFinder.FindOpenPort();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");

        var authService = new Mock<IAuthService>(MockBehavior.Strict);
        authService.Setup(service => service.ValidateToken(SessionToken)).Returns(authenticated);
        authService.Setup(service => service.ValidateTabToken(TabToken)).Returns(authenticated);

        var eventBus = new AppEventBus();
        var events = new List<AppEvent>();
        using var subscription = eventBus.Subscribe(events.Add);
        var upstreamHandler = new RecordingUpstreamHandler();
        using var clientFactory = new StubHttpClientFactory(upstreamHandler);

        builder.Services.AddSingleton(authService.Object);
        builder.Services.AddSingleton<IAppEventBus>(eventBus);
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
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                "/llm/openai/v1/responses?api_key=query-secret");
            request.Headers.TryAddWithoutValidation(LlmProxyCodexConfig.SessionHeaderName, SessionToken);
            request.Headers.TryAddWithoutValidation(LlmProxyCodexConfig.TabHeaderName, TabToken);
            request.Headers.TryAddWithoutValidation("Cookie", "session=cookie-secret");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "upstream-auth-secret");
            request.Content = new StringContent(
                "{\"input\":\"body-secret\"}",
                Encoding.UTF8,
                "application/json");

            using var response = await client.SendAsync(request, cancellationToken);
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

    private sealed class RecordingUpstreamHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount += 1;
            RequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("relay-ok")
            });
        }
    }
}
