using System.Net;
using System.Text;
using System.Text.Json;
using VibeRails.Services.ReviewRequests;
using VibeRails.Services.VCA;
using Tests.VcaRegression;
using Xunit;

namespace Tests.Services.ReviewRequests;

/// <summary>Slice D's three hosted calls: what is sent, what is accepted, and the host's refusals in fixed local wording.</summary>
public sealed class ReviewInboxTransportTests
{
    private const string Reference = ReviewRequestTestSupport.Reference;
    private const string Tree = ReviewRequestTestSupport.Tree;
    private static readonly string BaseTree = new('a', 40);
    private static readonly DateTime Created = new(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static object InboxItem(string id = Reference, string status = "open", bool decided = false) => new
    {
        id, repository = "vibe-rails", requestedBy = "dev@example.test", status, fileCount = 2, sessionCount = 1,
        cardKey = "VIBE-208", createdUtc = Created, expiresUtc = Created.AddMonths(1), decided
    };

    private static object BundleJson(object[]? files = null, string? statement = null, string id = Reference) => new
    {
        id, repository = "vibe-rails", requestedBy = "dev@example.test", status = "open", treeId = Tree, baseTree = BaseTree,
        baseCommit = new string('b', 40), cardKey = "VIBE-208", note = "Check the retry.", createdUtc = Created, expiresUtc = Created.AddMonths(1),
        statement = statement ?? $"VibeRails human-verified commit v1\ntree {Tree}\n", canDecide = true,
        files = files ?? [new { path = "db.cs", change = "modified", binary = false, changedLines = 1, before = "a\n", after = "b\n" }],
        sessions = new[] { new { number = 1, name = "Implementation", cli = "codex", startedUtc = Created, endedUtc = Created.AddHours(1), status = "ready" } },
        yourDecisions = Array.Empty<object>()
    };

    private static byte[] Bytes(object value) => JsonSerializer.SerializeToUtf8Bytes(value);

    [Fact]
    public async Task InboxIsReadWithTheKeyAndValidated()
    {
        HttpRequestMessage? seen = null;
        var transport = ReviewRequestTestSupport.Transport(request =>
        {
            seen = request;
            return Task.FromResult(ReviewRequestTestSupport.Json(HttpStatusCode.OK, new { emailVerified = true, requests = new[] { InboxItem() }, more = false }));
        });

        var inbox = await transport.InboxAsync("key-1", Ct);

        Assert.Equal(("GET", "https://viberails.ai/api/v1/review-requests/inbox", "key-1"),
            (seen!.Method.Method, seen.RequestUri!.AbsoluteUri, seen.Headers.GetValues("X-Api-Key").Single()));
        Assert.True(inbox.EmailVerified);
        var item = Assert.Single(inbox.Requests!);
        Assert.Equal((Reference, "vibe-rails", "dev@example.test", "open", "VIBE-208", false), (item.Id, item.Repository, item.RequestedBy, item.Status, item.CardKey, item.Decided));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("bad-reference")]
    [InlineData("bad-status")]
    [InlineData("too-many")]
    public void InboxAnswersOutsideTheContractAreInvalid(string shape)
    {
        object body = shape switch
        {
            "duplicate" => new { emailVerified = true, requests = new[] { InboxItem(), InboxItem() }, more = false },
            "bad-reference" => new { emailVerified = true, requests = new[] { InboxItem(id: "rvw_short") }, more = false },
            "bad-status" => new { emailVerified = true, requests = new[] { InboxItem(status: "maybe") }, more = false },
            _ => new { emailVerified = true, requests = Enumerable.Range(0, ReviewRequestTransport.MaxInboxItems + 1)
                .Select(i => InboxItem(id: "rvw_" + i.ToString("x32"))).ToArray(), more = true }
        };

        var ex = Assert.Throws<ReviewRequestTransportException>(() => ReviewRequestTransport.ParseInbox(Bytes(body)));
        Assert.Equal("invalid_response", ex.Code);
    }

    [Fact]
    public async Task BundleIsReadForTheReferenceAndNotFoundIsReportedPlainly()
    {
        var calls = new List<string>();
        var transport = ReviewRequestTestSupport.Transport(request =>
        {
            calls.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(request.RequestUri.AbsoluteUri.EndsWith("/bundle", StringComparison.Ordinal) && calls.Count == 1
                ? ReviewRequestTestSupport.Json(HttpStatusCode.OK, BundleJson())
                : ReviewRequestTestSupport.Json(HttpStatusCode.NotFound, new { error = "secret prose", code = "not_found" }));
        });

        var bundle = await transport.BundleAsync("key-1", Reference, Ct);
        Assert.Equal($"https://viberails.ai/api/v1/review-requests/{Reference}/bundle", calls.Single());
        Assert.Equal((Tree, "db.cs", "b\n", 1, "ready"), (bundle.TreeId, bundle.Files![0].Path, bundle.Files[0].After, bundle.Sessions![0].Number, bundle.Sessions[0].Status));

        var missing = await Assert.ThrowsAsync<ReviewRequestTransportException>(() => transport.BundleAsync("key-1", Reference, Ct));
        Assert.Equal(("not_found", 404), (missing.Code, missing.HttpStatus));
        Assert.DoesNotContain("secret prose", missing.Message);
    }

    [Theory]
    [InlineData("other-id")]
    [InlineData("statement")]
    [InlineData("omitted-with-text")]
    [InlineData("no-files")]
    [InlineData("oversized")]
    [InlineData("added-with-before")]
    [InlineData("deleted-with-after")]
    [InlineData("modified-with-previous")]
    [InlineData("rename-without-source")]
    [InlineData("duplicate-path")]
    [InlineData("bytes-with-text")]
    [InlineData("bytes-on-deleted")]
    [InlineData("bytes-not-base64")]
    [InlineData("bytes-over-file-bound")]
    [InlineData("bytes-over-bundle-bound")]
    [InlineData("unknown-mode")]
    [InlineData("mode-on-deleted")]
    [InlineData("base-mode-on-added")]
    public void BundleAnswersOutsideTheContractAreInvalid(string shape)
    {
        var body = shape switch
        {
            "other-id" => BundleJson(id: "rvw_" + new string('f', 32)),
            "statement" => BundleJson(statement: $"VibeRails human-verified commit v1\ntree {new string('f', 40)}\n"),
            "omitted-with-text" => BundleJson([new { path = "x.bin", change = "added", binary = true, omitted = "binary", after = "text" }]),
            "no-files" => BundleJson([]),
            // The F1 variants: a side the renderer shows that the tree does not prove.
            "added-with-before" => BundleJson([new { path = "new.conf", change = "added", binary = false, before = "x\n", after = "x\n" }]),
            "deleted-with-after" => BundleJson([new { path = "auth.conf", change = "deleted", binary = false, before = "x\n", after = "x\n" }]),
            "modified-with-previous" => BundleJson([new { path = "auth.conf", change = "modified", binary = false, previousPath = "template.conf", before = "x\n", after = "x\n" }]),
            "rename-without-source" => BundleJson([new { path = "auth.conf", change = "renamed", binary = false, before = "x\n", after = "y\n" }]),
            "duplicate-path" => BundleJson([new { path = "db.cs", change = "modified", binary = false, before = "a\n", after = "b\n" }, new { path = "db.cs", change = "added", binary = false, after = "b\n" }]),
            // VIBE-211: carried bytes only stand in for text that is not shown, decode within the desktop's bounds, and modes are git's.
            "bytes-with-text" => BundleJson([new { path = "db.cs", change = "modified", binary = false, before = "a\n", after = "b\n", afterBytes = Base64(3) }]),
            "bytes-on-deleted" => BundleJson([new { path = "x.bin", change = "deleted", binary = true, omitted = "binary", afterBytes = Base64(3) }]),
            "bytes-not-base64" => BundleJson([new { path = "x.bin", change = "added", binary = true, omitted = "binary", afterBytes = "not base64!" }]),
            "bytes-over-file-bound" => BundleJson([new { path = "x.bin", change = "added", binary = true, omitted = "binary", afterBytes = Base64(ReviewBundleCapture.MaxFileBytes + 1) }]),
            "bytes-over-bundle-bound" => BundleJson(Enumerable.Range(0, 3).Select(i => (object)new { path = $"x{i}.bin", change = "added", binary = true, omitted = "binary", afterBytes = Base64(ReviewBundleCapture.MaxFileBytes) }).ToArray()),
            "unknown-mode" => BundleJson([new { path = "db.cs", change = "modified", binary = false, before = "a\n", after = "b\n", mode = "100600" }]),
            "mode-on-deleted" => BundleJson([new { path = "db.cs", change = "deleted", binary = false, before = "a\n", mode = "100644" }]),
            "base-mode-on-added" => BundleJson([new { path = "db.cs", change = "added", binary = false, after = "b\n", previousMode = "100644" }]),
            _ => BundleJson([new { path = "big.txt", change = "added", binary = false, after = new string('x', ReviewBundleCapture.MaxFileContentBytes + 1) }])
        };

        var ex = Assert.Throws<ReviewRequestTransportException>(() => ReviewRequestTransport.ParseBundle(Bytes(body), Reference));
        Assert.Equal("invalid_response", ex.Code);
    }

    private static string Base64(int length) => Convert.ToBase64String(new byte[length]);

    /// <summary>VIBE-211: a file listed without text may carry its exact bytes and every file its modes; both reach the popup intact.</summary>
    [Fact]
    public void BundleBytesAndModesAreAcceptedWithinTheBounds()
    {
        var logo = Convert.ToBase64String([0x89, 0x50, 0x4E, 0x47, 0x00, 0xFF]);
        var bundle = ReviewRequestTransport.ParseBundle(Bytes(BundleJson(
        [
            new { path = "db.cs", change = "modified", binary = false, changedLines = 1, before = "a\n", after = "b\n", mode = "100755", previousMode = "100644" },
            new { path = "logo.png", change = "added", binary = true, omitted = "binary", afterBytes = logo, mode = "100644" },
            new { path = "big.txt", change = "modified", binary = false, omitted = "oversized", afterBytes = Base64(ReviewBundleCapture.MaxFileBytes), mode = "100644", previousMode = "100644" },
            new { path = "gone.bin", change = "deleted", binary = true, omitted = "binary", previousMode = "100755" }
        ])), Reference);

        Assert.Equal(["db.cs", "logo.png", "big.txt", "gone.bin"], bundle.Files!.Select(file => file.Path));
        Assert.Equal((logo, "100644", null), (bundle.Files[1].AfterBytes, bundle.Files[1].Mode, bundle.Files[1].PreviousMode));
        Assert.Equal(("100755", "100644"), (bundle.Files[0].Mode, bundle.Files[0].PreviousMode));
        Assert.Equal((null, "100755"), (bundle.Files[3].Mode, bundle.Files[3].PreviousMode));
        Assert.Empty(ReviewTreeRebuilder.FilesWithoutContent(bundle.Files));
    }

    [Fact]
    public async Task DecisionsArePostedAsJsonAndTheHostsRefusalsKeepTheirCode()
    {
        var answers = new Queue<HttpResponseMessage>(
        [
            ReviewRequestTestSupport.Json(HttpStatusCode.Created, new { id = 7, reviewer = "me@example.test", decision = "rejected", comment = "no", decidedUtc = Created }),
            ReviewRequestTestSupport.Json(HttpStatusCode.Conflict, new { error = "remote prose", code = "signer_mismatch" }),
            ReviewRequestTestSupport.Json(HttpStatusCode.Conflict, new { error = "remote prose", code = "not_a_reviewer" }),
            ReviewRequestTestSupport.Json(HttpStatusCode.ServiceUnavailable, new { error = "remote prose", code = "verification_unavailable" }),
            ReviewRequestTestSupport.Json(HttpStatusCode.NotFound, new { error = "remote prose", code = "not_found" })
        ]);
        var bodies = new List<string>();
        var transport = ReviewRequestTestSupport.Transport(async request =>
        {
            bodies.Add(await request.Content!.ReadAsStringAsync());
            Assert.Equal(("POST", $"https://viberails.ai/api/v1/review-requests/{Reference}/decisions"), (request.Method.Method, request.RequestUri!.AbsoluteUri));
            return answers.Dequeue();
        });

        var recorded = await transport.DecideAsync("key-1", Reference, new ReviewInboxDecisionBody("rejected", null, "no"), Ct);
        Assert.Equal((7L, "rejected", "no"), (recorded.Id, recorded.Decision, recorded.Comment));
        Assert.Equal("""{"decision":"rejected","comment":"no"}""", bodies[0]);

        var trailer = HumanSignatureRuleTests.SignatureLine(Tree);
        var approval = new ReviewInboxDecisionBody("approved", trailer, null);
        foreach (var (code, status) in new[] { ("signer_mismatch", 409), ("not_a_reviewer", 409), ("verification_unavailable", 503), ("not_found", 404) })
        {
            var ex = await Assert.ThrowsAsync<ReviewRequestTransportException>(() => transport.DecideAsync("key-1", Reference, approval, Ct));
            Assert.Equal((code, status), (ex.Code, ex.HttpStatus));
            Assert.DoesNotContain("remote prose", ex.Message);
        }
        Assert.Contains($"\"trailer\":\"{trailer}\"", bodies[1]);
    }

    /// <summary>
    /// VIBE-213, item 2: the inbox serializer escapes non-ASCII and HTML-sensitive characters as six bytes
    /// each, so a legal 4,000-character comment in CJK (or full of quotes) is about 24 KB on the wire. A
    /// 16 KiB local bound refused it as "larger than viberails.ai accepts" though the host takes 256 KiB.
    /// </summary>
    [Theory]
    [InlineData('審')]
    [InlineData('"')]
    public async Task AMaximalRejectionCommentReachesTheHostWhateverItsCharacters(char filler)
    {
        var comment = new string(filler, ReviewInboxService.MaxCommentChars);
        var posted = 0;
        var transport = ReviewRequestTestSupport.Transport(async request =>
        {
            posted = (await request.Content!.ReadAsByteArrayAsync()).Length;
            return ReviewRequestTestSupport.Json(HttpStatusCode.Created,
                new { id = 7, reviewer = "me@example.test", decision = "rejected", comment, decidedUtc = Created });
        });

        var recorded = await transport.DecideAsync("key-1", Reference, new ReviewInboxDecisionBody("rejected", null, comment), Ct);

        Assert.Equal(comment, recorded.Comment);
        Assert.True(posted > 16 * 1024, $"the escaped comment is {posted} bytes, the size the old bound refused");
        Assert.True(posted <= ReviewRequestTransport.MaxDecisionBodyBytes);
    }

    [Fact]
    public void DecisionAnswersAreValidated()
    {
        var approved = new { id = 1, reviewer = "me@example.test", decision = "approved", trailer = HumanSignatureRuleTests.SignatureLine(Tree), decidedUtc = Created, verifiedUtc = Created };
        Assert.Equal(Tree, CommitSignatureFormat.Parse(ReviewRequestTransport.ParseDecision(Bytes(approved)).Trailer).Single().TreeId);

        var unsigned = new { id = 1, reviewer = "me@example.test", decision = "approved", decidedUtc = Created };
        Assert.Equal("invalid_response", Assert.Throws<ReviewRequestTransportException>(() => ReviewRequestTransport.ParseDecision(Bytes(unsigned))).Code);
        Assert.Equal("invalid_response", Assert.Throws<ReviewRequestTransportException>(() => ReviewRequestTransport.ParseDecision(Encoding.UTF8.GetBytes("not json"))).Code);
    }
}
