using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using TokenSaver;
using VibeRails.Services.LlmProxy;
using VibeRails.Utils;
using Xunit;

namespace Tests.Routes;

public sealed class EnterpriseGatewayRoutesTests
{
    [Fact]
    public async Task NestedClaudeSendsInheritedCorporateCredentialsOnlyToTheCapturedGateway()
    {
        await Exercise("anthropic", "/llm/anthropic/v1/messages", "messages", "session", "tab",
            inheritedClaudeBase: "http://127.0.0.1:4321/llm/anthropic",
            capturedClaudeUpstream: "https://claude.gateway.example");
    }

    [Theory]
    [InlineData("")]
    [InlineData(LlmProxyUpstream.Unavailable)]
    [InlineData("https://user:secret@corporate.example/v1")]
    [InlineData("http://127.0.0.1:4321/llm/anthropic")]
    public async Task NestedClaudeNeverForwardsCorporateCredentialsWhenItsUpstreamIsUnknown(string? captured)
    {
        await Exercise("anthropic", "/llm/anthropic/v1/messages", "messages", "session", "tab",
            inheritedClaudeBase: "http://127.0.0.1:4321/llm/anthropic", capturedClaudeUpstream: captured,
            unknownUpstream: true);
    }

    [Fact]
    public async Task NestedClaudeUnderAReleaseWithoutTheCaptureUsesThatReleasesOwnDestination()
    {
        // An installed release from before the capture sets the relay URL and nothing else; its relay
        // forwarded to the provider default, so this build does the same instead of answering 502.
        await Exercise("anthropic", "/llm/anthropic/v1/messages", "messages", "session", "tab",
            clearGateway: true, inheritedClaudeBase: "http://127.0.0.1:4321/llm/anthropic",
            capturedClaudeUpstream: null);
    }

    [Fact]
    public async Task NestedClaudeUsesPublicProviderOnlyWithAnExplicitDefaultMarker()
    {
        await Exercise("anthropic", "/llm/anthropic/v1/messages", "messages", "session", "tab",
            clearGateway: true, inheritedClaudeBase: "http://127.0.0.1:4321/llm/anthropic",
            capturedClaudeUpstream: LlmProxyUpstream.ProviderDefault);
    }

    [Theory]
    [InlineData("openai", "/llm/openai/v1/responses", "responses", "api-key")]
    [InlineData("openai", "/llm/openai/v1/responses", "responses", "x-api-key")]
    [InlineData("anthropic", "/llm/anthropic/v1/messages", "messages", "api-key")]
    [InlineData("anthropic", "/llm/anthropic/v1/messages", "messages", "X-Corporate-Key")]
    public async Task RawApiKeyAndTokenSaverUseTheSameGatewayRequest(string provider, string path, string endpoint, string header)
    {
        await Exercise(provider, path, endpoint, "session", "tab", bearer: "gateway-secret", compress: true, apiKeyHeader: header);
    }

    [Fact]
    public async Task InvalidSavedHeaderFailsClosedBeforeSendingTheCredential()
    {
        await Exercise("openai", "/llm/openai/v1/responses", "responses", "session", "tab",
            bearer: "gateway-secret", apiKeyHeader: "Host", invalidAuth: true);
    }

    [Fact]
    public async Task UncompressedGatewayRequestStillReportsProxyActivity()
    {
        await Exercise("openai", "/llm/openai/v1/responses", "responses", "session", "tab",
            bearer: "gateway-secret", apiKeyHeader: "api-key");
    }

    [Theory]
    [InlineData("openai", "/llm/openai/v1/responses", "responses")]
    [InlineData("anthropic", "/llm/anthropic/v1/messages", "messages")]
    public async Task GatewayBearerOverridesProviderCredentialsAfterTokenSaver(string provider, string path, string endpoint)
    {
        await Exercise(provider, path, endpoint, "session", "tab", bearer: "gateway-secret", compress: true);
    }

    [Theory]
    [InlineData("openai", "/llm/openai/v1/responses", "responses")]
    [InlineData("anthropic", "/llm/anthropic/v1/messages", "messages")]
    public async Task RemovedGatewayNeverReceivesSavedBearerAtPublicProvider(string provider, string path, string endpoint)
    {
        await Exercise(provider, path, endpoint, "session", "tab", bearer: "dormant-secret", clearGateway: true);
    }

    [Theory]
    [InlineData("openai", "/llm/openai/v1/responses", "responses")]
    [InlineData("anthropic", "/llm/anthropic/v1/messages", "messages")]
    [InlineData("zai", "/llm/zai/api/paas/v4/chat/completions", "chat/completions")]
    [InlineData("xai", "/llm/xai/v1/chat/completions", "chat/completions")]
    [InlineData("cli-chat", "/llm/cli-chat/v1/responses", "responses")]
    public async Task EveryProviderUsesItsGatewayAndStripsLocalCredentials(string provider, string path, string endpoint)
    {
        await Exercise(provider, path, endpoint, "session", "tab");
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("session", null)]
    [InlineData(null, "tab")]
    public async Task GatewayDoesNotBypassAuthentication(string? session, string? tab)
    {
        await Exercise("openai", "/llm/openai/v1/responses", "responses", session, tab);
    }

    [Theory]
    [InlineData("openai", "/llm/openai/v1/responses")]
    [InlineData("anthropic", "/llm/anthropic/v1/messages")]
    [InlineData("zai", "/llm/zai/api/paas/v4/chat/completions")]
    [InlineData("xai", "/llm/xai/v1/chat/completions")]
    [InlineData("cli-chat", "/llm/cli-chat/v1/responses")]
    public async Task HandEditedInvalidGatewayFailsClosedInsteadOfUsingTheProviderDefault(string provider, string path)
    {
        await Exercise(provider, path, "unused", "session", "tab", storedGateway: "genai.example.test/v1");
    }

    [Fact]
    public async Task ClaudeGatewayGivenWithoutV1StillReachesMessages()
    {
        // The form ANTHROPIC_BASE_URL takes; Claude Code itself appends /v1.
        await Exercise("anthropic", "/llm/anthropic/v1/messages", "messages", "session", "tab",
            claudeGateway: "https://claude.gateway.example");
    }

    [Fact]
    public async Task GatewayAuthChangeKeepsAnExistingCodexTerminalEndpoint()
    {
        await Exercise("openai", "/llm/openai/backend-api/codex/responses", "responses", "session", "tab",
            bearer: "gateway-secret");
    }

    [Theory]
    // A gateway marked as supporting tool search that does not: name the switch, once, in the log.
    [InlineData("https://claude.gateway.example/v1", 400, "{\"error\":{\"message\":\"Unknown content type: tool_reference\"}}", true)]
    [InlineData("https://claude.gateway.example/v1", 400, "{\"error\":{\"message\":\"max_tokens is required\"}}", false)]
    [InlineData("https://claude.gateway.example/v1", 200, "data: {\"type\":\"tool_reference\"}\n\n", false)]
    [InlineData("", 400, "{\"error\":{\"message\":\"Unknown content type: tool_reference\"}}", false)]
    public async Task ToolReferenceRejectionNamesTheSwitchWithoutChangingTheResponse(
        string gateway, int status, string upstreamBody, bool warns)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        var snapshot = LlmProxySettingsService.Resolve(new Settings
        {
            ClaudeTokenSaverEnabled = false,
            ClaudeEnterpriseNetworkProxy = gateway,
            ClaudeGatewaySupportsToolSearch = true
        });
        var settingsService = new Mock<ILlmProxySettingsService>();
        settingsService.Setup(s => s.GetSettings()).Returns(snapshot);
        var auth = new Mock<ILlmProxyAuthGate>();
        auth.Setup(a => a.ValidateTokens(It.IsAny<string?>(), It.IsAny<string?>())).Returns(true);
        var handler = new FixedResponseHandler((HttpStatusCode)status, upstreamBody);
        using var client = new HttpClient(handler);
        var clients = new Mock<IHttpClientFactory>();
        clients.Setup(c => c.CreateClient("llm-proxy")).Returns(client);
        var events = new Mock<ILlmProxyEventSink>();
        builder.Services.AddSingleton(settingsService.Object);
        builder.Services.AddSingleton(auth.Object);
        builder.Services.AddSingleton(clients.Object);
        builder.Services.AddSingleton(events.Object);
        builder.Services.AddSingleton(Mock.Of<ILlmProxyExchangeSink>());
        await using var app = builder.Build();
        LlmAnthropicProxyRoutes.Map(app);

        var context = new DefaultHttpContext { RequestServices = app.Services };
        context.Request.Method = "POST";
        context.Request.Path = "/llm/anthropic/v1/messages";
        context.Request.Headers[LlmProxyCodexConfig.SessionHeaderName] = "session";
        context.Request.Headers[LlmProxyCodexConfig.TabHeaderName] = "tab";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"model\":\"m\",\"messages\":[]}"));
        context.Request.ContentType = "application/json";
        context.Response.Body = new MemoryStream();
        var route = Assert.Single(((IEndpointRouteBuilder)app).DataSources.SelectMany(s => s.Endpoints));
        await route.RequestDelegate!(context);

        Assert.Equal(1, handler.Calls);
        Assert.Equal(status, context.Response.StatusCode);
        Assert.Equal(upstreamBody, Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray()));
        events.Verify(e => e.Warning("Claude proxy", It.Is<string>(m =>
            m.Contains("tool_reference") && m.Contains("\"Gateway supports tool search\"") && m.Contains("Settings → LLMs → Claude"))),
            warns ? Times.Once : Times.Never);
    }

    private sealed class FixedResponseHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private static async Task Exercise(
        string provider, string path, string endpoint, string? session, string? tab, string? storedGateway = null,
        string? claudeGateway = null, string? bearer = null, bool compress = false, bool clearGateway = false,
        string? apiKeyHeader = null, bool invalidAuth = false, string? inheritedClaudeBase = null,
        string? capturedClaudeUpstream = null, bool unknownUpstream = false)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        var settings = new Settings
        {
            CodexLlmProxyEnabled = false, ClaudeLlmProxyEnabled = false,
            OpenCodeLlmProxyEnabled = false, GrokLlmProxyEnabled = false,
            CodexLlmProxyMode = "api",
            ClaudeTokenSaverEnabled = false, CodexTokenSaverEnabled = false,
            OpenCodeTokenSaverEnabled = false, GrokTokenSaverEnabled = false,
            CodexEnterpriseNetworkProxy = "https://codex.gateway.example/v1",
            ClaudeEnterpriseNetworkProxy = "https://claude.gateway.example/v1",
            OpenCodeEnterpriseNetworkProxy = "https://opencode.gateway.example/v1",
            GrokEnterpriseNetworkProxy = "https://grok.gateway.example/v1"
        };
        if (claudeGateway is not null)
            settings.ClaudeEnterpriseNetworkProxy = claudeGateway;
        if (storedGateway is not null)
        {
            settings.CodexEnterpriseNetworkProxy = settings.ClaudeEnterpriseNetworkProxy = storedGateway;
            settings.OpenCodeEnterpriseNetworkProxy = settings.GrokEnterpriseNetworkProxy = storedGateway;
        }
        settings.CodexGatewayBearerToken = settings.ClaudeGatewayBearerToken = bearer ?? "";
        if (apiKeyHeader is not null)
        {
            settings.CodexGatewayAuthMode = settings.ClaudeGatewayAuthMode = "api-key";
            settings.CodexGatewayApiKeyHeader = settings.ClaudeGatewayApiKeyHeader = apiKeyHeader;
        }
        settings.CodexTokenSaverEnabled = settings.ClaudeTokenSaverEnabled = compress;
        if (clearGateway) settings.CodexEnterpriseNetworkProxy = settings.ClaudeEnterpriseNetworkProxy = "";
        if (inheritedClaudeBase is not null) settings.ClaudeEnterpriseNetworkProxy = "";
        var snapshot = LlmProxySettingsService.Resolve(settings, inheritedClaudeBaseUrl: inheritedClaudeBase,
            capturedClaudeUpstream: capturedClaudeUpstream);
        var settingsService = new Mock<ILlmProxySettingsService>();
        settingsService.Setup(s => s.GetSettings()).Returns(snapshot);
        var auth = new Mock<ILlmProxyAuthGate>();
        auth.Setup(a => a.ValidateTokens(It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns((string? s, string? t) => s == "session" && t == "tab");
        var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        var clients = new Mock<IHttpClientFactory>();
        clients.Setup(c => c.CreateClient("llm-proxy")).Returns(client);
        builder.Services.AddSingleton(settingsService.Object);
        builder.Services.AddSingleton(auth.Object);
        builder.Services.AddSingleton(clients.Object);
        var events = new Mock<ILlmProxyEventSink>();
        builder.Services.AddSingleton(events.Object);
        builder.Services.AddSingleton(Mock.Of<ILlmProxyExchangeSink>());
        await using var app = builder.Build();
        switch (provider)
        {
            case "openai": LlmProxyRoutes.Map(app); break;
            case "anthropic": LlmAnthropicProxyRoutes.Map(app); break;
            case "zai": LlmZaiProxyRoutes.Map(app); break;
            case "xai": LlmXaiProxyRoutes.Map(app); break;
            default: LlmCliChatProxyRoutes.Map(app); break;
        }

        var context = new DefaultHttpContext { RequestServices = app.Services };
        context.Request.Method = "POST";
        context.Request.Path = path;
        context.Request.QueryString = new QueryString("?stream=true");
        if (session is not null) context.Request.Headers[LlmProxyCodexConfig.SessionHeaderName] = session;
        if (tab is not null) context.Request.Headers[LlmProxyCodexConfig.TabHeaderName] = tab;
        context.Request.Headers[LlmProxyCodexConfig.TerminalSessionHeaderName] = "local-terminal";
        context.Request.Headers.Cookie = "local-cookie";
        context.Request.Headers.Authorization = "Bearer provider-key";
        context.Request.Headers["x-api-key"] = "provider-secret";
        if (inheritedClaudeBase is not null && !clearGateway)
            context.Request.Headers["x-corporate-key"] = "corporate-secret";
        context.Request.Headers["chatgpt-account-id"] = "personal-account";
        context.Request.Headers["Proxy-Authorization"] = "local-proxy-key";
        var body = !compress ? "{\"model\":\"enterprise-model\",\"input\":\"unchanged\"}" : provider == "openai" ? """
            {"model":"enterprise-model","input":[
              {"type":"function_call","name":"shell_command","call_id":"c1","arguments":"{}"},
              {"type":"function_call_output","call_id":"c1","output":"\u001b[31mred\u001b[0m  \r\n"}]}
            """ : """
            {"model":"enterprise-model","messages":[
              {"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"ls"}}]},
              {"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"\u001b[31mred\u001b[0m  \r\n"}]}]}
            """;
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Request.ContentLength = context.Request.Body.Length;
        context.Request.ContentType = "application/json";
        context.Response.Body = new MemoryStream();
        var route = Assert.Single(((IEndpointRouteBuilder)app).DataSources.SelectMany(s => s.Endpoints));
        await route.RequestDelegate!(context);

        if (session is null || tab is null)
        {
            Assert.Equal(401, context.Response.StatusCode);
            Assert.Null(handler.Target);
            return;
        }
        if (unknownUpstream)
        {
            Assert.Equal(502, context.Response.StatusCode);
            Assert.Null(handler.Target);
            var message = Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());
            Assert.Contains("original upstream destination", message);
            Assert.DoesNotContain("secret", message);
            return;
        }
        if (storedGateway is not null)
        {
            Assert.Equal(502, context.Response.StatusCode);
            Assert.Null(handler.Target);
            Assert.Contains("Enterprise Network Proxy", Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray()));
            return;
        }
        if (invalidAuth)
        {
            Assert.Equal(502, context.Response.StatusCode);
            Assert.Null(handler.Target);
            return;
        }
        var host = provider switch { "openai" => "codex", "anthropic" => "claude", "zai" or "xai" => "opencode", _ => "grok" };
        Assert.Equal(clearGateway ? $"https://{(provider == "openai" ? "api.openai.com" : "api.anthropic.com")}/v1/{endpoint}?stream=true"
            : $"https://{host}.gateway.example/v1/{endpoint}?stream=true", handler.Target?.AbsoluteUri);
        if (compress)
        {
            Assert.NotEqual(body, handler.Body);
            using var transformed = System.Text.Json.JsonDocument.Parse(handler.Body!);
            var toolOutput = provider == "openai"
                ? transformed.RootElement.GetProperty("input")[1].GetProperty("output").GetString()
                : transformed.RootElement.GetProperty("messages")[1].GetProperty("content")[0].GetProperty("content").GetString();
            // The curated pipeline retains ANSI styling, but removes trailing spaces/newlines.
            Assert.Equal("\u001b[31mred\u001b[0m", toolOutput);
            events.Verify(e => e.SavingsMeasured(It.Is<LlmProxySavingsReport>(s => s.BytesAfter < s.BytesBefore)), Times.Once);
        }
        else Assert.Equal(body, handler.Body);
        if (inheritedClaudeBase is not null && !clearGateway)
        {
            Assert.Equal("corporate-secret", handler.Headers["x-corporate-key"]);
            Assert.Equal("provider-secret", handler.Headers["x-api-key"]);
        }
        Assert.Equal(bearer is not null && !clearGateway
            ? apiKeyHeader is null ? $"Bearer {bearer}" : null
            : "Bearer provider-key", handler.Authorization);
        if (bearer is not null && !clearGateway)
        {
            if (apiKeyHeader is not null)
                Assert.Equal(bearer, handler.Headers[apiKeyHeader]);
            if (!string.Equals(apiKeyHeader, "x-api-key", StringComparison.OrdinalIgnoreCase))
                Assert.False(handler.HasProviderApiKey);
            Assert.False(handler.HasProviderAccount);
        }
        events.Verify(e => e.ProxyActivity(It.IsAny<string>(), "POST", It.IsAny<string>(), "200", It.IsAny<long?>()), Times.Once);
        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal("data: gateway-response\n\n", Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray()));
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        internal Uri? Target;
        internal string? Body;
        internal string? Authorization;
        internal bool HasProviderApiKey;
        internal bool HasProviderAccount;
        internal Dictionary<string, string> Headers = new(StringComparer.OrdinalIgnoreCase);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Target = request.RequestUri;
            Body = await request.Content!.ReadAsStringAsync(token);
            Authorization = request.Headers.Authorization?.ToString();
            HasProviderApiKey = request.Headers.Contains("x-api-key");
            HasProviderAccount = request.Headers.Contains("chatgpt-account-id");
            Headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
            foreach (var name in new[] { "viberails_session", "viberails_tab", "viberails_terminal_session", "Cookie", "Proxy-Authorization" })
                Assert.False(request.Headers.Contains(name), name);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("data: gateway-response\n\n", Encoding.UTF8, "text/event-stream")
            };
        }
    }
}
