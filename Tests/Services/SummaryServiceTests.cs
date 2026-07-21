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

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.GetSummaryAsync("local transcript", TestContext.Current.CancellationToken));

        Assert.Contains("Remote Access", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task GetSummaryAsync_ApiKeyMissing_DoesNotSendTranscript()
    {
        ParserConfigs.SetRemoteAccess(true);
        ParserConfigs.SetApiKey("   ");
        var handler = new CapturingHandler();
        var service = CreateService(handler);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.GetSummaryAsync("local transcript", TestContext.Current.CancellationToken));

        Assert.Contains("API key", exception.Message, StringComparison.OrdinalIgnoreCase);
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
        Assert.Contains("Keep this normal context.", sentTranscript, StringComparison.Ordinal);
        Assert.Contains("Keep this trailing context too.", sentTranscript, StringComparison.Ordinal);
        Assert.Contains("[REDACTED: possible credential]", sentTranscript, StringComparison.Ordinal);
        Assert.DoesNotContain("configured-api-key", sentTranscript, StringComparison.Ordinal);
        Assert.DoesNotContain("super_secret_value_here", sentTranscript, StringComparison.Ordinal);
        Assert.DoesNotContain("bearer-value-that-must-not-leak", sentTranscript, StringComparison.Ordinal);
        Assert.DoesNotContain("correct-horse-battery-staple", sentTranscript, StringComparison.Ordinal);
        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiJ9", sentTranscript, StringComparison.Ordinal);
        Assert.DoesNotContain("cHJpdmF0ZSBrZXkgYnl0ZXM", sentTranscript, StringComparison.Ordinal);
    }

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

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public HttpMethod? Method { get; private set; }
        public Uri? RequestUri { get; private set; }
        public string? ApiKey { get; private set; }
        public string? RequestBody { get; private set; }

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
