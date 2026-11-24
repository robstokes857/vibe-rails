using System.Net;
using System.Text;
using System.Text.Json;
using VibeRails.Services.Integrations.VibeCodeRemote;
using VibeRails.Utils;
using Xunit;

namespace Tests.Services;

[Collection("ProcessEnvIsolation")]
public sealed class SummaryServiceTests : IDisposable
{
    private readonly bool _originalRemoteAccess = ParserConfigs.GetRemoteAccess();
    private readonly string _originalApiKey = ParserConfigs.GetApiKey();

    [Fact]
    public async Task GetSummaryAsync_RemoteAccessDisabled_DoesNotSendTranscript()
    {
        ParserConfigs.SetRemoteAccess(false);
        ParserConfigs.SetApiKey("configured-api-key");
        var handler = new CapturingHandler();
        var service = CreateService(handler);

        var exception = await Assert.ThrowsAsync<RemoteSummaryException>(() =>
            service.GetSummaryAsync("local transcript", TestContext.Current.CancellationToken));

        Assert.Contains("Remote Access", exception.Message, StringComparison.Ordinal);
        Assert.Equal(400, exception.StatusCode);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task GetSummaryAsync_ApiKeyMissing_DoesNotSendTranscript()
    {
        ParserConfigs.SetRemoteAccess(true);
        ParserConfigs.SetApiKey("   ");
        var handler = new CapturingHandler();
        var service = CreateService(handler);

        var exception = await Assert.ThrowsAsync<RemoteSummaryException>(() =>
            service.GetSummaryAsync("local transcript", TestContext.Current.CancellationToken));

        Assert.Contains("API key", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(400, exception.StatusCode);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task GetSummaryAsync_Enabled_RedactsCredentialsBeforeSending()
    {
        ParserConfigs.SetRemoteAccess(true);
        ParserConfigs.SetApiKey("configured-api-key");
        var handler = new CapturingHandler();
        var service = CreateService(handler);
        const string transcript = """
            Keep this normal context.
            The configured cloud credential is configured-api-key
            password=super_secret_value_here
            {"password":"hunter2"}
            Authorization: Basic dTpw
            Server=db.example;User Id=sa;Pwd=synthetic-db-password;Database=app
            client_secret: synthetic-client-secret
            Authorization: Bearer bearer-value-that-must-not-leak
            Visit https://admin:correct-horse-battery-staple@example.test/private
            eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.signature_value_here
            -----BEGIN PRIVATE KEY-----
            cHJpdmF0ZSBrZXkgYnl0ZXMgdGhhdCBtdXN0IG5vdCBsZWFr
            -----END PRIVATE KEY-----
            Keep this trailing context too.
            """;

        var result = await service.GetSummaryAsync(
            transcript,
            TestContext.Current.CancellationToken);

        Assert.Equal("concise summary", result);
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("https://viberails.ai/api/v1/summary", handler.RequestUri?.AbsoluteUri);
        Assert.Equal("configured-api-key", handler.ApiKey);

        using var payload = JsonDocument.Parse(Assert.IsType<string>(handler.RequestBody));
        var sentTranscript = payload.RootElement.GetProperty("sessionText").GetString();
        Assert.NotNull(sentTranscript);
        // Best effort: values are masked in place and the rest of the transcript still goes.
        Assert.Contains("Keep this normal context.", sentTranscript, StringComparison.Ordinal);
        Assert.Contains("Keep this trailing context too.", sentTranscript, StringComparison.Ordinal);
        Assert.Contains("password=XXXXX", sentTranscript, StringComparison.Ordinal);
        Assert.DoesNotContain("configured-api-key", sentTranscript, StringComparison.Ordinal);
        Assert.DoesNotContain("super_secret_value_here", sentTranscript, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", sentTranscript, StringComparison.Ordinal);
        Assert.DoesNotContain("dTpw", sentTranscript, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-db-password", sentTranscript, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-client-secret", sentTranscript, StringComparison.Ordinal);
        Assert.DoesNotContain("bearer-value-that-must-not-leak", sentTranscript, StringComparison.Ordinal);
        Assert.DoesNotContain("correct-horse-battery-staple", sentTranscript, StringComparison.Ordinal);
        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiJ9", sentTranscript, StringComparison.Ordinal);
        Assert.DoesNotContain("cHJpdmF0ZSBrZXkgYnl0ZXM", sentTranscript, StringComparison.Ordinal);
    }

    // VIBE-157: an upstream 500 escaped as HttpRequestException, the local route answered with a
    // header-stripped 500, and Send To blamed the local host. Each failure is now fixed local text
    // with a gateway status (never 401, which would send the browser to sign in again).
    [Theory]
    [InlineData(500, 502, "HTTP 500")]
    [InlineData(503, 502, "HTTP 503")]
    [InlineData(401, 502, "API key")]
    [InlineData(403, 502, "Integrations permission")]
    [InlineData(413, 502, "too large")]
    [InlineData(429, 502, "limiting")]
    public async Task GetSummaryAsync_UpstreamFailure_ThrowsSafeGatewayError(int upstream, int local, string expected)
    {
        ParserConfigs.SetRemoteAccess(true);
        ParserConfigs.SetApiKey("configured-api-key");
        var service = CreateService(new StatusHandler(() => new HttpResponseMessage((HttpStatusCode)upstream)
        {
            Content = new StringContent("provider detail with configured-api-key", Encoding.UTF8, "text/plain")
        }));

        var exception = await Assert.ThrowsAsync<RemoteSummaryException>(() =>
            service.GetSummaryAsync("transcript", TestContext.Current.CancellationToken));

        Assert.Equal(local, exception.StatusCode);
        Assert.Equal(upstream, exception.UpstreamStatus);
        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("provider detail", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("configured-api-key", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetSummaryAsync_Unreachable_ThrowsSafeGatewayError()
    {
        ParserConfigs.SetRemoteAccess(true);
        ParserConfigs.SetApiKey("configured-api-key");
        var service = CreateService(new StatusHandler(() => throw new HttpRequestException("No such host is known.")));

        var exception = await Assert.ThrowsAsync<RemoteSummaryException>(() =>
            service.GetSummaryAsync("transcript", TestContext.Current.CancellationToken));

        Assert.Equal(502, exception.StatusCode);
        Assert.Null(exception.UpstreamStatus);
        Assert.Contains("Could not reach viberails.ai", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetSummaryAsync_Timeout_IsGatewayTimeout_ButCallerCancellationPropagates()
    {
        ParserConfigs.SetRemoteAccess(true);
        ParserConfigs.SetApiKey("configured-api-key");
        var timedOut = CreateService(new StatusHandler(() => throw new TaskCanceledException("timeout")));

        var exception = await Assert.ThrowsAsync<RemoteSummaryException>(() =>
            timedOut.GetSummaryAsync("transcript", TestContext.Current.CancellationToken));
        Assert.Equal(504, exception.StatusCode);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var service = CreateService(new StatusHandler(() => new HttpResponseMessage(HttpStatusCode.OK)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetSummaryAsync("transcript", cancelled.Token));
    }

    // VIBE-363: viberails.ai answers a handoff with an event stream (keep-alive comments, then one
    // data line) because its ingress drops a request that sends nothing for 100 seconds.
    [Fact]
    public async Task GetHandoffAsync_PostsRedactedTextAndReadsTheStreamedResult()
    {
        ParserConfigs.SetRemoteAccess(true);
        ParserConfigs.SetApiKey("configured-api-key");
        var handler = new CapturingHandler
        {
            Respond = () => Stream(": started\n\n: working\n\n: working\n\ndata: {\"type\":\"result\",\"summary\":\"Here's where we are.\"}\n\n")
        };
        var service = CreateService(handler);

        var result = await service.GetHandoffAsync(
            "4f0c3b2e-source-session",
            "Keep the screen.\npassword=super_secret_value_here\nconfigured-api-key\n└─ done",
            SessionResumeServiceMaxChars, TestContext.Current.CancellationToken);

        Assert.Equal("Here's where we are.", result);
        Assert.Equal("https://viberails.ai/api/v1/summary/handoff", handler.RequestUri?.AbsoluteUri);
        Assert.Equal("configured-api-key", handler.ApiKey);
        using var payload = JsonDocument.Parse(Assert.IsType<string>(handler.RequestBody));
        var sent = payload.RootElement.GetProperty("sessionText").GetString()!;
        Assert.Contains("Keep the screen.", sent, StringComparison.Ordinal);
        Assert.Contains("└─ done", sent, StringComparison.Ordinal);
        Assert.DoesNotContain("super_secret_value_here", sent, StringComparison.Ordinal);
        Assert.DoesNotContain("configured-api-key", sent, StringComparison.Ordinal);
        Assert.Equal(SessionResumeServiceMaxChars, payload.RootElement.GetProperty("maxChars").GetInt32());
        Assert.Equal("4f0c3b2e-source-session", payload.RootElement.GetProperty("sessionId").GetString());
    }

    [Theory]
    [InlineData("summary_timeout", 504, "20 minutes")]
    [InlineData("handoff_too_large", 413, "longer than the model accepts")]
    [InlineData("handoff_rate_limited", 502, "limiting requests")]
    [InlineData("summary_rejected", 502, "declined")]
    [InlineData("summary_unavailable", 502, "could not write the handoff")]
    public async Task GetHandoffAsync_ErrorInTheStream_IsFixedLocalText(string code, int local, string expected)
    {
        ParserConfigs.SetRemoteAccess(true);
        ParserConfigs.SetApiKey("configured-api-key");
        var service = CreateService(new StatusHandler(() => Stream(
            $": started\n\ndata: {{\"type\":\"error\",\"status\":503,\"code\":\"{code}\",\"error\":\"remote text with configured-api-key\"}}\n\n")));

        var exception = await Assert.ThrowsAsync<RemoteSummaryException>(() =>
            service.GetHandoffAsync("source-session", "screen", SessionResumeServiceMaxChars, TestContext.Current.CancellationToken));

        Assert.Equal(local, exception.StatusCode);
        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("remote text", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetHandoffAsync_StreamWithoutAResult_IsAGatewayError()
    {
        ParserConfigs.SetRemoteAccess(true);
        ParserConfigs.SetApiKey("configured-api-key");
        var service = CreateService(new StatusHandler(() => Stream(": started\n\n: working\n\n")));

        var exception = await Assert.ThrowsAsync<RemoteSummaryException>(() =>
            service.GetHandoffAsync("source-session", "screen", SessionResumeServiceMaxChars, TestContext.Current.CancellationToken));

        Assert.Equal(502, exception.StatusCode);
        Assert.Contains("without a result", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(404, """{"error":"not found"}""", 502, "does not support full-session handoffs")]
    [InlineData(413, """{"code":"handoff_too_large","estimatedTokens":912345,"maxTokens":800000}""", 413, "912,345 tokens")]
    [InlineData(413, "not json", 413, "800,000 tokens")]
    [InlineData(429, """{"code":"handoff_busy"}""", 429, "still being written")]
    [InlineData(429, """{"code":"other"}""", 502, "limiting")]
    [InlineData(401, """{"error":"bad key"}""", 502, "API key")]
    public async Task GetHandoffAsync_RefusalBeforeTheStream_IsFixedLocalText(int upstream, string body, int local, string expected)
    {
        ParserConfigs.SetRemoteAccess(true);
        ParserConfigs.SetApiKey("configured-api-key");
        var service = CreateService(new StatusHandler(() => new HttpResponseMessage((HttpStatusCode)upstream)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        }));

        var exception = await Assert.ThrowsAsync<RemoteSummaryException>(() =>
            service.GetHandoffAsync("source-session", "screen", SessionResumeServiceMaxChars, TestContext.Current.CancellationToken));

        Assert.Equal(local, exception.StatusCode);
        Assert.Equal(upstream, exception.UpstreamStatus);
        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetHandoffAsync_RemoteAccessDisabled_SendsNothing_AndTimeoutIsGatewayTimeout()
    {
        ParserConfigs.SetRemoteAccess(false);
        ParserConfigs.SetApiKey("configured-api-key");
        var handler = new CapturingHandler();
        var disabled = await Assert.ThrowsAsync<RemoteSummaryException>(() =>
            CreateService(handler).GetHandoffAsync("source-session", "screen", SessionResumeServiceMaxChars, TestContext.Current.CancellationToken));
        Assert.Equal(400, disabled.StatusCode);
        Assert.Equal(0, handler.RequestCount);

        ParserConfigs.SetRemoteAccess(true);
        var timedOut = await Assert.ThrowsAsync<RemoteSummaryException>(() =>
            CreateService(new StatusHandler(() => throw new TaskCanceledException("timeout")))
                .GetHandoffAsync("source-session", "screen", SessionResumeServiceMaxChars, TestContext.Current.CancellationToken));
        Assert.Equal(504, timedOut.StatusCode);
        Assert.Contains("25 minutes", timedOut.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void HandoffTokenEstimate_IsFourUtf8BytesPerToken()
    {
        Assert.Equal(0, SessionHandoffLimits.EstimateTokens(""));
        Assert.Equal(1, SessionHandoffLimits.EstimateTokens("abcd"));
        Assert.Equal(2, SessionHandoffLimits.EstimateTokens("abcde"));
        Assert.Equal(3, SessionHandoffLimits.EstimateTokens("────"));
    }

    private const int SessionResumeServiceMaxChars = VibeRails.Services.SessionResumeService.MaxResumeSummaryChars;

    private static HttpResponseMessage Stream(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "text/event-stream")
    };

    public void Dispose()
    {
        ParserConfigs.SetRemoteAccess(_originalRemoteAccess);
        ParserConfigs.SetApiKey(_originalApiKey);
    }

    private static SummaryService CreateService(HttpMessageHandler handler) =>
        new(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://viberails.ai")
        });

    private sealed class StatusHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(respond());
        }
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public HttpMethod? Method { get; private set; }
        public Uri? RequestUri { get; private set; }
        public string? ApiKey { get; private set; }
        public string? RequestBody { get; private set; }
        public Func<HttpResponseMessage>? Respond { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            Method = request.Method;
            RequestUri = request.RequestUri;
            ApiKey = request.Headers.TryGetValues("X-Api-Key", out var values)
                ? values.SingleOrDefault()
                : null;
            RequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            if (Respond is not null) return Respond();

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"summary":"concise summary"}""",
                    Encoding.UTF8,
                    "application/json")
            };
        }
    }
}
