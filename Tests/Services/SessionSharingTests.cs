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

    [Theory]
    [InlineData(503, "schema_update_required", "schema_update_required", "database update")]
    [InlineData(500, null, "server_error", "HTTP 500")]
    [InlineData(404, null, "server_update_required", "deploy the sharing update")]
    [InlineData(405, null, "server_update_required", "HTTP 405")]
    [InlineData(403, "capability_required", "permission_denied", "Sessions access")]
    [InlineData(401, "api_key_expired", "invalid_api_key", "expired")]
    [InlineData(401, "api_key_revoked", "invalid_api_key", "revoked")]
    [InlineData(429, null, "rate_limited", "Try again in a minute")]
    [InlineData(502, null, "server_unavailable", "HTTP 502")]
    [InlineData(503, null, "server_unavailable", "HTTP 503")]
    [InlineData(307, null, "unexpected_redirect", "redirect")]
    public async Task RemoteFailuresExplainTheCauseWithoutEchoingRemoteDetails(int httpStatus, string? code, string status, string message)
    {
        using var client = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage((HttpStatusCode)httpStatus)
        {
            Content = JsonContent.Create(new { code = code ?? ApiKey, error = "Sensitive SQL: " + ApiKey }),
            Headers = { Location = new Uri("https://example.invalid/" + ApiKey) }
        })));
        var result = await Service(client).CreateAsync(_id, "Demo", Ct);
        Assert.False(result.Success);
        Assert.Equal(status, result.Status);
        Assert.Equal(httpStatus, result.HttpStatus);
        Assert.Contains(message, result.Message);
        var json = System.Text.Json.JsonSerializer.Serialize(result, SessionSharingJsonContext.Default.SessionShareResponse);
        Assert.DoesNotContain(ApiKey, json);
        Assert.DoesNotContain("Sensitive SQL", json);
        Assert.DoesNotContain("example.invalid", json);
        Assert.Contains($"\"httpStatus\":{httpStatus}", json);
        _repository.Verify(r => r.QueueSessionShareUploadAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("html")]
    [InlineData("malformed")]
    [InlineData("oversized")]
    public async Task UnreadableErrorBodiesStillReportTheUpstreamHttpStatus(string variant)
    {
        using var client = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = variant switch
            {
                "html" => new StringContent("<html>" + ApiKey + "</html>", Encoding.UTF8, "text/html"),
                "oversized" => new StringContent(new string('x', 17000), Encoding.UTF8, "application/json"),
                _ => new StringContent("{broken:" + ApiKey, Encoding.UTF8, "application/json")
            }
        })));
        var result = await Service(client).CreateAsync(_id, "Demo", Ct);
        Assert.Equal("server_error", result.Status);
        Assert.Equal(500, result.HttpStatus);
        Assert.Contains("HTTP 500", result.Message);
        Assert.DoesNotContain(ApiKey, result.Message);
    }

    [Theory]
    [InlineData(HttpRequestError.NameResolutionError, "DNS")]
    [InlineData(HttpRequestError.SecureConnectionError, "secure connection")]
    [InlineData(HttpRequestError.ConnectionError, "internet connection")]
    public async Task TransportFailuresHaveUsefulMessagesWithoutExceptionDetails(HttpRequestError error, string message)
    {
        using var client = new HttpClient(new Handler(_ => throw new HttpRequestException(error, ApiKey)));
        var result = await Service(client).CreateAsync(_id, "Demo", Ct);
        Assert.Equal("network_error", result.Status);
        Assert.Null(result.HttpStatus);
        Assert.Contains(message, result.Message);
        Assert.DoesNotContain(ApiKey, result.Message);
    }

    [Fact]
    public async Task MalformedSuccessReportsAnInvalidResponseWithoutExceptionDetails()
    {
        using var client = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
        { Content = new StringContent("{broken:" + ApiKey, Encoding.UTF8, "application/json") })));
        var result = await Service(client).CreateAsync(_id, "Demo", Ct);
        Assert.Equal("invalid_response", result.Status);
        Assert.Contains("unreadable response", result.Message);
        Assert.DoesNotContain(ApiKey, result.Message);
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
                request.Content = new StringContent(new string('x', 8200), Encoding.UTF8, "application/json");
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

    [Fact]
    public async Task ListedPeopleAreSentAsAnEmailAudience_AndMustBeConfirmedBeforeAnyUploadIsQueued()
    {
        var confirm = true;
        using var client = new HttpClient(new Handler(async request =>
        {
            var body = await request.Content!.ReadFromJsonAsync(SessionSharingJsonContext.Default.RemoteSessionShareRequest, Ct);
            Assert.Equal("email", body!.Access);
            Assert.Equal(new[] { "Reviewer@example.test", "lead@example.test" }, body.Emails);
            var remote = confirm ? Remote(true) with { Access = "email", Recipients = body.Emails } : Remote(true);
            return new HttpResponseMessage(HttpStatusCode.Created) { Content = JsonContent.Create(remote, SessionSharingJsonContext.Default.RemoteSessionShareResponse) };
        }));
        var service = Service(client);
        var emails = new[] { " Reviewer@example.test ", "lead@example.test", "reviewer@EXAMPLE.test", "" };
        var result = await service.CreateAsync(_id, "Private review", "email", emails, Ct);
        Assert.True(result.Success); Assert.Equal("email", result.Access);
        Assert.Equal(new[] { "Reviewer@example.test", "lead@example.test" }, result.Recipients);
        _repository.Verify(r => r.QueueSessionShareUploadAsync(_id.ToString("D"), SessionSharingService.KeyFingerprint(ApiKey), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
        // A server that ignores the audience would have created a public link: report it and queue nothing more.
        confirm = false;
        var unconfirmed = await service.CreateAsync(_id, "Private review", "email", emails, Ct);
        Assert.False(unconfirmed.Success); Assert.Equal("server_update_required", unconfirmed.Status);
        Assert.Contains("revoke", unconfirmed.Message);
        _repository.Verify(r => r.QueueSessionShareUploadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("public", new[] { "someone@example.test" })]
    [InlineData("email", new string[0])]
    [InlineData("email", new[] { "not an address" })]
    [InlineData("friends", new[] { "someone@example.test" })]
    [InlineData("email", new[] { "a@x.test", "b@x.test", "c@x.test", "d@x.test", "e@x.test", "f@x.test", "g@x.test", "h@x.test", "i@x.test", "j@x.test", "k@x.test" })]
    public async Task InvalidAudiencesNeverCallRemote(string access, string[] emails)
    {
        using var client = new HttpClient(new Handler(_ => throw new InvalidOperationException("Unexpected outbound request")));
        var result = await Service(client).CreateAsync(_id, "Demo", access, emails, Ct);
        Assert.Equal("invalid_request", result.Status);
        Assert.DoesNotContain("example", result.Message);
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
