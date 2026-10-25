using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Moq;
using Tests.VcaRegression;
using VibeRails.Data.Sqlite;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services.Integrations.VibeCodeRemote;
using VibeRails.Services.ReviewRequests;
using VibeRails.Services.VCA;
using Xunit;
using static Tests.Services.ReviewRequests.ReviewRequestTestSupport;

namespace Tests.Services.ReviewRequests;

/// <summary>
/// Collecting decisions (VB-50, slice C): the Board-sync cadence (15 minutes while a sent request is
/// open, doubling to two hours otherwise or while the host is unreachable, persisted next due time),
/// decisions landing through the ledger exactly once with local re-verification, decided requests
/// still collecting later decisions, the key pin, and the reconciliation of an interrupted send.
/// </summary>
public sealed class ReviewRequestPollerTests : IDisposable
{
    private const string ApiKey = "review-poller-fixture-key";
    private static readonly string Fingerprint = SessionSharingService.KeyFingerprint(ApiKey);
    private static readonly DateTime Start = new(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "vb-review-poller-" + Guid.NewGuid().ToString("N"));
    private readonly string _project;
    private readonly ReviewRequestStore _store;
    private readonly Clock _clock = new(Start);
    private readonly RecordingEventBus _events = new();
    private readonly Mock<ISessionArchiveReader> _archives = new(MockBehavior.Strict);
    private readonly List<HttpRequestMessage> _requests = [];
    private readonly Dictionary<string, ReviewRequestHostedStatus> _hosted = [];
    private Func<HttpRequestMessage, HttpResponseMessage>? _override;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ReviewRequestPollerTests()
    {
        _project = Path.Combine(_directory, "project");
        Directory.CreateDirectory(_project);
        _store = new ReviewRequestStore(new SqliteConnectionStringBuilder { DataSource = Path.Combine(_directory, "state.db"), Pooling = false }.ToString());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task PollsEveryFifteenMinutesWhileARequestIsOpen_KeepsPollingADecidedOneAtTheBackoff_AndReturnsWhenOneIsSent()
    {
        var reviews = Reviews(FakeCommitSignatureVerifier.ValidFor("lead@example.com"));
        var poller = Poller(reviews, new ReviewRequestPollState(null, () => _clock.Now));
        var first = await SentAsync(reviews, Reference);
        async Task<int> At(int minutes) { _clock.Now = Start.AddMinutes(minutes); await poller.PollDueAsync(Ct); return _requests.Count(r => r.Method == HttpMethod.Get); }

        Assert.Equal(1, await At(0));     // the first tick polls once
        Assert.Equal(1, await At(1));     // never every minute
        Assert.Equal(1, await At(14));
        Assert.Equal(2, await At(15));    // open: every 15 minutes
        Assert.Equal(3, await At(30));

        _hosted[Reference] = Hosted(first.Id, status: "approved", created: Start, decisions: [Approved(1, "lead@example.com")]);
        Assert.Equal(4, await At(45));    // the approval lands; nothing is open any more
        Assert.Equal("approved", (await reviews.GetAsync(_project, first.Id, Ct)).Request.Status);
        Assert.Equal(4, await At(60));
        Assert.Equal(4, await At(74));    // back off to 30 ...
        Assert.Equal(5, await At(75));    // ... the decided request is still polled, so a later decision is collected (review F2)
        Assert.Equal(5, await At(134));
        Assert.Equal(6, await At(135));   // 60
        Assert.Equal(6, await At(254));
        Assert.Equal(7, await At(255));   // 120, the ceiling
        Assert.Equal(1, (await _store.GetDecisionsAsync(first.Id, Ct)).Count); // the same hosted decision never lands twice

        var second = await SentAsync(reviews, "rvw_" + new string('2', 32));
        Assert.Equal(7, await At(374));   // not due until the ceiling passes (a send through the sender brings it forward; see the sender tests)
        Assert.Equal(9, await At(375));   // both requests polled; one is open again
        Assert.Equal(9, await At(389));
        Assert.Equal(11, await At(390));  // and the cadence is 15 minutes once more
        Assert.Contains(_requests, r => r.RequestUri!.AbsolutePath.Contains("rvw_" + new string('2', 32)));
        Assert.Equal("open", (await reviews.GetAsync(_project, second.Id, Ct)).Request.Status);
    }

    [Fact]
    public async Task AnUnreachableHostBacksOffInsteadOfRetryingEveryTick()
    {
        var reviews = Reviews(FakeCommitSignatureVerifier.ValidFor("lead@example.com"));
        var poller = Poller(reviews, new ReviewRequestPollState(null, () => _clock.Now));
        await SentAsync(reviews, Reference);
        _override = _ => throw new HttpRequestException("down");
        async Task<int> At(int minutes) { _clock.Now = Start.AddMinutes(minutes); await poller.PollDueAsync(Ct); return _requests.Count; }

        Assert.Equal(1, await At(0));
        Assert.Equal(1, await At(14));
        Assert.Equal(2, await At(15));    // the first failure keeps the interval
        Assert.Equal(2, await At(44));
        Assert.Equal(3, await At(45));    // 30
        Assert.Equal(3, await At(104));
        Assert.Equal(4, await At(105));   // 60
        Assert.Equal(4, await At(224));
        Assert.Equal(5, await At(225));   // 120
        _override = null;
        Assert.Equal(6, await At(345));   // reachable again with an open request: 15 minutes from here
        Assert.Equal(7, await At(360));
    }

    [Fact]
    public async Task DecisionsLandThroughTheLedgerOnce_ReverifiedAndPublished()
    {
        var verifier = FakeCommitSignatureVerifier.ValidFor("lead@example.com");
        var reviews = Reviews(verifier);
        var state = new ReviewRequestPollState(null, () => _clock.Now);
        var sent = await SentAsync(reviews, Reference);
        _hosted[Reference] = Hosted(sent.Id, status: "approved", created: Start, decisions:
            [Rejected(4, "lead@example.com", "One more case.") with { LineComments = [new ReviewLineComment("db.cs", 2, "AFTER", "Bound this.\r\nAnd\u0007that.")] },
             Approved(9, "lead@example.com", comment: "Looks right now.")]);

        await Poller(reviews, state).PollDueAsync(Ct);

        var detail = await reviews.GetAsync(_project, sent.Id, Ct);
        Assert.Equal("approved", detail.Request.Status);
        Assert.Equal(["rejected", "approved"], detail.Request.Decisions.Select(d => d.Decision));
        Assert.Equal("One more case.", detail.Request.Decisions[0].Comment);
        // Slice F: the rejection's line comments land with it, cleaned the way a local decision is.
        Assert.Equal(new ReviewLineComment("db.cs", 2, "after", "Bound this.\nAnd that."), Assert.Single(detail.Request.Decisions[0].LineComments!));
        Assert.Null(detail.Request.Decisions[1].LineComments);
        Assert.Equal(HumanSignatureRuleTests.SignatureLine(Tree), detail.Request.Decisions[1].Trailer);
        Assert.NotNull(detail.Request.Decisions[1].VerifiedUtc);
        var signature = Assert.Single(verifier.Calls);
        Assert.Equal(Tree, signature.TreeId);
        var rows = await _store.GetDecisionsAsync(sent.Id, Ct);
        Assert.Equal([4L, 9L], rows.Select(r => r.RemoteId));
        Assert.Equal([4L, 9L], (await _store.GetRemoteDecisionIdsAsync(sent.Id, Ct)).Order());
        var evt = Assert.Single(_events.Events);
        Assert.Equal(("review_request_updated", "approved"), (evt.Type, evt.Payload.GetProperty("status").GetString()));
        // The commit hook finds the approval the same way it finds a locally pasted one.
        Assert.Single(await _store.FindVerifiedApprovalsAsync(Tree, Ct));

        // The decided request is polled again at the backoff; the same hosted decisions land nothing new.
        _clock.Now = Start.AddMinutes(15);
        await Poller(reviews, state).PollDueAsync(Ct);
        Assert.Equal(2, _requests.Count);
        Assert.Equal(2, (await _store.GetDecisionsAsync(sent.Id, Ct)).Count);
        Assert.Single(verifier.Calls);
        Assert.Single(_events.Events);
    }

    /// <summary>Review F2: the host keeps taking decisions after the first one; so must the ledger.</summary>
    [Fact]
    public async Task ALaterApprovalAfterARejectionIsStillCollected()
    {
        var reviews = Reviews(FakeCommitSignatureVerifier.ValidFor("lead@example.com"));
        var state = new ReviewRequestPollState(null, () => _clock.Now);
        var sent = await SentAsync(reviews, Reference);
        _hosted[Reference] = Hosted(sent.Id, status: "rejected", created: Start, decisions: [Rejected(1, "lead@example.com", "Not yet.")]);

        await Poller(reviews, state).PollDueAsync(Ct);
        Assert.Equal("rejected", (await reviews.GetAsync(_project, sent.Id, Ct)).Request.Status);
        Assert.Empty(await _store.FindVerifiedApprovalsAsync(Tree, Ct));

        _hosted[Reference] = Hosted(sent.Id, status: "approved", created: Start, decisions:
            [Rejected(1, "lead@example.com", "Not yet."), Approved(2, "lead@example.com", comment: "Fixed, thanks.")]);
        _clock.Now = Start.AddMinutes(15);
        await Poller(reviews, state).PollDueAsync(Ct);

        var detail = await reviews.GetAsync(_project, sent.Id, Ct);
        Assert.Equal("approved", detail.Request.Status);
        Assert.Equal([1L, 2L], (await _store.GetDecisionsAsync(sent.Id, Ct)).Select(d => d.RemoteId));
        Assert.Single(await _store.FindVerifiedApprovalsAsync(Tree, Ct));
        Assert.Equal(["rejected", "approved"], _events.Events.Select(e => e.Payload.GetProperty("status").GetString()));
    }

    [Fact]
    public async Task ARejectedHostedDecisionIsNotReverifiedEveryPass_ButAnUnavailableVerifierIsRetried()
    {
        var verifier = new SwitchableVerifier { Answer = new(CommitSignatureStatus.Unavailable, "viberails.ai could not be reached") };
        var reviews = Reviews(verifier);
        var state = new ReviewRequestPollState(null, () => _clock.Now);
        var sent = await SentAsync(reviews, Reference);
        _hosted[Reference] = Hosted(sent.Id, status: "approved", created: Start, decisions: [Approved(1, "lead@example.com")]);

        await Poller(reviews, state).PollDueAsync(Ct);                     // unavailable: nothing stored, retried
        Assert.Empty(await _store.GetDecisionsAsync(sent.Id, Ct));
        Assert.Single(verifier.Calls);
        Assert.Equal(Start.AddMinutes(15), state.NextDueUtc);               // first failure keeps the interval

        verifier.Answer = new(CommitSignatureStatus.Valid, "valid", "intruder@example.com");
        _clock.Now = Start.AddMinutes(15);
        await Poller(reviews, state).PollDueAsync(Ct);                     // a signer who is not a reviewer: refused for good
        Assert.Empty(await _store.GetDecisionsAsync(sent.Id, Ct));
        Assert.Equal(2, verifier.Calls.Count);
        Assert.Equal("open", (await reviews.GetAsync(_project, sent.Id, Ct)).Request.Status);

        _clock.Now = Start.AddMinutes(30);
        await Poller(reviews, state).PollDueAsync(Ct);                     // still polled (open), but the refused decision is not re-verified
        Assert.Equal(3, _requests.Count);
        Assert.Equal(2, verifier.Calls.Count);
        Assert.Empty(_events.Events);
    }

    [Fact]
    public async Task OnlyRequestsSentWithTheCurrentKeyArePolled_AndCancelledOnesAreCancelledOnTheHost()
    {
        var reviews = Reviews(FakeCommitSignatureVerifier.ValidFor("lead@example.com"));
        var state = new ReviewRequestPollState(null, () => _clock.Now);
        var mine = await SentAsync(reviews, Reference);
        var theirs = await SentAsync(reviews, "rvw_" + new string('e', 32), fingerprint: SessionSharingService.KeyFingerprint("someone-else"));
        var cancelled = await SentAsync(reviews, "rvw_" + new string('9', 32));
        await reviews.CancelAsync(_project, cancelled.Id, Ct);

        await Poller(reviews, state).PollDueAsync(Ct);

        Assert.Equal([
            "DELETE /api/v1/review-requests/rvw_" + new string('9', 32),
            "GET /api/v1/review-requests/" + Reference + "/decisions"
        ], _requests.Select(r => r.Method + " " + r.RequestUri!.AbsolutePath).Order());
        Assert.Equal(_clock.Now, (await _store.GetAsync(cancelled.Id, Ct))!.RemoteCancelledUTC);
        Assert.Null((await _store.GetAsync(theirs.Id, Ct))!.RemoteCancelledUTC);
        Assert.Equal("open", (await _store.GetAsync(theirs.Id, Ct))!.Status);
        Assert.Equal("open", (await _store.GetAsync(mine.Id, Ct))!.Status);

        // Once confirmed, the cancellation is not sent again.
        _requests.Clear();
        _clock.Now = Start.AddMinutes(15);
        await Poller(reviews, state).PollDueAsync(Ct);
        Assert.Equal(["GET"], _requests.Select(r => r.Method.Method));
    }

    [Fact]
    public async Task ExpiredRequestsStopBeingPolledAfterTheGrace()
    {
        var reviews = Reviews(FakeCommitSignatureVerifier.ValidFor("lead@example.com"));
        var state = new ReviewRequestPollState(null, () => _clock.Now);
        await SentAsync(reviews, Reference, expires: Start - ReviewRequestPoller.ExpiryGrace - TimeSpan.FromDays(1));
        await SentAsync(reviews, "rvw_" + new string('3', 32), expires: Start - ReviewRequestPoller.ExpiryGrace + TimeSpan.FromDays(1));

        await Poller(reviews, state).PollDueAsync(Ct);

        var polled = Assert.Single(_requests);
        Assert.Contains("rvw_" + new string('3', 32), polled.RequestUri!.AbsolutePath);
    }

    /// <summary>Review F3: a send interrupted after the hosted confirmation still gets its sessions queued, under the key that sent it.</summary>
    [Fact]
    public async Task AnInterruptedSendHasItsSessionsQueuedByThePoller_Once()
    {
        var reviews = Reviews(FakeCommitSignatureVerifier.ValidFor("lead@example.com"));
        var state = new ReviewRequestPollState(null, () => _clock.Now);
        var session = Guid.NewGuid().ToString("D");
        var sessions = new Mock<ISessionStore>(MockBehavior.Strict);
        sessions.Setup(s => s.GetSessionByIdAsync(session, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SessionResponse(session, "codex", null, _project, Start.AddHours(-1), Start.AddMinutes(-30), 0));
        sessions.Setup(s => s.GetSessionDisplayInfoAsync(session)).ReturnsAsync(("codex", "Fix the migration"));
        var created = await reviews.CreateAsync(_project, new CreateReviewRequest(Reviewers: ["lead@example.com"], Sessions: [session]), sessions.Object, Ct);
        Assert.True(await _store.MarkSentAsync(created.Id, Reference, Fingerprint, _clock.Now, _clock.Now.AddMonths(1), Ct)); // sent, sessions never queued
        _hosted[Reference] = Hosted(created.Id, created: _clock.Now);
        _archives.Setup(a => a.EnsureSessionShareUploadAsync(session, Fingerprint, It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        await Poller(reviews, state).PollDueAsync(Ct);

        _archives.Verify(a => a.EnsureSessionShareUploadAsync(session, Fingerprint, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(_clock.Now, (await _store.GetAsync(created.Id, Ct))!.SessionsQueuedUTC);

        _clock.Now = Start.AddMinutes(15);
        await Poller(reviews, state).PollDueAsync(Ct);
        _archives.Verify(a => a.EnsureSessionShareUploadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PersistedNextDueSurvivesANewPoller_AndTheFileHoldsNoKey()
    {
        var path = Path.Combine(_directory, ReviewRequestPollState.FileName);
        var reviews = Reviews(FakeCommitSignatureVerifier.ValidFor("lead@example.com"));
        await SentAsync(reviews, Reference);

        await Poller(reviews, new ReviewRequestPollState(path, () => _clock.Now)).PollDueAsync(Ct);
        Assert.Single(_requests);
        var file = await File.ReadAllTextAsync(path, Ct);
        Assert.DoesNotContain(ApiKey, file);
        Assert.DoesNotContain(Reference, file);
        Assert.Contains(Fingerprint, file);

        _clock.Now = Start.AddMinutes(1);
        await Poller(reviews, new ReviewRequestPollState(path, () => _clock.Now)).PollDueAsync(Ct); // a restart or another root: not due yet
        Assert.Single(_requests);
        _clock.Now = Start.AddMinutes(15);
        await Poller(reviews, new ReviewRequestPollState(path, () => _clock.Now)).PollDueAsync(Ct);
        Assert.Equal(2, _requests.Count);

        // Another account's state does not inherit this one's schedule.
        var other = new ReviewRequestPollState(path, () => _clock.Now);
        Assert.True(other.TryBegin(SessionSharingService.KeyFingerprint("other")));
    }

    [Fact]
    public async Task NoKeyMeansNoRequestAtAll()
    {
        var reviews = Reviews(FakeCommitSignatureVerifier.ValidFor("lead@example.com"));
        await SentAsync(reviews, Reference);
        var poller = new ReviewRequestPoller(_store, reviews, Transport(Answer), _archives.Object, _events,
            new ReviewRequestPollState(null, () => _clock.Now), () => null, _clock);

        await poller.PollDueAsync(Ct);

        Assert.Empty(_requests);
    }

    /// <summary>
    /// VIBE-212 (Codex VIBE-209 fourth-pass review, F2): a send whose answer was lost is reconciled before decisions
    /// are polled. The stored bundle is posted again under the pinned key, the host answers an exact retry with the
    /// stored request, the reference lands, and the request is polled as an ordinary sent one in the same pass. A
    /// request cancelled meanwhile has its hosted copy revoked at once, so a later Delete needs no host.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnUnconfirmedSendIsReconciledBeforeDecisionsArePolled_AndRevokedWhenTheRequestWasCancelled(bool cancelledMeanwhile)
    {
        var reviews = Reviews(FakeCommitSignatureVerifier.ValidFor("lead@example.com"));
        var state = new ReviewRequestPollState(null, () => _clock.Now);
        var created = await reviews.CreateAsync(_project, new CreateReviewRequest(Reviewers: ["lead@example.com"]), null, Ct);
        Assert.True(await _store.MarkSendAttemptAsync(created.Id, Fingerprint, Start.AddMinutes(-5), Ct)); // the POST was made; its answer never arrived
        if (cancelledMeanwhile) await reviews.CancelAsync(_project, created.Id, Ct);
        _hosted[Reference] = Hosted(created.Id, created: Start.AddMinutes(-5), decisions: [Approved(1, "lead@example.com")]);

        await Poller(reviews, state).PollDueAsync(Ct);

        var calls = _requests.Select(r => r.Method + " " + r.RequestUri!.AbsolutePath).ToList();
        var stored = await _store.GetAsync(created.Id, Ct);
        Assert.Equal((Reference, Fingerprint), (stored!.RemoteId, stored.SentKeyFingerprint));
        Assert.False(stored.SendUncertain);
        Assert.Empty(await _store.ListSendAttemptsPendingAsync(Ct));
        if (cancelledMeanwhile)
        {
            Assert.Equal(["POST /api/v1/review-requests", "DELETE /api/v1/review-requests/" + Reference], calls);
            Assert.Equal(("cancelled", _clock.Now), (stored.Status, stored.RemoteCancelledUTC));
            Assert.Empty(await _store.GetDecisionsAsync(created.Id, Ct));
            Assert.NotNull(stored.SessionsQueuedUTC);
            Assert.DoesNotContain(await _store.ListSentPendingAsync(Ct), r => r.Id == created.Id); // nothing left for the host
        }
        else
        {
            Assert.Equal(["POST /api/v1/review-requests", "GET /api/v1/review-requests/" + Reference + "/decisions"], calls);
            Assert.Equal("approved", stored.Status); // polled in the same pass
            Assert.Equal(_clock.Now, stored.SessionsQueuedUTC);
            Assert.Single(await _store.FindVerifiedApprovalsAsync(Tree, Ct));
        }
        Assert.Contains(_events.Events, e => e.Payload.GetProperty("id").GetString() == created.Id && e.Payload.GetProperty("sent").GetBoolean());

        // The next pass has nothing left to reconcile.
        _requests.Clear();
        _clock.Now = Start.AddMinutes(15);
        await Poller(reviews, state).PollDueAsync(Ct);
        Assert.DoesNotContain(_requests, r => r.Method == HttpMethod.Post);
    }

    /// <summary>
    /// VIBE-212: on the retry, only a refusal decided by the content or the route proves the first POST never landed.
    /// A key the host now rejects, a missing permission, a rate limit or a server error refuses the call, not the
    /// bundle, so the attempt stays for the next pass.
    /// </summary>
    [Theory]
    [InlineData(400, true)]
    [InlineData(404, true)]
    [InlineData(413, true)]
    [InlineData(401, false)]
    [InlineData(403, false)]
    [InlineData(429, false)]
    [InlineData(500, false)]
    public async Task AReconciliationRefusedByContentClearsTheAttempt_ButARefusedKeyOrRateLimitKeepsIt(int status, bool cleared)
    {
        var reviews = Reviews(FakeCommitSignatureVerifier.ValidFor("lead@example.com"));
        var state = new ReviewRequestPollState(null, () => _clock.Now);
        var created = await reviews.CreateAsync(_project, new CreateReviewRequest(Reviewers: ["lead@example.com"]), null, Ct);
        Assert.True(await _store.MarkSendAttemptAsync(created.Id, Fingerprint, _clock.Now, Ct));
        _override = request => request.Method == HttpMethod.Post
            ? new HttpResponseMessage((HttpStatusCode)status)
            : throw new InvalidOperationException("unexpected " + request.Method);

        await Poller(reviews, state).PollDueAsync(Ct);

        Assert.Equal(["POST"], _requests.Select(r => r.Method.Method));
        var stored = await _store.GetAsync(created.Id, Ct);
        Assert.Null(stored!.RemoteId);
        Assert.Equal(!cleared, stored.SendUncertain);
        Assert.Equal(cleared ? null : Fingerprint, stored.SentKeyFingerprint);
        Assert.Equal(cleared ? 0 : 1, (await _store.ListSendAttemptsPendingAsync(Ct)).Count);
        Assert.Equal(cleared, _events.Events.Any(e => e.Payload.GetProperty("id").GetString() == created.Id && !e.Payload.GetProperty("sent").GetBoolean()));
        Assert.Equal(Start.AddMinutes(15), state.NextDueUtc); // the host answered, so this is not a backoff
    }

    /// <summary>VIBE-212: an attempt pinned to another account's key is never posted under this one, and an unreachable host leaves every attempt for the next pass.</summary>
    [Fact]
    public async Task OnlyAttemptsPinnedToTheCurrentKeyAreReconciled_AndAnUnreachableHostLeavesThemForTheNextPass()
    {
        var reviews = Reviews(FakeCommitSignatureVerifier.ValidFor("lead@example.com"));
        var state = new ReviewRequestPollState(null, () => _clock.Now);
        var theirs = await reviews.CreateAsync(_project, new CreateReviewRequest(Reviewers: ["lead@example.com"]), null, Ct);
        Assert.True(await _store.MarkSendAttemptAsync(theirs.Id, SessionSharingService.KeyFingerprint("someone-else"), _clock.Now, Ct));
        var mine = await reviews.CreateAsync(_project, new CreateReviewRequest(Reviewers: ["lead@example.com"]), null, Ct);
        Assert.True(await _store.MarkSendAttemptAsync(mine.Id, Fingerprint, _clock.Now, Ct));
        _hosted[Reference] = Hosted(mine.Id, created: _clock.Now);
        _override = _ => throw new HttpRequestException("down");

        await Poller(reviews, state).PollDueAsync(Ct);

        Assert.Equal(["POST"], _requests.Select(r => r.Method.Method)); // mine, once; theirs is never posted under this key
        Assert.Equal(2, (await _store.ListSendAttemptsPendingAsync(Ct)).Count);
        Assert.Equal(Start.AddMinutes(15), state.NextDueUtc); // the first failure keeps the interval, then backs off as usual
        Assert.Empty(_events.Events);

        _override = null;
        _clock.Now = Start.AddMinutes(15);
        await Poller(reviews, state).PollDueAsync(Ct);
        Assert.Equal(Reference, (await _store.GetAsync(mine.Id, Ct))!.RemoteId);
        Assert.Null((await _store.GetAsync(theirs.Id, Ct))!.RemoteId);
        Assert.Single(await _store.ListSendAttemptsPendingAsync(Ct));
    }

    private ReviewRequestService Reviews(ICommitSignatureVerifier verifier) => new(_store, Snapshots(_project).Object, verifier, _clock);

    private ReviewRequestPoller Poller(ReviewRequestService reviews, ReviewRequestPollState state) =>
        new(_store, reviews, Transport(Answer), _archives.Object, _events, state, () => ApiKey, _clock);

    private Task<HttpResponseMessage> Answer(HttpRequestMessage request)
    {
        _requests.Add(request);
        if (_override is not null) return Task.FromResult(_override(request));
        if (request.Method == HttpMethod.Delete) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/review-requests", StringComparison.Ordinal))
        {
            // The create route as the host answers it: an exact retry gets the stored request (200, VIBE-212); a first send is created (201).
            var requestId = RequestIdOf(request);
            var stored = _hosted.Values.FirstOrDefault(h => h.RequestId?.ToString("D") == requestId);
            return Task.FromResult(stored is not null
                ? Json(HttpStatusCode.OK, stored)
                : Json(HttpStatusCode.Created, Hosted(requestId, reference: "rvw_" + new string('c', 32), created: _clock.Now)));
        }
        var reference = request.RequestUri!.Segments[^2].TrimEnd('/');
        return Task.FromResult(_hosted.TryGetValue(reference, out var hosted)
            ? Json(HttpStatusCode.OK, hosted)
            : Json(HttpStatusCode.NotFound, new { error = "gone", code = "not_found" }));
    }

    private static string RequestIdOf(HttpRequestMessage request)
    {
        using var body = JsonDocument.Parse(request.Content!.ReadAsStream());
        return body.RootElement.GetProperty("requestId").GetString()!;
    }

    /// <summary>A request created from the fixture index (no sessions) and marked sent, with an open hosted copy unless the test replaces it.</summary>
    private async Task<ReviewRequestSummary> SentAsync(ReviewRequestService reviews, string reference, string? fingerprint = null, DateTime? expires = null)
    {
        var created = await reviews.CreateAsync(_project, new CreateReviewRequest(Reviewers: ["lead@example.com"]), null, Ct);
        Assert.True(await _store.MarkSentAsync(created.Id, reference, fingerprint ?? Fingerprint, _clock.Now, expires ?? _clock.Now.AddMonths(1), Ct));
        Assert.True(await _store.MarkSessionsQueuedAsync(created.Id, _clock.Now, Ct));
        _hosted[reference] = Hosted(created.Id, reference: reference, created: _clock.Now);
        return created;
    }

    private sealed class SwitchableVerifier : ICommitSignatureVerifier
    {
        public CommitSignatureVerification Answer { get; set; } = new(CommitSignatureStatus.Rejected, "not configured");
        public List<CommitSignature> Calls { get; } = [];

        public Task<CommitSignatureVerification> VerifyAsync(CommitSignature signature, CancellationToken cancellationToken)
        {
            Calls.Add(signature);
            return Task.FromResult(Answer);
        }
    }
}
