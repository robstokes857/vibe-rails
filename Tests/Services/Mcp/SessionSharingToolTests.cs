using System.Net;
using System.Net.Http.Json;
using Moq;
using Tests.Services.VCA;
using TokenSaver;
using VibeRails.Services.AgentTools;
using VibeRails.Services.Integrations.VibeCodeRemote;
using VibeRails.Services.Mcp.Tools;
using Xunit;

namespace Tests.Services.Mcp;

public sealed class SessionSharingToolTests
{
    [Theory]
    [InlineData(LocalToolApiContext.CurrentSessionIdVariable, null)]
    [InlineData(LocalToolApiContext.CurrentSessionIdVariable, "not-a-session")]
    [InlineData(LocalToolApiContext.CurrentSessionIdVariable, "00000000-0000-0000-0000-000000000000")]
    [InlineData(LocalToolApiContext.SessionTokenVariable, null)]
    [InlineData(LocalToolApiContext.TabTokenVariable, null)]
    [InlineData(LocalToolApiContext.ApiBaseUrlVariable, "https://example.com")]
    [InlineData(LocalToolApiContext.ApiBaseUrlVariable, "file:///tmp/share")]
    public async Task MissingIdentityOrCredentialsAndNonLocalOriginsNeverSend(string variable, string? value)
    {
        var environment = Environment();
        environment[variable] = value;
        var factory = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        var tool = new SessionSharingTool(factory.Object, key => environment.GetValueOrDefault(key));
        Assert.StartsWith("FAIL:", await tool.CreateSessionShareLink("Fix bug", TestContext.Current.CancellationToken));
        factory.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task InvalidNamesNeverSend()
    {
        var factory = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        var tool = new SessionSharingTool(factory.Object, _ => null);
        foreach (var name in new[] { "", "   ", new string('x', 161) })
            Assert.StartsWith("FAIL:", await tool.CreateSessionShareLink(name, TestContext.Current.CancellationToken));
        factory.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("ready", "Your replay is ready to share.")]
    [InlineData("pending_upload", "Available after the session ends and uploads.")]
    [InlineData("account_changed", "Switch back to the account key used to create it.")]
    public async Task UsesRootCredentialsAndCurrentSessionAndReturnsCommitTrailer(string status, string message)
    {
        var environment = Environment();
        using var client = new HttpClient(new Handler(async (request, ct) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal($"http://127.0.0.1:4321/api/v1/sessions/{environment[LocalToolApiContext.CurrentSessionIdVariable]}/sharing-links", request.RequestUri!.AbsoluteUri);
            Assert.Equal("root-session-secret", request.Headers.GetValues(LlmProxyCodexConfig.SessionHeaderName).Single());
            Assert.Equal("root-tab-secret", request.Headers.GetValues(LlmProxyCodexConfig.TabHeaderName).Single());
            var body = await request.Content!.ReadFromJsonAsync(SessionSharingJsonContext.Default.CreateSessionShareRequest, ct);
            Assert.Equal("Fix bug", body!.DisplayName);
            return Response(new(true, status, message, SessionShareCommitRuleTests.Url, "Fix bug", DateTimeOffset.UtcNow.AddDays(30)));
        }));
        var result = await Tool(client, environment).CreateSessionShareLink("  Fix bug  ", TestContext.Current.CancellationToken);
        Assert.Contains(message, result);
        Assert.Contains("Status: " + status, result);
        Assert.EndsWith("vibe-share:" + SessionShareCommitRuleTests.Url, result);
        Assert.DoesNotContain("secret", result);
    }

    [Fact]
    public async Task ReturnsSafeDomainFailureWithoutInventingALink()
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(Response(
            new(false, "no_api_key", "Sign in to your VibeRails account before sharing.")))));
        var result = await Tool(client).CreateSessionShareLink("Fix bug", TestContext.Current.CancellationToken);
        Assert.Equal("FAIL: Sign in to your VibeRails account before sharing.", result);
        Assert.DoesNotContain("vibe-share:", result);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Redirect)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task HttpErrorsDoNotEchoResponseBodies(HttpStatusCode status)
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(
            new HttpResponseMessage(status) { Content = new StringContent("secret server error") })));
        var result = await Tool(client).CreateSessionShareLink("Fix bug", TestContext.Current.CancellationToken);
        Assert.StartsWith("FAIL:", result);
        Assert.Contains(((int)status).ToString(), result);
        Assert.DoesNotContain("secret", result);
    }

    [Theory]
    [InlineData("invalid-url")]
    [InlineData("invalid-json")]
    [InlineData("oversized")]
    public async Task InvalidResponsesNeverProduceCommitTrailers(string kind)
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(kind == "invalid-url"
            ? Response(new(true, "ready", "Ready", "https://evil.test/secret", "Fix bug", DateTimeOffset.UtcNow.AddDays(30)))
            : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(kind == "oversized" ? new string('x', 16385) : "secret: not json")
            })));
        var result = await Tool(client).CreateSessionShareLink("Fix bug", TestContext.Current.CancellationToken);
        Assert.StartsWith("FAIL:", result);
        Assert.DoesNotContain("vibe-share:", result);
        Assert.DoesNotContain("secret", result);
    }

    [Fact]
    public async Task CallerCancellationPropagates()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var client = new HttpClient(new Handler((_, ct) => Task.FromCanceled<HttpResponseMessage>(ct)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Tool(client).CreateSessionShareLink("Fix bug", cancellation.Token));
    }

    [Fact]
    public async Task ListedPeopleBecomeAnEmailAudience_AfterTheRootConfirmsSupport_AndAreNamedInTheResult()
    {
        var calls = new List<string>();
        using var client = new HttpClient(new Handler(async (request, ct) =>
        {
            calls.Add(request.Method + " " + request.RequestUri!.AbsolutePath);
            Assert.Equal("root-session-secret", request.Headers.GetValues(LlmProxyCodexConfig.SessionHeaderName).Single());
            Assert.Equal("root-tab-secret", request.Headers.GetValues(LlmProxyCodexConfig.TabHeaderName).Single());
            if (request.Method == HttpMethod.Get)
                return Capabilities("public", "email");
            var body = await request.Content!.ReadFromJsonAsync(SessionSharingJsonContext.Default.CreateSessionShareRequest, ct);
            Assert.Equal("email", body!.Access);
            Assert.Equal(new[] { "reviewer@example.test", "lead@example.test" }, body.Emails);
            return Response(new(true, "ready", "Your replay is ready to share.", SessionSharingCommitRuleUrl(), "Fix bug",
                DateTimeOffset.UtcNow.AddDays(30), null, "email", body.Emails));
        }));
        var result = await Tool(client).CreateSessionShareLink("Fix bug", TestContext.Current.CancellationToken, " reviewer@example.test, lead@example.test ");
        // The root is asked whether it can restrict links before it is asked to create one.
        Assert.Equal(["GET /api/v1/session-sharing/capabilities", "POST /api/v1/sessions/b47e0d56-d697-4675-81d9-aa3274dd0c9d/sharing-links"], calls);
        Assert.Contains("Only these people can open this link", result);
        Assert.Contains("reviewer@example.test, lead@example.test", result);
        Assert.DoesNotContain("Anyone with this link", result);
        Assert.EndsWith("vibe-share:" + SessionShareCommitRuleTests.Url, result);
        using var strict = new HttpClient(new Handler((_, _) => throw new InvalidOperationException("Unexpected request")));
        Assert.StartsWith("FAIL:", await Tool(strict).CreateSessionShareLink("Fix bug", TestContext.Current.CancellationToken, "not an address"));
    }

    [Theory]
    [InlineData(404)]
    [InlineData(405)]
    [InlineData(200)]
    public async Task ListedPeopleFailBeforeAnyLinkIsCreated_WhenTheRunningRootCannotRestrictLinks(int status)
    {
        // The reviewed defect: a newer MCP process talking to an older root got a public link and a
        // ready-to-copy trailer. Now the older root is never asked to create anything.
        var posted = false;
        using var client = new HttpClient(new Handler((request, _) =>
        {
            if (request.Method != HttpMethod.Get) { posted = true; throw new InvalidOperationException("No link may be created"); }
            return Task.FromResult(status == 200 ? Capabilities("public")
                : new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("secret legacy page") });
        }));
        var result = await Tool(client).CreateSessionShareLink("Fix bug", TestContext.Current.CancellationToken, "reviewer@example.test");
        Assert.False(posted);
        Assert.StartsWith("FAIL:", result);
        Assert.Contains("no link was created", result);
        Assert.DoesNotContain("vibe-share:", result);
        Assert.DoesNotContain("secret", result);
    }

    [Theory]
    [InlineData("legacy")]
    [InlineData("public")]
    [InlineData("partial")]
    public async Task ListedPeopleAreNeverReportedUnlessTheRootConfirmsExactlyThem(string shape)
    {
        using var client = new HttpClient(new Handler((request, _) => Task.FromResult(request.Method == HttpMethod.Get
            ? Capabilities("public", "email")
            : Response(shape switch
            {
                "public" => new(true, "ready", "Your replay is ready to share.", SessionShareCommitRuleTests.Url, "Fix bug", DateTimeOffset.UtcNow.AddDays(30), null, "public"),
                "partial" => new(true, "ready", "Your replay is ready to share.", SessionShareCommitRuleTests.Url, "Fix bug", DateTimeOffset.UtcNow.AddDays(30), null, "email", ["reviewer@example.test"]),
                _ => new(true, "ready", "Your replay is ready to share.", SessionShareCommitRuleTests.Url, "Fix bug", DateTimeOffset.UtcNow.AddDays(30))
            }))));
        var result = await Tool(client).CreateSessionShareLink("Fix bug", TestContext.Current.CancellationToken, "reviewer@example.test, lead@example.test");
        Assert.StartsWith("FAIL:", result);
        Assert.Contains("revoke", result);
        Assert.DoesNotContain("vibe-share:", result);
        Assert.DoesNotContain(SessionShareCommitRuleTests.Url, result);
        Assert.DoesNotContain("Anyone with this link", result);
    }

    private static HttpResponseMessage Capabilities(params string[] access) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new ShareCapabilitiesResponse(access, 10), SessionSharingJsonContext.Default.ShareCapabilitiesResponse)
    };

    private static string SessionSharingCommitRuleUrl() => SessionShareCommitRuleTests.Url;

    private static SessionSharingTool Tool(HttpClient client, Dictionary<string, string?>? environment = null)
    {
        environment ??= Environment();
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(SessionSharingTool.HttpClientName)).Returns(client);
        return new(factory.Object, key => environment.GetValueOrDefault(key));
    }

    private static HttpResponseMessage Response(SessionShareResponse result) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(result, SessionSharingJsonContext.Default.SessionShareResponse)
    };

    private static Dictionary<string, string?> Environment() => new()
    {
        [LocalToolApiContext.ApiBaseUrlVariable] = "http://127.0.0.1:4321",
        [LocalToolApiContext.SessionTokenVariable] = "root-session-secret",
        [LocalToolApiContext.TabTokenVariable] = "root-tab-secret",
        [LocalToolApiContext.CurrentSessionIdVariable] = "b47e0d56-d697-4675-81d9-aa3274dd0c9d",
        [AgentSessionTool.BaseUrlVariable] = "http://127.0.0.1:9876",
        [AgentSessionTool.SessionTokenVariable] = "child-session-secret",
        [AgentSessionTool.TabTokenVariable] = "child-tab-secret"
    };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
