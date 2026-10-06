using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using VibeRails.Auth;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Middleware;
using VibeRails.Routes;
using VibeRails.Services.Integrations.VibeCodeRemote;
using VibeRails.Utils;
using Xunit;

namespace Tests.Services;

[Collection("ProcessEnvIsolation")]
public sealed class SessionSharingTests : IDisposable
{
    private const string ApiKey = "sharing-fixture-account-key";
    private readonly string _originalKey = ParserConfigs.GetApiKey();
    private readonly Guid _id = Guid.NewGuid();
    private readonly Mock<IRepository> _repository = new(MockBehavior.Strict);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public SessionSharingTests()
    {
        ParserConfigs.SetApiKey(ApiKey);
        _repository.Setup(r => r.GetSessionByIdAsync(_id.ToString("D"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SessionResponse(_id.ToString("D"), "shell", null, "fixture", DateTime.UtcNow, null, null));
        _repository.Setup(r => r.QueueSessionShareUploadAsync(_id.ToString("D"),
            SessionSharingService.KeyFingerprint(ApiKey), It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
    }

    [Theory]
    [InlineData(true, false, "pending_upload")]
    [InlineData(false, false, "ready")]
    [InlineData(true, true, "account_changed")]
    public async Task CreatesAtPinnedOrigin_AndPersistsOnlyRequiredUploadUnderOriginalKey(bool uploadRequired, bool changeKey, string status)
    {
        using var client = new HttpClient(new Handler(async request =>
        {
            Assert.Equal("https://viberails.ai/api/v1/session-sharing-links", request.RequestUri!.AbsoluteUri);
            Assert.Equal(ApiKey, Assert.Single(request.Headers.GetValues("X-Api-Key")));
            Assert.False(request.Headers.Contains("Cookie"));
            var body = await request.Content!.ReadFromJsonAsync(SessionSharingJsonContext.Default.RemoteSessionShareRequest, Ct);
            Assert.Equal(_id, body!.SessionId);
            Assert.Equal("<b>Demo</b>", body.DisplayName);
            if (changeKey) ParserConfigs.SetApiKey("different-fixture-key");
            return Success(uploadRequired);
        }));
        var result = await Service(client).CreateAsync(_id, "  <b>Demo</b>  ", Ct);
        Assert.True(result.Success);
        Assert.Equal(status, result.Status);
        Assert.Equal("https://viberails.ai/shared/session?key=" + new string('a', 64), result.Url);
        Assert.DoesNotContain(ApiKey, System.Text.Json.JsonSerializer.Serialize(result, SessionSharingJsonContext.Default.SessionShareResponse));
        _repository.Verify(r => r.QueueSessionShareUploadAsync(_id.ToString("D"),
            SessionSharingService.KeyFingerprint(ApiKey), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            uploadRequired ? Times.Once : Times.Never);
    }

    [Theory]
    [InlineData("foreign_session")]
    [InlineData("external_url")]
    [InlineData("guid_key")]
    [InlineData("expired")]
    [InlineData("missing_upload_required")]
    [InlineData("oversized")]
    [InlineData("redirect")]
    public async Task RejectsUntrustedResponses_WithoutSchedulingUpload(string variant)
    {
        using var client = new HttpClient(new Handler(_ =>
        {
            var remote = Remote(true);
            remote = variant switch
            {
                "foreign_session" => remote with { SessionId = Guid.NewGuid() },
                "external_url" => remote with { SharePath = "https://example.invalid/stolen" },
                "guid_key" => remote with { Key = Guid.NewGuid().ToString("N") },
                "expired" => remote with { ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(-1) },
                "missing_upload_required" => remote with { UploadRequired = null },
                _ => remote
            };
            return Task.FromResult(variant switch
            {
                "oversized" => new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent(new string('x', 17000)) },
                "redirect" => new HttpResponseMessage(HttpStatusCode.TemporaryRedirect) { Headers = { Location = new Uri("https://example.invalid/") } },
                _ => new HttpResponseMessage(HttpStatusCode.Created) { Content = JsonContent.Create(remote, SessionSharingJsonContext.Default.RemoteSessionShareResponse) }
            });
        }));
        Assert.False((await Service(client).CreateAsync(_id, "Demo", Ct)).Success);
        _repository.Verify(r => r.QueueSessionShareUploadAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
        using var handler = Assert.IsType<HttpClientHandler>(SessionSharingService.CreateHandler());
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
    }

    [Fact]
    public async Task LocalRoute_RequiresBothCredentials_BoundsBodies_AndKeepsRemote401ADomainOutcome()
    {
        var calls = 0;
        using var remote = new HttpClient(new Handler(_ =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
                { Content = new StringContent("untrusted remote error containing a credential") });
        }));
        var auth = new AuthService();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<IAuthService>(auth);
        builder.Services.AddSingleton(Service(remote));
        await using var app = builder.Build();
        app.UseMiddleware<CookieAuthMiddleware>();
        SessionSharingRoutes.Map(app);
        await app.StartAsync(Ct);
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            foreach (var (session, tab) in new[] { (false, false), (true, false), (false, true), (true, true) })
            {
                using var request = Request();
                if (session) request.Headers.Add("viberails_session", auth.GetInstanceToken());
                if (tab) request.Headers.Add("viberails_tab", auth.GetTabToken());
                using var response = await client.SendAsync(request, Ct);
                Assert.Equal(session && tab ? HttpStatusCode.OK : HttpStatusCode.Unauthorized, response.StatusCode);
                if (session && tab)
                {
                    Assert.True(response.Headers.CacheControl!.NoStore);
                    var result = await response.Content.ReadFromJsonAsync(SessionSharingJsonContext.Default.SessionShareResponse, Ct);
                    Assert.False(result!.Success);
                    Assert.Equal("invalid_api_key", result.Status);
                    Assert.DoesNotContain("untrusted", result.Message);
                }
            }
            Assert.Equal(1, calls);
            client.DefaultRequestHeaders.Add("viberails_session", auth.GetInstanceToken());
            client.DefaultRequestHeaders.Add("viberails_tab", auth.GetTabToken());
            foreach (var chunked in new[] { false, true })
            {
                using var request = Request();
                request.Content = new StringContent(new string('x', 4100), Encoding.UTF8, "application/json");
                request.Headers.TransferEncodingChunked = chunked;
                using var response = await client.SendAsync(request, Ct);
                Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
            }
            using (var request = Request())
            {
                request.Content = new StringContent("{", Encoding.UTF8, "application/json");
                Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(request, Ct)).StatusCode);
            }
            Assert.Equal(1, calls);
        }
        finally { await app.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task InvalidInputsAndMissingRecordingOrKey_NeverCallRemote()
    {
        using var client = new HttpClient(new Handler(_ => throw new InvalidOperationException("Unexpected outbound request")));
        var service = Service(client);
        foreach (var name in new[] { null, " ", new string('x', 161) })
            Assert.Equal("invalid_request", (await service.CreateAsync(_id, name, Ct)).Status);
        ParserConfigs.SetApiKey("");
        Assert.Equal("no_api_key", (await service.CreateAsync(_id, "Demo", Ct)).Status);
        _repository.Setup(r => r.GetSessionByIdAsync(_id.ToString("D"), It.IsAny<CancellationToken>())).ReturnsAsync((SessionResponse?)null);
        Assert.Equal("not_found", (await service.CreateAsync(_id, "Demo", Ct)).Status);
    }

    private HttpRequestMessage Request() => new(HttpMethod.Post, $"/api/v1/sessions/{_id:D}/sharing-links")
        { Content = JsonContent.Create(new CreateSessionShareRequest("Demo"), SessionSharingJsonContext.Default.CreateSessionShareRequest) };
    private SessionSharingService Service(HttpClient client) => new(client, _repository.Object, _repository.Object);
    private RemoteSessionShareResponse Remote(bool uploadRequired) => new(_id, new string('a', 64),
        "/shared/session?key=" + new string('a', 64), DateTimeOffset.UtcNow.AddMonths(1), uploadRequired);
    private HttpResponseMessage Success(bool uploadRequired) => new(HttpStatusCode.Created)
        { Content = JsonContent.Create(Remote(uploadRequired), SessionSharingJsonContext.Default.RemoteSessionShareResponse) };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => callback(request);
    }
    public void Dispose() => ParserConfigs.SetApiKey(_originalKey);
}
