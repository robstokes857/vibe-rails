using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Tests.VcaRegression;
using VibeRails.Services.ReviewRequests;
using Xunit;
using static Tests.Services.ReviewRequests.ReviewRequestTestSupport;

namespace Tests.Services.ReviewRequests;

/// <summary>
/// The desktop transport to viberails.ai (VB-50, slice C) against an intercepted handler: what is
/// sent, what is accepted, and that every failure is fixed local wording with no remote text in it.
/// </summary>
public sealed class ReviewRequestTransportTests
{
    private const string ApiKey = "review-fixture-account-key";
    private static readonly string RequestId = Guid.NewGuid().ToString("D");
    private static readonly byte[] Bundle = Encoding.UTF8.GetBytes("{\"schema\":1,\"treeId\":\"" + Tree + "\",\"files\":[]}");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(HttpStatusCode.Created)]
    [InlineData(HttpStatusCode.OK)]
    public async Task SendPostsTheStoredBundleWithOnlyTheApiKeyHeader(HttpStatusCode status)
    {
        var transport = Transport(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://viberails.ai/api/v1/review-requests", request.RequestUri!.AbsoluteUri);
            Assert.Equal(ApiKey, Assert.Single(request.Headers.GetValues("X-Api-Key")));
            Assert.False(request.Headers.Contains("Cookie"));
            Assert.False(request.Headers.Contains("Authorization"));
            Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
            using var body = JsonDocument.Parse(await request.Content.ReadAsStringAsync(Ct));
            Assert.Equal(RequestId, body.RootElement.GetProperty("requestId").GetString());
            // The stored bundle bytes travel untouched inside the envelope.
            Assert.Equal(Encoding.UTF8.GetString(Bundle), body.RootElement.GetProperty("bundle").GetRawText());
            return Json(status, Hosted(RequestId));
        });

        var remote = await transport.SendAsync(ApiKey, RequestId, Tree, Bundle, Ct);

        Assert.Equal(Reference, remote.Id);
        Assert.Equal(Tree, remote.TreeId);
        Assert.Equal("open", remote.Status);
        Assert.Empty(remote.Decisions!);
    }

    [Theory]
    [InlineData("other_request")]
    [InlineData("other_tree")]
    [InlineData("bad_reference")]
    [InlineData("bad_status")]
    [InlineData("expiry_before_creation")]
    [InlineData("expiry_too_far")]
    [InlineData("not_json")]
    [InlineData("oversized")]
    public async Task SendRefusesAnswersThatDoNotDescribeThisRequest(string variant)
    {
        var transport = Transport(_ =>
        {
            var hosted = Hosted(RequestId);
            hosted = variant switch
            {
                "other_request" => hosted with { RequestId = Guid.NewGuid() },
                "other_tree" => hosted with { TreeId = new string('0', 40) },
                "bad_reference" => hosted with { Id = "rvw_" + new string('X', 32) },
                "bad_status" => hosted with { Status = "deleted" },
                "expiry_before_creation" => hosted with { ExpiresUtc = hosted.CreatedUtc.AddMinutes(-1) },
                "expiry_too_far" => hosted with { ExpiresUtc = hosted.CreatedUtc.AddDays(40) },
                _ => hosted
            };
            return Task.FromResult(variant switch
            {
                "not_json" => new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("<html>", Encoding.UTF8, "application/json") },
                "oversized" => new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent(new string(' ', ReviewRequestTransport.MaxResponseBytes + 1), Encoding.UTF8, "application/json") },
                _ => Json(HttpStatusCode.Created, hosted)
            });
        });

        var ex = await Assert.ThrowsAsync<ReviewRequestTransportException>(() => transport.SendAsync(ApiKey, RequestId, Tree, Bundle, Ct));
        Assert.Equal("invalid_response", ex.Code);
    }

    [Theory]
    [InlineData(401, "api_key_expired", "invalid_api_key", "expired")]
    [InlineData(401, "api_key_revoked", "invalid_api_key", "revoked")]
    [InlineData(401, null, "invalid_api_key", "Sign in again")]
    [InlineData(403, "capability_required", "permission_denied", "Sessions access")]
    [InlineData(404, null, "server_update_required", "Nothing was sent")]
    [InlineData(405, null, "server_update_required", "HTTP 405")]
    [InlineData(501, null, "server_update_required", "HTTP 501")]
    [InlineData(400, "invalid_request", "invalid_request", "rejected the review bundle")]
    [InlineData(409, "request_conflict", "request_conflict", "different bundle")]
    [InlineData(413, null, "too_large", "transfer limit")]
    [InlineData(429, "write_budget_exceeded", "rate_limited", "Try again in a minute")]
    [InlineData(502, null, "server_unavailable", "HTTP 502")]
    [InlineData(503, null, "server_unavailable", "HTTP 503")]
    [InlineData(307, null, "unexpected_redirect", "redirect")]
    [InlineData(500, null, "server_error", "HTTP 500")]
    public async Task FailuresUseFixedWordingAndNeverEchoRemoteText(int httpStatus, string? code, string expectedCode, string wording)
    {
        var transport = Transport(_ => Task.FromResult(new HttpResponseMessage((HttpStatusCode)httpStatus)
        {
            Content = JsonContent.Create(new { code = code ?? ApiKey, error = "Sensitive SQL: " + ApiKey }),
            Headers = { Location = new Uri("https://example.invalid/" + ApiKey) }
        }));

        var ex = await Assert.ThrowsAsync<ReviewRequestTransportException>(() => transport.SendAsync(ApiKey, RequestId, Tree, Bundle, Ct));

        Assert.Equal(expectedCode, ex.Code);
        Assert.Equal(httpStatus, ex.HttpStatus);
        Assert.Contains(wording, ex.Message);
        Assert.DoesNotContain(ApiKey, ex.Message);
        Assert.DoesNotContain("Sensitive", ex.Message);
        Assert.DoesNotContain("example.invalid", ex.Message);
    }

    [Fact]
    public async Task NetworkFailuresAreNamedWithoutExceptionText()
    {
        var dns = Transport(_ => throw new HttpRequestException("secret-proxy.internal refused", null, null) { });
        var ex = await Assert.ThrowsAsync<ReviewRequestTransportException>(() => dns.SendAsync(ApiKey, RequestId, Tree, Bundle, Ct));
        Assert.Equal("network_error", ex.Code);
        Assert.True(ex.IsUnreachable);
        Assert.DoesNotContain("secret-proxy", ex.Message);

        // The handler honours the transport's deadline token, as a real connection would.
        var slow = Transport(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return null!; });
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var pending = slow.PollAsync(ApiKey, Reference, cancelled.Token);
        await cancelled.CancelAsync();
        // The caller's own cancellation is not a transport failure.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task ASilentHostIsATimeoutInFixedWording()
    {
        var transport = new ReviewRequestTransport(new HttpClient(new Handler(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return null!;
        })) { Timeout = TimeSpan.FromMilliseconds(200) });

        var ex = await Assert.ThrowsAsync<ReviewRequestTransportException>(() => transport.PollAsync(ApiKey, Reference, Ct));

        Assert.Equal("timeout", ex.Code);
        Assert.True(ex.IsUnreachable);
        Assert.Contains("did not respond in time", ex.Message);
    }

    [Fact]
    public async Task PollReadsStatusAndDecisionsOfThisRequest()
    {
        var transport = Transport(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://viberails.ai/api/v1/review-requests/" + Reference + "/decisions", request.RequestUri!.AbsoluteUri);
            Assert.Equal(ApiKey, Assert.Single(request.Headers.GetValues("X-Api-Key")));
            return Task.FromResult(Json(HttpStatusCode.OK, Hosted(RequestId, status: "approved", decisions:
                [Rejected(4, "lead@example.com", "One more case.") with { LineComments = [new ReviewLineComment("retry.cs", 3, "after", "Bound this.")] },
                 Approved(9, "lead@example.com", comment: "Looks right now.")])));
        });

        var remote = await transport.PollAsync(ApiKey, Reference, Ct);

        Assert.Equal("approved", remote.Status);
        Assert.Equal([4L, 9L], remote.Decisions!.Select(d => d.Id));
        Assert.Equal(HumanSignatureRuleTests.SignatureLine(Tree), remote.Decisions[1].Trailer);
        Assert.Equal("One more case.", remote.Decisions[0].Comment);
        // Slice F: line comments travel with the decision, within the desktop's bounds.
        Assert.Equal(new ReviewLineComment("retry.cs", 3, "after", "Bound this."), Assert.Single(remote.Decisions[0].LineComments!));
        Assert.Null(remote.Decisions[1].LineComments);
    }

    [Theory]
    [InlineData("too_many")]
    [InlineData("duplicate_ids")]
    [InlineData("approval_without_trailer")]
    [InlineData("malformed_trailer")]
    [InlineData("unknown_kind")]
    [InlineData("reviewer_not_an_email")]
    [InlineData("other_reference")]
    [InlineData("long_comment")]
    [InlineData("too_many_line_comments")]
    [InlineData("line_comment_bad_side")]
    [InlineData("line_comment_zero_line")]
    [InlineData("line_comment_long_text")]
    [InlineData("line_comment_blank_text")]
    [InlineData("line_comment_empty_path")]
    public async Task PollRefusesMalformedDecisions(string variant)
    {
        var transport = Transport(_ =>
        {
            var ok = Approved(1, "lead@example.com");
            ReviewRequestHostedDecision[] decisions = variant switch
            {
                "too_many" => Enumerable.Range(1, ReviewRequestTransport.MaxDecisions + 1).Select(i => Rejected(i, "lead@example.com", "no")).ToArray(),
                "duplicate_ids" => [ok, ok],
                "approval_without_trailer" => [ok with { Trailer = null }],
                "malformed_trailer" => [ok with { Trailer = "vibe-signature: v1 key=nope" }],
                "unknown_kind" => [ok with { Decision = "maybe" }],
                "reviewer_not_an_email" => [ok with { Reviewer = "lead" }],
                "long_comment" => [ok with { Comment = new string('c', ReviewRequestService.MaxCommentChars + 1) }],
                "too_many_line_comments" => [ok with { LineComments = Enumerable.Range(1, ReviewRequestService.MaxLineComments + 1).Select(i => new ReviewLineComment("a.cs", i, "after", "x")).ToList() }],
                "line_comment_bad_side" => [ok with { LineComments = [new ReviewLineComment("a.cs", 1, "left", "x")] }],
                "line_comment_zero_line" => [ok with { LineComments = [new ReviewLineComment("a.cs", 0, "after", "x")] }],
                "line_comment_long_text" => [ok with { LineComments = [new ReviewLineComment("a.cs", 1, "after", new string('x', ReviewRequestService.MaxLineCommentChars + 1))] }],
                "line_comment_blank_text" => [ok with { LineComments = [new ReviewLineComment("a.cs", 1, "after", " \u200b\u0007 ")] }],
                "line_comment_empty_path" => [ok with { LineComments = [new ReviewLineComment("", 1, "after", "x")] }],
                _ => [ok]
            };
            var hosted = Hosted(RequestId, reference: variant == "other_reference" ? "rvw_" + new string('f', 32) : Reference, decisions: decisions);
            return Task.FromResult(Json(HttpStatusCode.OK, hosted));
        });

        var ex = await Assert.ThrowsAsync<ReviewRequestTransportException>(() => transport.PollAsync(ApiKey, Reference, Ct));
        Assert.Equal("invalid_response", ex.Code);
    }

    [Fact]
    public async Task PollReportsARequestTheHostNoLongerHas()
    {
        var transport = Transport(_ => Task.FromResult(Json(HttpStatusCode.NotFound, new { error = "gone " + ApiKey, code = "not_found" })));
        var ex = await Assert.ThrowsAsync<ReviewRequestTransportException>(() => transport.PollAsync(ApiKey, Reference, Ct));
        Assert.Equal("not_found", ex.Code);
        Assert.False(ex.IsUnreachable);
        Assert.DoesNotContain(ApiKey, ex.Message);
    }

    [Theory]
    [InlineData(200, true)]
    [InlineData(204, true)]
    [InlineData(404, true)]
    [InlineData(500, false)]
    [InlineData(401, false)]
    public async Task CancelAcceptsAnAlreadyGoneRequestAndRefusesServerErrors(int status, bool succeeds)
    {
        string? path = null;
        var transport = Transport(request =>
        {
            Assert.Equal(HttpMethod.Delete, request.Method);
            path = request.RequestUri!.AbsolutePath;
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status));
        });

        var attempt = transport.CancelAsync(ApiKey, Reference, Ct);
        if (succeeds) await attempt;
        else await Assert.ThrowsAsync<ReviewRequestTransportException>(() => attempt);
        Assert.Equal("/api/v1/review-requests/" + Reference, path);
    }

    [Fact]
    public async Task OnlyWellFormedReferencesAreEverPutInAPath()
    {
        var calls = 0;
        var transport = Transport(_ => { calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); });
        foreach (var bad in new[] { "", "rvw_", "rvw_" + new string('A', 32), "../x", Reference + "/", "rvw_" + new string('a', 31) })
        {
            Assert.False(ReviewRequestTransport.IsReference(bad));
            await Assert.ThrowsAsync<ReviewRequestTransportException>(() => transport.PollAsync(ApiKey, bad, Ct));
            await Assert.ThrowsAsync<ReviewRequestTransportException>(() => transport.CancelAsync(ApiKey, bad, Ct));
        }
        Assert.True(ReviewRequestTransport.IsReference(Reference));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void HandlerFollowsNoRedirectsAndKeepsNoCookies()
    {
        using var handler = Assert.IsType<HttpClientHandler>(ReviewRequestTransport.CreateHandler());
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.Equal("https://viberails.ai", ReviewRequestTransport.Destination);
        Assert.Equal("https://viberails.ai/api/v1/review-requests", ReviewRequestTransport.Endpoint.AbsoluteUri);
    }
}
