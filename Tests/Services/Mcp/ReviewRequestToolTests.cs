using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Moq;
using TokenSaver;
using VibeRails.Services.AgentTools;
using VibeRails.Services.Mcp.Tools;
using VibeRails.Services.ReviewRequests;
using Xunit;

namespace Tests.Services.Mcp;

/// <summary>
/// <c>request_review</c> (VB-50 slice E) creates a request through the inherited root API with the
/// calling session attached and reports the disclosure. It never sends, decides or signs, and it never
/// relays the loopback credentials or a response body it did not parse.
/// </summary>
public sealed class ReviewRequestToolTests
{
    private const string SessionId = "b47e0d56-d697-4675-81d9-aa3274dd0c9d";
    private const string RequestId = "6f1a2b3c-4d5e-4f60-8a9b-0c1d2e3f4a5b";
    private static readonly string Tree = new('b', 40);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(LocalToolApiContext.CurrentSessionIdVariable, null)]
    [InlineData(LocalToolApiContext.CurrentSessionIdVariable, "not-a-session")]
    [InlineData(LocalToolApiContext.CurrentSessionIdVariable, "00000000-0000-0000-0000-000000000000")]
    [InlineData(LocalToolApiContext.SessionTokenVariable, null)]
    [InlineData(LocalToolApiContext.TabTokenVariable, null)]
    [InlineData(LocalToolApiContext.ApiBaseUrlVariable, "https://example.com")]
    [InlineData(LocalToolApiContext.ApiBaseUrlVariable, "file:///tmp/share")]
    public async Task MissingIdentityOrCredentialsAndNonLocalOriginsNeverCall(string variable, string? value)
    {
        var environment = Environment();
        environment[variable] = value;
        var factory = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        var tool = new ReviewRequestTool(factory.Object, key => environment.GetValueOrDefault(key));
        Assert.StartsWith("FAIL:", await tool.RequestReview("lead@example.com", Ct));
        factory.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not an address")]
    [InlineData("lead@example.com, <b>x</b>@example.com")]
    [InlineData("a1@example.com,a2@example.com,a3@example.com,a4@example.com,a5@example.com,a6@example.com,a7@example.com,a8@example.com,a9@example.com,a10@example.com,a11@example.com")]
    public async Task InvalidReviewersNeverCall(string reviewers)
    {
        var factory = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        var tool = new ReviewRequestTool(factory.Object, key => Environment().GetValueOrDefault(key));
        Assert.StartsWith("FAIL:", await tool.RequestReview(reviewers, Ct));
        Assert.StartsWith("FAIL:", await tool.RequestReview("lead@example.com", Ct, note: new string('n', ReviewRequestService.MaxNoteChars + 1)));
        Assert.StartsWith("FAIL:", await tool.RequestReview("lead@example.com", Ct, cardKey: new string('k', ReviewRequestService.MaxCardKeyChars + 1)));
        factory.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CreatesWithTheCallingSessionAttachedThenReportsTheDisclosureWithoutSending()
    {
        var calls = new List<string>();
        using var client = new HttpClient(new Handler(async (request, ct) =>
        {
            calls.Add($"{request.Method} {request.RequestUri!.PathAndQuery}");
            Assert.Equal("127.0.0.1", request.RequestUri.Host);
            Assert.Equal("root-session-secret", request.Headers.GetValues(LlmProxyCodexConfig.SessionHeaderName).Single());
            Assert.Equal("root-tab-secret", request.Headers.GetValues(LlmProxyCodexConfig.TabHeaderName).Single());
            if (request.Method == HttpMethod.Post)
            {
                var body = await request.Content!.ReadFromJsonAsync(ReviewRequestJsonContext.Default.CreateReviewRequest, ct);
                Assert.Equal("staged", body!.Scope);
                Assert.Equal(["lead@example.com", "second@example.com"], body.Reviewers);
                Assert.Equal([SessionId], body.Sessions);
                Assert.Equal("VB-50", body.CardKey);
                Assert.Equal("Please check the migration.", body.Note);
                return Json(HttpStatusCode.Created, Summary(), ReviewRequestJsonContext.Default.ReviewRequestSummary);
            }
            return Json(HttpStatusCode.OK, Disclosure(), ReviewRequestJsonContext.Default.ReviewRequestDisclosure);
        }));

        var result = await Tool(client).RequestReview(" lead@example.com; second@example.com ", Ct, note: "  Please check the migration. ", cardKey: " VB-50 ");

        Assert.Equal(["POST /api/v1/review-requests", $"GET /api/v1/review-requests/{RequestId}/disclosure"], calls);
        Assert.StartsWith($"Review request {RequestId} created (open) for repo. Nothing has been sent.", result);
        Assert.Contains("Tree: " + Tree, result);
        Assert.Contains("Reviewers: lead@example.com, second@example.com", result);
        Assert.Contains("modified db.cs [1 line]", result);
        Assert.Contains("added logo.png [no text: binary]", result);
        Assert.Contains("Codex session (codex; still running, so it uploads after it ends)", result);
        Assert.Contains("Note: Please check the migration.", result);
        Assert.Contains("open card VB-50 in VibeRails", result);
        Assert.Contains("server_update_required", result);
        Assert.Contains("cannot sign, approve or reject", result);
        Assert.DoesNotContain("secret", result);
        Assert.DoesNotContain("vibe-signature:", result);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.MethodNotAllowed)]
    public async Task AnOlderRootIsNeverAskedForADisclosureAndReportsTheUpdate(HttpStatusCode status)
    {
        var calls = 0;
        using var client = new HttpClient(new Handler((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("<html>secret legacy page</html>") });
        }));
        var result = await Tool(client).RequestReview("lead@example.com", Ct);
        Assert.Equal(1, calls);
        Assert.StartsWith("FAIL:", result);
        Assert.Contains("does not support review requests", result);
        Assert.Contains("Update VibeRails", result);
        Assert.DoesNotContain("secret", result);
    }

    [Fact]
    public async Task AReusedRequestIsReportedWithoutReadingAnotherSessionsDisclosure()
    {
        var calls = 0;
        using var client = new HttpClient(new Handler((request, _) =>
        {
            calls++;
            Assert.Equal(HttpMethod.Post, request.Method);
            return Task.FromResult(Json(HttpStatusCode.OK, new ReviewRequestReuse(RequestId, Tree),
                ReviewRequestJsonContext.Default.ReviewRequestReuse));
        }));
        var result = await Tool(client).RequestReview("lead@example.com", Ct);
        Assert.Equal(1, calls);
        Assert.StartsWith($"Review request {RequestId} reused (open, unsent).", result);
        Assert.Contains("Tree: " + Tree, result);
        Assert.Contains("reviewers, note and attached sessions are unchanged", result);
        Assert.Contains("Open Review requests in VibeRails", result);
    }

    [Fact]
    public async Task TheRootsOwnRefusalKeepsItsCodeAndWording()
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(
            Json(HttpStatusCode.Conflict, new ReviewRequestError("Nothing is staged. Stage the changes to review with git add first.", "nothing_staged"),
                ReviewRequestJsonContext.Default.ReviewRequestError))));
        var result = await Tool(client).RequestReview("lead@example.com", Ct);
        Assert.Equal("FAIL (nothing_staged): Nothing is staged. Stage the changes to review with git add first.", result);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Redirect)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task UnparsedErrorsReportOnlyTheStatus(HttpStatusCode status)
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(
            new HttpResponseMessage(status) { Content = new StringContent("secret server error") })));
        var result = await Tool(client).RequestReview("lead@example.com", Ct);
        Assert.StartsWith("FAIL:", result);
        Assert.Contains(((int)status).ToString(), result);
        Assert.DoesNotContain("secret", result);
    }

    [Fact]
    public async Task AFailedDisclosureReadStillReportsTheCreatedRequest()
    {
        using var client = new HttpClient(new Handler((request, _) => Task.FromResult(request.Method == HttpMethod.Post
            ? Json(HttpStatusCode.Created, Summary(), ReviewRequestJsonContext.Default.ReviewRequestSummary)
            : new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("secret") })));
        var result = await Tool(client).RequestReview("lead@example.com", Ct);
        Assert.StartsWith($"Review request {RequestId} created (open). Nothing has been sent.", result);
        Assert.Contains("Files: 2; sessions attached: 1.", result);
        Assert.Contains("disclosure could not be read", result);
        Assert.DoesNotContain("secret", result);
    }

    [Fact]
    public async Task InvalidCreateAnswersNeverInventARequest()
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent("secret: not json", System.Text.Encoding.UTF8, "application/json")
        })));
        var result = await Tool(client).RequestReview("lead@example.com", Ct);
        Assert.StartsWith("FAIL:", result);
        Assert.DoesNotContain("secret", result);
        Assert.DoesNotContain("created (", result);
    }

    [Fact]
    public async Task CallerCancellationPropagates()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var client = new HttpClient(new Handler((_, ct) => Task.FromCanceled<HttpResponseMessage>(ct)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Tool(client).RequestReview("lead@example.com", cancellation.Token));
    }

    [Fact]
    public void ReviewerListsSplitOnTheUsualSeparatorsAndDropRepeats()
    {
        Assert.Equal(["a@example.com", "b@example.com"], ReviewRequestTool.SplitReviewers(" a@example.com, A@example.com;b@example.com\nb@example.com "));
        Assert.Empty(ReviewRequestTool.SplitReviewers(null));
    }

    private static ReviewRequestSummary Summary() =>
        new(RequestId, "staged", Tree, new string('c', 40), "open", ["lead@example.com", "second@example.com"], "VB-50", 2, 1,
            new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc), []);

    private static ReviewRequestDisclosure Disclosure()
    {
        var at = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);
        return new(RequestId, "https://viberails.ai", "email", "repo", Tree, new string('d', 40), new string('c', 40),
            [
                new ReviewDisclosureFile("db.cs", "modified", null, 1, true, null),
                new ReviewDisclosureFile("logo.png", "added", null, null, false, "binary")
            ],
            1, 1, 42,
            [new ReviewDisclosureSession(SessionId, "Codex session", "codex", at, null, true)],
            ReviewRequestSender.SessionDisclosure, ["lead@example.com", "second@example.com"], "VB-50", "Please check the migration.",
            at.AddMonths(1), false, null);
    }

    private static HttpResponseMessage Json<T>(HttpStatusCode status, T body, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) =>
        new(status) { Content = JsonContent.Create(body, info) };

    private static ReviewRequestTool Tool(HttpClient client, Dictionary<string, string?>? environment = null)
    {
        environment ??= Environment();
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(ReviewRequestTool.HttpClientName)).Returns(client);
        return new(factory.Object, key => environment.GetValueOrDefault(key));
    }

    private static Dictionary<string, string?> Environment() => new()
    {
        [LocalToolApiContext.ApiBaseUrlVariable] = "http://127.0.0.1:4321",
        [LocalToolApiContext.SessionTokenVariable] = "root-session-secret",
        [LocalToolApiContext.TabTokenVariable] = "root-tab-secret",
        [LocalToolApiContext.CurrentSessionIdVariable] = SessionId
    };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
