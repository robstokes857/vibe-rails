using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using TokenSaver;
using TokenSaver.Minify;
using VibeRails.Auth;
using VibeRails.DTOs;
using VibeRails.Interfaces;
using VibeRails.Services;
using VibeRails.Services.LlmProxy;
using VibeRails.Utils;
using Xunit;

namespace Tests.Routes;

/// <summary>
/// Drives the Claude/Anthropic relay end-to-end through real Kestrel and asserts the token saver's
/// wire behavior: qualifying <c>/v1/messages</c> bodies reach the upstream minified with a correct
/// Content-Length, while every other path — saver off, invalid JSON, non-messages endpoints —
/// forwards byte-identical (fail-open, token_saving_plan.md §6). Also pins the metrics contract:
/// measured requests hit the savings store, and the activity ping carries savings only for a
/// request that actually shrank.
/// </summary>
public class LlmAnthropicProxyRoutesTests
{
    private const string SessionToken = "test-session-token";
    private const string TabToken = "test-tab-token";

    // ONE client for every test in this class: a per-test HttpClient leaves its sockets in
    // TIME_WAIT after dispose, and enough of those clogs the machine's ephemeral TCP ports.
    private static readonly HttpClient SharedClient = new();

    // JSON-escaped dirty Bash output: SGR + trailing spaces + CRLF → minifies to "hi\ndone".
    private const string DirtyToolOutput = "\\u001b[1mhi\\u001b[0m  \\r\\ndone";

    private static string MessagesBody(string toolResultContent) =>
        $$$"""
        {"model":"claude-sonnet-5","messages":[
          {"role":"assistant","content":[{"type":"tool_use","id":"tu_1","name":"Bash","input":{"command":"ls"}}]},
          {"role":"user","content":[{"type":"tool_result","tool_use_id":"tu_1","content":"{{{toolResultContent}}}"}]}
        ]}
        """;

    [Fact]
    public async Task TokenSaver_RewritesBashToolResult_BeforeForwardingUpstream()
    {
        var body = MessagesBody(DirtyToolOutput);

        var result = await SendAsync(
            "/llm/anthropic/v1/messages", body, tokenSaverEnabled: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        var upstreamBody = Encoding.UTF8.GetString(result.UpstreamBody);
        Assert.NotEqual(body, upstreamBody);
        Assert.Contains("hi\\ndone", upstreamBody, StringComparison.Ordinal);
        Assert.DoesNotContain("u001b", upstreamBody, StringComparison.OrdinalIgnoreCase);
        // The rewritten body ships with an exact Content-Length, not chunked.
        Assert.Equal(result.UpstreamBody.Length, result.UpstreamContentLength);

        var record = Assert.Single(result.Savings.Records);
        Assert.Equal("anthropic", record.Provider);
        Assert.True(record.BytesBefore > record.BytesAfter);

        var ping = Assert.Single(result.Events);
        Assert.Equal("proxy_activity", ping.Type);
        Assert.Equal("Claude proxy", ping.Payload.GetProperty("source").GetString());
        Assert.Equal(
            record.BytesBefore - record.BytesAfter,
            ping.Payload.GetProperty("bytesSaved").GetInt64());
        Assert.True(ping.Payload.GetProperty("tokensSavedTotal").GetInt64() >= 0);
    }

    [Fact]
    public async Task TokenSaverDisabled_ForwardsBodyByteIdentical()
    {
        var body = MessagesBody(DirtyToolOutput);

        var result = await SendAsync(
            "/llm/anthropic/v1/messages", body, tokenSaverEnabled: false,
            TestContext.Current.CancellationToken);

        Assert.Equal(Encoding.UTF8.GetBytes(body), result.UpstreamBody);
        Assert.Empty(result.Savings.Records);
    }

    [Fact]
    public async Task InvalidJsonBody_FailsOpenByteIdentical()
    {
        const string body = "{\"messages\":[{\"role\":"; // truncated JSON

        var result = await SendAsync(
            "/llm/anthropic/v1/messages", body, tokenSaverEnabled: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal(Encoding.UTF8.GetBytes(body), result.UpstreamBody);
    }

    [Fact]
    public async Task NonMessagesPath_IsNeverTouched()
    {
        var body = MessagesBody(DirtyToolOutput);

        var result = await SendAsync(
            "/llm/anthropic/v1/messages/count_tokens", body, tokenSaverEnabled: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(Encoding.UTF8.GetBytes(body), result.UpstreamBody);
        Assert.Empty(result.Savings.Records);
    }

    [Fact]
    public async Task CleanBody_IsMeasuredButNotRewritten()
    {
        // Already-minimal output: the request must be counted (Requests vs RewrittenRequests is
        // the canary for an upstream tool rename) but forwarded byte-identical, with no savings
        // claimed on the ping.
        var body = MessagesBody("clean output");

        var result = await SendAsync(
            "/llm/anthropic/v1/messages", body, tokenSaverEnabled: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(Encoding.UTF8.GetBytes(body), result.UpstreamBody);
        var record = Assert.Single(result.Savings.Records);
        Assert.Equal(record.BytesBefore, record.BytesAfter);

        var ping = Assert.Single(result.Events);
        Assert.True(
            !ping.Payload.TryGetProperty("bytesSaved", out var bytesSaved)
            || bytesSaved.ValueKind == System.Text.Json.JsonValueKind.Null);
    }

    private sealed record ProxyResult(
        HttpStatusCode StatusCode,
        byte[] UpstreamBody,
        long? UpstreamContentLength,
        StubTokenSavingsStore Savings,
        List<AppEvent> Events);

    private static async Task<ProxyResult> SendAsync(
        string path, string body, bool tokenSaverEnabled, CancellationToken cancellationToken)
    {
        // Port 0 = kernel-assigned: parallel Kestrel-hosted test classes can race a find-then-rebind
        // port picker, so bind ephemeral and read the real address after start.
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var authService = new Mock<IAuthService>(MockBehavior.Strict);
        authService.Setup(service => service.ValidateToken(SessionToken)).Returns(true);
        authService.Setup(service => service.ValidateTabToken(TabToken)).Returns(true);

        var eventBus = new AppEventBus();
        var events = new List<AppEvent>();
        using var subscription = eventBus.Subscribe(events.Add);
        var savingsStore = new StubTokenSavingsStore();
        var upstreamHandler = new RecordingUpstreamHandler();
        using var clientFactory = new StubHttpClientFactory(upstreamHandler);

        builder.Services.AddSingleton<ILlmProxyAuthGate>(new LlmProxyAuthGateAdapter(authService.Object));
        builder.Services.AddSingleton<ILlmProxyEventSink>(new LlmProxyEventSinkAdapter(eventBus, savingsStore));
        builder.Services.AddSingleton<IHttpClientFactory>(clientFactory);
        builder.Services.AddSingleton<ILlmProxySettingsService>(new StubProxySettingsService(
            new LlmProxySettings(
                CodexLlmProxyEnabled: false,
                CodexLlmProxyMode: CodexLlmProxySettings.ModeSubscription,
                ClaudeLlmProxyEnabled: true,
                ClaudeTokenSaverEnabled: tokenSaverEnabled,
                ClaudeTokenSaverFlags: MinifyFlags.Default)));

        await using var app = builder.Build();
        LlmAnthropicProxyRoutes.Map(app);
        await app.StartAsync(cancellationToken);

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post, new Uri(new Uri(app.Urls.First()), path));
            request.Headers.TryAddWithoutValidation(LlmProxyClaudeConfig.SessionHeaderName, SessionToken);
            request.Headers.TryAddWithoutValidation(LlmProxyClaudeConfig.TabHeaderName, TabToken);
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

            using var response = await SharedClient.SendAsync(request, cancellationToken);
            await response.Content.ReadAsStringAsync(cancellationToken);

            return new ProxyResult(
                response.StatusCode,
                upstreamHandler.RequestBody,
                upstreamHandler.RequestContentLength,
                savingsStore,
                [.. events]);
        }
        finally
        {
            await app.StopAsync(CancellationToken.None);
        }
    }

    private sealed class StubProxySettingsService(LlmProxySettings settings) : ILlmProxySettingsService
    {
        public LlmProxySettings GetSettings() => settings;
    }

    internal sealed class StubTokenSavingsStore : VibeRails.DB.ITokenSavingsStore
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

    private sealed class RecordingUpstreamHandler : HttpMessageHandler
    {
        public byte[] RequestBody { get; private set; } = [];
        public long? RequestContentLength { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestContentLength = request.Content?.Headers.ContentLength;
            RequestBody = request.Content is null
                ? []
                : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"type\":\"message\"}", Encoding.UTF8, "application/json")
            };
        }
    }
}
