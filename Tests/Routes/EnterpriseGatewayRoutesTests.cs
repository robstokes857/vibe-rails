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

    private static async Task Exercise(
        string provider, string path, string endpoint, string? session, string? tab, string? storedGateway = null)
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
        if (storedGateway is not null)
        {
            settings.CodexEnterpriseNetworkProxy = settings.ClaudeEnterpriseNetworkProxy = storedGateway;
            settings.OpenCodeEnterpriseNetworkProxy = settings.GrokEnterpriseNetworkProxy = storedGateway;
        }
        var snapshot = LlmProxySettingsService.Resolve(settings);
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
        builder.Services.AddSingleton(Mock.Of<ILlmProxyEventSink>());
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
        context.Request.Headers["Proxy-Authorization"] = "local-proxy-key";
        const string body = "{\"model\":\"enterprise-model\",\"input\":\"unchanged\"}";
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
        if (storedGateway is not null)
        {
            Assert.Equal(502, context.Response.StatusCode);
            Assert.Null(handler.Target);
            Assert.Contains("Enterprise Network Proxy", Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray()));
            return;
        }
        var host = provider switch { "openai" => "codex", "anthropic" => "claude", "zai" or "xai" => "opencode", _ => "grok" };
        Assert.Equal($"https://{host}.gateway.example/v1/{endpoint}?stream=true", handler.Target?.AbsoluteUri);
        Assert.Equal(body, handler.Body);
        Assert.Equal("Bearer provider-key", handler.Authorization);
        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal("data: gateway-response\n\n", Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray()));
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        internal Uri? Target;
        internal string? Body;
        internal string? Authorization;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Target = request.RequestUri;
            Body = await request.Content!.ReadAsStringAsync(token);
            Authorization = request.Headers.Authorization?.ToString();
            foreach (var name in new[] { "viberails_session", "viberails_tab", "viberails_terminal_session", "Cookie", "Proxy-Authorization" })
                Assert.False(request.Headers.Contains(name), name);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("data: gateway-response\n\n", Encoding.UTF8, "text/event-stream")
            };
        }
    }
}
