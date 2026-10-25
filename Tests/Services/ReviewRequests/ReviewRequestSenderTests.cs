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
using Xunit;
using static Tests.Services.ReviewRequests.ReviewRequestTestSupport;

namespace Tests.Services.ReviewRequests;

/// <summary>
/// Sending a review request (VB-50, slice C): the disclosure names everything that leaves, nothing
/// moves without confirmation or a key, a confirmed send records the hosted copy and queues the
/// attached sessions under the key that sent it, an interrupted send is finished by a retry, a local
/// cancel reaches the hosted copy, and a delete never discards a hosted copy that is still readable.
/// </summary>
public sealed class ReviewRequestSenderTests : IDisposable
{
    private const string ApiKey = "review-sender-fixture-key";
    private static readonly string Fingerprint = SessionSharingService.KeyFingerprint(ApiKey);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "vb-review-sender-" + Guid.NewGuid().ToString("N"));
    private readonly string _project;
    private readonly ReviewRequestStore _store;
    private readonly ReviewRequestService _reviews;
    private readonly Mock<ISessionArchiveReader> _archives = new(MockBehavior.Strict);
    private readonly Mock<ISessionStore> _sessions = new(MockBehavior.Strict);
    private readonly RecordingEventBus _events = new();
    private readonly Clock _clock = new(new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc));
    private readonly ReviewRequestPollState _polls;
    private readonly List<HttpRequestMessage> _requests = [];
    private string? _key = ApiKey;
    private Func<HttpRequestMessage, Task<HttpResponseMessage>> _answer;
    private static readonly string EndedSession = Guid.NewGuid().ToString("D");
    private static readonly string LiveSession = Guid.NewGuid().ToString("D");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ReviewRequestSenderTests()
    {
        _project = Path.Combine(_directory, "project");
        Directory.CreateDirectory(_project);
        _store = new ReviewRequestStore(new SqliteConnectionStringBuilder { DataSource = Path.Combine(_directory, "state.db"), Pooling = false }.ToString());
        _reviews = new ReviewRequestService(_store, Snapshots(_project).Object, FakeCommitSignatureVerifier.ValidFor("lead@example.com"), _clock);
        _polls = new ReviewRequestPollState(null, () => _clock.Now);
        var started = _clock.Now.AddHours(-1);
        _sessions.Setup(s => s.GetSessionByIdAsync(EndedSession, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SessionResponse(EndedSession, "codex", null, _project, started, started.AddMinutes(20), 0));
        _sessions.Setup(s => s.GetSessionByIdAsync(LiveSession, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SessionResponse(LiveSession, "claude", null, _project, started, null, null));
        _sessions.Setup(s => s.GetSessionDisplayInfoAsync(EndedSession)).ReturnsAsync(("codex", "Fix the migration"));
        _sessions.Setup(s => s.GetSessionDisplayInfoAsync(LiveSession)).ReturnsAsync(("claude", (string?)null));
        _answer = request => Task.FromResult(request.Method == HttpMethod.Delete
            ? new HttpResponseMessage(HttpStatusCode.OK)
            : Json(HttpStatusCode.Created, Hosted(RequestIdOf(request), created: _clock.Now)));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task DisclosureListsEverythingThatWouldLeaveTheComputer()
    {
        var created = await CreateAsync();

        var disclosure = await Sender().DisclosureAsync(_project, created.Id, _sessions.Object, Ct);

        Assert.Equal("https://viberails.ai", disclosure.Destination);
        Assert.Equal("email", disclosure.Audience);
        Assert.Equal(Tree, disclosure.TreeId);
        Assert.Equal(new string('d', 40), disclosure.BaseTree);
        Assert.Equal(new string('c', 40), disclosure.BaseCommit);
        Assert.Equal(["db.cs", "logo.png"], disclosure.Files.Select(f => f.Path));
        Assert.True(disclosure.Files[0].TextIncluded);
        Assert.Null(disclosure.Files[0].Omitted);
        Assert.False(disclosure.Files[1].TextIncluded);
        Assert.Equal("binary", disclosure.Files[1].Omitted);
        Assert.Equal((1, 1), (disclosure.FilesWithText, disclosure.FilesWithoutText));
        Assert.Equal("class Db { }\nclass Db { int Version = 2; }\n".Length, disclosure.TextBytes);
        Assert.Equal(["Fix the migration", "claude"], disclosure.Sessions.Select(s => s.Name));
        Assert.Equal([false, true], disclosure.Sessions.Select(s => s.Live));
        Assert.Contains("terminal output", disclosure.SessionDisclosure);
        Assert.Contains("after it ends", disclosure.SessionDisclosure);
        Assert.Equal(["lead@example.com"], disclosure.Reviewers);
        Assert.Equal("VB-50", disclosure.CardKey);
        Assert.Equal("Check the retry bound.", disclosure.Note);
        Assert.Equal(_clock.Now.AddMonths(1), disclosure.ExpiresUtc);
        Assert.False(disclosure.Sent);
        Assert.Null(disclosure.Remote);
        Assert.Empty(_requests);
    }

    [Fact]
    public async Task NothingLeavesWithoutConfirmationOrWithoutAKey()
    {
        var created = await CreateAsync();
        var sender = Sender();

        var unconfirmed = await Assert.ThrowsAsync<ReviewRequestException>(() => sender.SendAsync(_project, created.Id, null, Ct));
        Assert.Equal(("confirmation_required", 400), (unconfirmed.Code, unconfirmed.Status));
        var declined = await Assert.ThrowsAsync<ReviewRequestException>(() => sender.SendAsync(_project, created.Id, new SendReviewRequest(false), Ct));
        Assert.Equal("confirmation_required", declined.Code);

        _key = "   ";
        var noKey = await Assert.ThrowsAsync<ReviewRequestException>(() => sender.SendAsync(_project, created.Id, new SendReviewRequest(true), Ct));
        Assert.Equal(("no_api_key", 409), (noKey.Code, noKey.Status));

        Assert.Empty(_requests);
        Assert.Empty(_events.Events);
        var stored = await _store.GetAsync(created.Id, Ct);
        Assert.Null(stored!.RemoteId);
        Assert.Null(stored.SentUTC);
    }

    [Fact]
    public async Task ConfirmedSendTransfersOnceRecordsTheHostedCopyQueuesSessionsUnderThePinnedKeyAndPublishes()
    {
        var created = await CreateAsync();
        var queued = new List<(string Session, string Fingerprint)>();
        _archives.Setup(a => a.EnsureSessionShareUploadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string session, string fingerprint, DateTime _, CancellationToken _) => { queued.Add((session, fingerprint)); return true; });
        var sender = Sender();

        var sent = await sender.SendAsync(_project, created.Id, new SendReviewRequest(true), Ct);

        var request = Assert.Single(_requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(ApiKey, Assert.Single(request.Headers.GetValues("X-Api-Key")));
        Assert.Contains("Attached sessions upload after they end", sent.Message);
        Assert.Equal(Reference, sent.Request.Remote!.Id);
        Assert.Equal(_clock.Now, sent.Request.Remote.SentUtc);
        Assert.Equal(_clock.Now.AddMonths(1), sent.Request.Remote.ExpiresUtc);
        Assert.Null(sent.Request.Remote.CancelledUtc);
        Assert.Equal("open", sent.Request.Status);

        var stored = await _store.GetAsync(created.Id, Ct);
        Assert.Equal((Reference, Fingerprint, _clock.Now, _clock.Now), (stored!.RemoteId, stored.SentKeyFingerprint, stored.SentUTC, stored.SessionsQueuedUTC));
        Assert.Equal([(EndedSession, Fingerprint), (LiveSession, Fingerprint)], queued);

        var evt = Assert.Single(_events.Events);
        Assert.Equal("review_request_updated", evt.Type);
        Assert.Equal(created.Id, evt.Payload.GetProperty("id").GetString());
        Assert.True(evt.Payload.GetProperty("sent").GetBoolean());
        Assert.Equal("VB-50", evt.Payload.GetProperty("cardKey").GetString());

        // A second send is answered from the ledger: no second transfer, no second queue entry.
        var again = await sender.SendAsync(_project, created.Id, new SendReviewRequest(true), Ct);
        Assert.Equal("This review request was already sent.", again.Message);
        Assert.Single(_requests);
        Assert.Equal(2, queued.Count);

        var disclosure = await sender.DisclosureAsync(_project, created.Id, _sessions.Object, Ct);
        Assert.True(disclosure.Sent);
        Assert.Equal(Reference, disclosure.Remote!.Id);
        Assert.Equal(_clock.Now.AddMonths(1), disclosure.ExpiresUtc);
    }

    /// <summary>Review N1: the send's answer promises a check in 15 minutes, so the idle backoff must not apply to it.</summary>
    [Fact]
    public async Task ASuccessfulSendBringsTheNextDecisionCheckForward()
    {
        var created = await CreateAsync();
        _archives.Setup(a => a.EnsureSessionShareUploadAsync(It.IsAny<string>(), Fingerprint, It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        for (var pass = 0; pass < 4; pass++) _polls.End(Fingerprint, anyOpen: false, reachable: true); // idle: 15, 30, 60, 120
        Assert.Equal(_clock.Now.AddHours(2), _polls.NextDueUtc);

        await Sender().SendAsync(_project, created.Id, new SendReviewRequest(true), Ct);

        Assert.Equal(_clock.Now.AddMinutes(15), _polls.NextDueUtc);
    }

    /// <summary>Review F3: the hosted confirmation is durable before the sessions are queued; a retry finishes the queue without a second transfer.</summary>
    [Fact]
    public async Task AnInterruptedSendResumesQueuingOnRetry_UnderThePinnedKey()
    {
        var created = await CreateAsync();
        var calls = 0;
        _archives.Setup(a => a.EnsureSessionShareUploadAsync(It.IsAny<string>(), Fingerprint, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Returns((string _, string _, DateTime _, CancellationToken _) =>
                ++calls == 1 ? throw new InvalidOperationException("database is locked") : Task.FromResult(true));
        var sender = Sender();

        await Assert.ThrowsAsync<InvalidOperationException>(() => sender.SendAsync(_project, created.Id, new SendReviewRequest(true), Ct));
        var stored = await _store.GetAsync(created.Id, Ct);
        Assert.Equal(Reference, stored!.RemoteId);
        Assert.Null(stored.SessionsQueuedUTC);
        Assert.Single(_requests);

        _key = "a-different-account-key"; // the retry still queues under the key that sent the request
        var retried = await sender.SendAsync(_project, created.Id, new SendReviewRequest(true), Ct);

        Assert.Contains("queued to upload now", retried.Message);
        Assert.Single(_requests);
        Assert.Equal(3, calls);
        Assert.Equal(_clock.Now, (await _store.GetAsync(created.Id, Ct))!.SessionsQueuedUTC);
        var third = await sender.SendAsync(_project, created.Id, new SendReviewRequest(true), Ct);
        Assert.Equal("This review request was already sent.", third.Message);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task SessionsNoLongerOnThisComputerAreReportedNotHidden()
    {
        var created = await CreateAsync();
        _archives.Setup(a => a.EnsureSessionShareUploadAsync(EndedSession, Fingerprint, It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _archives.Setup(a => a.EnsureSessionShareUploadAsync(LiveSession, Fingerprint, It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var sent = await Sender().SendAsync(_project, created.Id, new SendReviewRequest(true), Ct);

        Assert.Contains("no longer on this computer", sent.Message);
        Assert.NotNull(sent.Request.Remote);
    }

    /// <summary>A 4xx is the host saying it did not take the bundle: nothing hosted can exist, so the attempt is cleared (VIBE-212) and the request is an unsent one.</summary>
    [Theory]
    [InlineData(404, "server_update_required")]
    [InlineData(401, "invalid_api_key")]
    [InlineData(403, "permission_denied")]
    [InlineData(400, "invalid_request")]
    [InlineData(413, "too_large")]
    [InlineData(429, "rate_limited")]
    public async Task ARefusalTheHostConfirmedRecordsNothingAndQueuesNothing_AndTheRequestIsDeletableWithoutTheHost(int status, string code)
    {
        var created = await CreateAsync();
        _answer = _ => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status));

        var ex = await Assert.ThrowsAsync<ReviewRequestException>(() => Sender().SendAsync(_project, created.Id, new SendReviewRequest(true), Ct));

        Assert.Equal((code, 502), (ex.Code, ex.Status));
        Assert.DoesNotContain(ReviewRequestSender.UnconfirmedSendNote, ex.Message);
        var stored = await _store.GetAsync(created.Id, Ct);
        Assert.Null(stored!.RemoteId);
        Assert.Null(stored.SentUTC);
        Assert.Null(stored.SentKeyFingerprint);
        Assert.Null(stored.SendAttemptedUTC);
        Assert.False(stored.SendUncertain);
        Assert.Empty(await _store.ListSendAttemptsPendingAsync(Ct));
        Assert.Empty(_events.Events);
        _archives.VerifyNoOtherCalls();

        // Nothing hosted can exist, so the request goes without the host (no key at all here).
        _key = null;
        await Sender().DeleteAsync(_project, created.Id, Ct);
        Assert.Single(_requests);
        Assert.Null(await _store.GetAsync(created.Id, Ct));
    }

    /// <summary>
    /// Codex VIBE-209 fourth-pass review, F2 (VIBE-212): the host commits the POST and the answer is lost
    /// (a reset, the deadline, a truncated 201 body, a gateway 502, a redirect). Before this fix the request
    /// looked unsent, Delete removed the row without the host, and the hosted copy stayed readable until
    /// expiry. The attempt and the pinned fingerprint are written before the POST, so the row stays.
    /// </summary>
    [Theory]
    [InlineData("network_error")]
    [InlineData("timeout")]
    [InlineData("invalid_response")]
    [InlineData("server_unavailable")]
    [InlineData("unexpected_redirect")]
    public async Task AnAcceptedPostWhoseAnswerIsLostKeepsThePinnedAttempt_AndDeleteIsRefused(string variant)
    {
        var created = await CreateAsync();
        for (var pass = 0; pass < 4; pass++) _polls.End(Fingerprint, anyOpen: false, reachable: true); // idle backoff at the two-hour ceiling
        _answer = request =>
        {
            if (request.Method == HttpMethod.Delete) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            return variant switch
            {
                "network_error" => throw new HttpRequestException("connection reset after the hosted commit"),
                "timeout" => throw new OperationCanceledException("the transport deadline passed after the hosted commit"),
                "invalid_response" => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
                    { Content = new StringContent("{\"id\":\"rvw_", System.Text.Encoding.UTF8, "application/json") }),
                "server_unavailable" => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)),
                _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found))
            };
        };

        var ex = await Assert.ThrowsAsync<ReviewRequestException>(() => Sender().SendAsync(_project, created.Id, new SendReviewRequest(true), Ct));

        Assert.Equal((variant, 502), (ex.Code, ex.Status));
        Assert.EndsWith(ReviewRequestSender.UnconfirmedSendNote, ex.Message);
        var stored = await _store.GetAsync(created.Id, Ct);
        Assert.Equal((null, _clock.Now, Fingerprint, "open"), (stored!.RemoteId, stored.SendAttemptedUTC, stored.SentKeyFingerprint, stored.Status));
        Assert.True(stored.SendUncertain);
        Assert.Equal([created.Id], (await _store.ListSendAttemptsPendingAsync(Ct)).Select(r => r.Id));
        Assert.DoesNotContain(await _store.ListSentPendingAsync(DateTime.MinValue, Ct), r => r.Id == created.Id);
        Assert.Equal(_clock.Now.AddMinutes(15), _polls.NextDueUtc); // the poller reconciles it within one interval
        var evt = Assert.Single(_events.Events);
        Assert.Equal(("open", false), (evt.Payload.GetProperty("status").GetString(), evt.Payload.GetProperty("sent").GetBoolean()));
        var summary = (await _reviews.GetAsync(_project, created.Id, Ct)).Request;
        Assert.Null(summary.Remote);
        Assert.Equal(_clock.Now, summary.SendAttemptedUtc);
        _archives.VerifyNoOtherCalls();

        // The reviewer's probe step: Delete used to remove the row without the host. It is refused and the row stays, cancelled.
        _requests.Clear();
        var refused = await Assert.ThrowsAsync<ReviewRequestException>(() => Sender().DeleteAsync(_project, created.Id, Ct));
        Assert.Equal(("send_pending", 409), (refused.Code, refused.Status));
        Assert.Contains("answer from viberails.ai was lost", refused.Message);
        Assert.Contains(ReviewRequestSender.CancelledApprovalNote, refused.Message);
        Assert.DoesNotContain("different account key", refused.Message);
        Assert.Empty(_requests);
        var kept = await _store.GetAsync(created.Id, Ct);
        Assert.Equal(("cancelled", Fingerprint, _clock.Now), (kept!.Status, kept.SentKeyFingerprint, kept.SendAttemptedUTC));
        Assert.Null(kept.RemoteId);
        Assert.Contains(await _store.ListSendAttemptsPendingAsync(Ct), r => r.Id == created.Id);
        Assert.Equal("cancelled", _events.Events[^1].Payload.GetProperty("status").GetString());

        var again = await Assert.ThrowsAsync<ReviewRequestException>(() => Sender().DeleteAsync(_project, created.Id, Ct));
        Assert.Equal("send_pending", again.Code);
        Assert.NotNull(await _store.GetAsync(created.Id, Ct));
    }

    /// <summary>VIBE-212: Send pressed again after a lost answer posts the same bytes under the same key; the host answers an exact retry with the stored request and the reference lands.</summary>
    [Fact]
    public async Task ARetryUnderTheSameKeyRecoversTheReferenceFromAnUnconfirmedSend()
    {
        var created = await CreateAsync();
        var posts = 0;
        _answer = request => ++posts == 1
            ? throw new HttpRequestException("connection reset after the hosted commit")
            : Task.FromResult(Json(HttpStatusCode.OK, Hosted(RequestIdOf(request), created: _clock.Now))); // 200: the stored request
        _archives.Setup(a => a.EnsureSessionShareUploadAsync(It.IsAny<string>(), Fingerprint, It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var sender = Sender();
        await Assert.ThrowsAsync<ReviewRequestException>(() => sender.SendAsync(_project, created.Id, new SendReviewRequest(true), Ct));
        Assert.True((await _store.GetAsync(created.Id, Ct))!.SendUncertain);

        var sent = await sender.SendAsync(_project, created.Id, new SendReviewRequest(true), Ct);

        Assert.Equal(2, _requests.Count(r => r.Method == HttpMethod.Post));
        Assert.StartsWith("Sent.", sent.Message);
        Assert.Equal(Reference, sent.Request.Remote!.Id);
        var stored = await _store.GetAsync(created.Id, Ct);
        Assert.Equal((Reference, Fingerprint, _clock.Now), (stored!.RemoteId, stored.SentKeyFingerprint, stored.SendAttemptedUTC));
        Assert.False(stored.SendUncertain);
        Assert.NotNull(stored.SessionsQueuedUTC);
        Assert.Empty(await _store.ListSendAttemptsPendingAsync(Ct));
        Assert.Contains(await _store.ListSentPendingAsync(DateTime.MinValue, Ct), r => r.Id == created.Id);
        _archives.Verify(a => a.EnsureSessionShareUploadAsync(It.IsAny<string>(), Fingerprint, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    /// <summary>VIBE-212 (VIBE-207 D1): the attempt pins the account. Another key may neither post the bundle nor clear the attempt, so a second hosted copy cannot arise.</summary>
    [Fact]
    public async Task ARetryUnderAnotherKeyIsRefusedWhileTheAttemptIsPinned()
    {
        var created = await CreateAsync();
        _answer = _ => throw new HttpRequestException("connection reset after the hosted commit");
        await Assert.ThrowsAsync<ReviewRequestException>(() => Sender().SendAsync(_project, created.Id, new SendReviewRequest(true), Ct));
        _requests.Clear();
        _events.Events.Clear();
        _key = "a-different-account-key";

        var refused = await Assert.ThrowsAsync<ReviewRequestException>(() => Sender().SendAsync(_project, created.Id, new SendReviewRequest(true), Ct));

        Assert.Equal(("send_pending", 409), (refused.Code, refused.Status));
        Assert.Contains("different account key", refused.Message);
        Assert.Empty(_requests);
        var stored = await _store.GetAsync(created.Id, Ct);
        Assert.Equal((Fingerprint, _clock.Now, "open"), (stored!.SentKeyFingerprint, stored.SendAttemptedUTC, stored.Status));
        Assert.Empty(_events.Events);

        // Delete under the other key is refused too, and names the account that can settle it.
        var delete = await Assert.ThrowsAsync<ReviewRequestException>(() => Sender().DeleteAsync(_project, created.Id, Ct));
        Assert.Equal("send_pending", delete.Code);
        Assert.Contains("different account key", delete.Message);
        Assert.Empty(_requests);
        Assert.Equal("cancelled", (await _store.GetAsync(created.Id, Ct))!.Status);
        _archives.VerifyNoOtherCalls();
    }

    /// <summary>VIBE-212: Cancel on an unconfirmed send cancels locally and leaves the attempt to the poller, which revokes the copy once the reference is known.</summary>
    [Fact]
    public async Task CancelOnAnUnconfirmedSendKeepsTheAttemptForThePoller()
    {
        var created = await CreateAsync();
        _answer = _ => throw new HttpRequestException("connection reset after the hosted commit");
        await Assert.ThrowsAsync<ReviewRequestException>(() => Sender().SendAsync(_project, created.Id, new SendReviewRequest(true), Ct));
        _requests.Clear();

        var summary = await Sender().CancelAsync(_project, created.Id, Ct);

        Assert.Equal("cancelled", summary.Status);
        Assert.Null(summary.Remote);
        Assert.Equal(_clock.Now, summary.SendAttemptedUtc);
        Assert.Empty(_requests); // no reference to cancel yet; the poller recovers it first
        Assert.Contains(await _store.ListSendAttemptsPendingAsync(Ct), r => r.Id == created.Id);
        var refused = await Assert.ThrowsAsync<ReviewRequestException>(() => Sender().DeleteAsync(_project, created.Id, Ct));
        Assert.Equal("send_pending", refused.Code);
    }

    /// <summary>
    /// VIBE-213, item 1: Send pressed again after a lost answer is a retry, and the first POST may have landed. A
    /// refused key, a missing permission or a rate limit refuse the call, not the content, so the attempt stays (the
    /// poller's rule); before the fix any 4xx cleared it, the request read unsent, Delete went without the host and
    /// the hosted copy stayed readable. A refusal decided by the content or the route still clears it.
    /// </summary>
    [Theory]
    [InlineData(401, "invalid_api_key", false)]
    [InlineData(403, "permission_denied", false)]
    [InlineData(429, "rate_limited", false)]
    [InlineData(400, "invalid_request", true)]
    [InlineData(409, "request_conflict", true)]
    [InlineData(413, "too_large", true)]
    public async Task ASameKeyRetryClearsTheAttemptOnlyWhenItsRefusalAlsoRefutesTheFirstPost(int status, string code, bool refutes)
    {
        var created = await CreateAsync();
        var firstAttempt = _clock.Now;
        var posts = 0;
        _answer = _ => ++posts == 1
            ? throw new HttpRequestException("connection reset after the hosted commit")
            : Task.FromResult(new HttpResponseMessage((HttpStatusCode)status));
        var sender = Sender();
        await Assert.ThrowsAsync<ReviewRequestException>(() => sender.SendAsync(_project, created.Id, new SendReviewRequest(true), Ct));
        _clock.Now = firstAttempt.AddMinutes(3);

        var ex = await Assert.ThrowsAsync<ReviewRequestException>(() => sender.SendAsync(_project, created.Id, new SendReviewRequest(true), Ct));

        Assert.Equal((code, 502), (ex.Code, ex.Status));
        Assert.Equal(2, posts);
        var stored = await _store.GetAsync(created.Id, Ct);
        Assert.Null(stored!.RemoteId);
        if (refutes)
        {
            Assert.DoesNotContain(ReviewRequestSender.UnconfirmedSendNote, ex.Message);
            Assert.Equal((null, null), (stored.SendAttemptedUTC, stored.SentKeyFingerprint));
            Assert.Empty(await _store.ListSendAttemptsPendingAsync(Ct));
        }
        else
        {
            Assert.EndsWith(ReviewRequestSender.UnconfirmedSendNote, ex.Message);
            Assert.Equal((firstAttempt, Fingerprint), (stored.SendAttemptedUTC, stored.SentKeyFingerprint));
            Assert.True(stored.SendUncertain);
            var refused = await Assert.ThrowsAsync<ReviewRequestException>(() => sender.DeleteAsync(_project, created.Id, Ct));
            Assert.Equal("send_pending", refused.Code);
            Assert.NotNull(await _store.GetAsync(created.Id, Ct));
        }
        _archives.VerifyNoOtherCalls();
    }

    /// <summary>VIBE-213, item 9: a refusal clears only the attempt it answered; another root's pin, or another key's, survives.</summary>
    [Fact]
    public async Task ClearingASendAttemptNeedsTheAttemptItAnswered()
    {
        var created = await CreateAsync();
        Assert.True(await _store.MarkSendAttemptAsync(created.Id, Fingerprint, _clock.Now, Ct));

        Assert.False(await _store.ClearSendAttemptAsync(created.Id, Fingerprint, _clock.Now.AddSeconds(1), _clock.Now, Ct));
        Assert.False(await _store.ClearSendAttemptAsync(created.Id, "another-fingerprint", _clock.Now, _clock.Now, Ct));
        Assert.True((await _store.GetAsync(created.Id, Ct))!.SendUncertain);

        Assert.True(await _store.ClearSendAttemptAsync(created.Id, Fingerprint, _clock.Now, _clock.Now, Ct));
        Assert.False((await _store.GetAsync(created.Id, Ct))!.SendUncertain);
    }

    /// <summary>
    /// VIBE-213, item 3: the person cancels while the bundle is in flight (the POST may take 90 s). Before the fix
    /// the request was marked sent regardless, its terminal recordings were queued for upload, and the event said
    /// "open". The reference is still stored, because it is the only way to revoke the hosted copy, but nothing
    /// uploads, the hosted copy is cancelled at once, and a cancellation the host does not confirm is retried.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task ACancelDuringTheSendRevokesTheHostedCopyAndQueuesNoUpload(HttpStatusCode hostedDelete)
    {
        var created = await CreateAsync();
        _answer = async request =>
        {
            if (request.Method == HttpMethod.Delete) return new HttpResponseMessage(hostedDelete);
            await _reviews.CancelAsync(_project, created.Id, Ct); // cancelled while the host is answering
            return Json(HttpStatusCode.Created, Hosted(RequestIdOf(request), created: _clock.Now));
        };

        var sent = await Sender().SendAsync(_project, created.Id, new SendReviewRequest(true), Ct);

        Assert.Contains("cancelled while it was being sent", sent.Message);
        Assert.Equal("cancelled", sent.Request.Status);
        Assert.Equal(["POST /api/v1/review-requests", "DELETE /api/v1/review-requests/" + Reference],
            _requests.Select(r => r.Method + " " + r.RequestUri!.AbsolutePath));
        var stored = await _store.GetAsync(created.Id, Ct);
        Assert.Equal(("cancelled", Reference, Fingerprint), (stored!.Status, stored.RemoteId, stored.SentKeyFingerprint));
        Assert.NotNull(stored.SessionsQueuedUTC); // marked done so neither a retry nor the poller ever queues them
        _archives.VerifyNoOtherCalls();
        var evt = Assert.Single(_events.Events);
        Assert.Equal(("cancelled", true), (evt.Payload.GetProperty("status").GetString(), evt.Payload.GetProperty("sent").GetBoolean()));
        if (hostedDelete == HttpStatusCode.OK)
        {
            Assert.Equal(_clock.Now, stored.RemoteCancelledUTC);
            Assert.DoesNotContain(await _store.ListSentPendingAsync(DateTime.MinValue, Ct), r => r.Id == created.Id);
            await Sender().DeleteAsync(_project, created.Id, Ct); // nothing hosted is readable, so no host is needed
            Assert.Null(await _store.GetAsync(created.Id, Ct));
        }
        else
        {
            Assert.Contains("retried automatically", sent.Message);
            Assert.Null(stored.RemoteCancelledUTC);
            Assert.Contains(await _store.ListSentPendingAsync(DateTime.MinValue, Ct), r => r.Id == created.Id);
            var refused = await Assert.ThrowsAsync<ReviewRequestException>(() => Sender().DeleteAsync(_project, created.Id, Ct));
            Assert.Equal("remote_pending", refused.Code);
        }
    }

    [Fact]
    public async Task OnlyAnOpenRequestCanBeSent()
    {
        var created = await CreateAsync();
        await _reviews.CancelAsync(_project, created.Id, Ct);

        var ex = await Assert.ThrowsAsync<ReviewRequestException>(() => Sender().SendAsync(_project, created.Id, new SendReviewRequest(true), Ct));

        Assert.Equal(("not_open", 409), (ex.Code, ex.Status));
        Assert.Empty(_requests);
    }

    /// <summary>Review F1: a delete racing the send would leave a hosted copy nothing local can revoke, so the send revokes it.</summary>
    [Fact]
    public async Task ASendRacingADeleteCancelsItsOwnHostedCopy()
    {
        var created = await CreateAsync();
        _answer = async request =>
        {
            if (request.Method == HttpMethod.Delete) return new HttpResponseMessage(HttpStatusCode.OK);
            await _store.DeleteAsync(created.Id, Ct); // the row disappears while the host is answering
            return Json(HttpStatusCode.Created, Hosted(created.Id, created: _clock.Now));
        };

        var ex = await Assert.ThrowsAsync<ReviewRequestException>(() => Sender().SendAsync(_project, created.Id, new SendReviewRequest(true), Ct));

        Assert.Equal(("not_found", 404), (ex.Code, ex.Status));
        Assert.Equal(["POST /api/v1/review-requests", "DELETE /api/v1/review-requests/" + Reference],
            _requests.Select(r => r.Method + " " + r.RequestUri!.AbsolutePath));
        _archives.VerifyNoOtherCalls();
        Assert.Empty(_events.Events);
    }

    /// <summary>
    /// Codex slice E review, F2: when the hosted copy of a request deleted mid-send cannot be
    /// cancelled, the reference must survive locally so the poller retries and Delete waits for the
    /// host; the caller is never told the copy was cancelled.
    /// </summary>
    [Fact]
    public async Task ASendRacingADeleteKeepsACancelledRowWhenTheHostedCopyCannotBeCancelledYet()
    {
        var created = await CreateAsync();
        var hostedDelete = HttpStatusCode.ServiceUnavailable;
        _answer = async request =>
        {
            if (request.Method == HttpMethod.Delete) return new HttpResponseMessage(hostedDelete);
            await _store.DeleteAsync(created.Id, Ct);
            return Json(HttpStatusCode.Created, Hosted(created.Id, created: _clock.Now));
        };

        var ex = await Assert.ThrowsAsync<ReviewRequestException>(() => Sender().SendAsync(_project, created.Id, new SendReviewRequest(true), Ct));

        Assert.Equal(("remote_pending", 409), (ex.Code, ex.Status));
        Assert.DoesNotContain("was cancelled", ex.Message);
        var kept = await _store.GetAsync(created.Id, Ct);
        Assert.NotNull(kept);
        Assert.Equal(("cancelled", Reference, Fingerprint), (kept!.Status, kept.RemoteId, kept.SentKeyFingerprint));
        Assert.Null(kept.RemoteCancelledUTC);
        Assert.NotNull(kept.SessionsQueuedUTC);
        Assert.Contains(await _store.ListSentPendingAsync(DateTime.MinValue, Ct), row => row.Id == created.Id);
        _archives.VerifyNoOtherCalls();
        var evt = Assert.Single(_events.Events);
        Assert.Equal(("cancelled", true), (evt.Payload.GetProperty("status").GetString(), evt.Payload.GetProperty("sent").GetBoolean()));

        // Once the host answers, the kept row lets Delete finish the revocation and go.
        hostedDelete = HttpStatusCode.OK;
        _requests.Clear();
        await Sender().DeleteAsync(_project, created.Id, Ct);
        Assert.Equal(["DELETE /api/v1/review-requests/" + Reference], _requests.Select(r => r.Method + " " + r.RequestUri!.AbsolutePath));
        Assert.Null(await _store.GetAsync(created.Id, Ct));
    }

    /// <summary>
    /// Codex slice E re-review, F2 continued: the caller's HTTP request is aborted while the compensating
    /// hosted DELETE is in flight. The reference was made durable before that call, with its own token,
    /// so the retry survives whether the host then answers or not.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.OK)]
    public async Task ACallerCancellationDuringTheCompensatingCancelNeverLosesTheHostedReference(HttpStatusCode hostedDelete)
    {
        var created = await CreateAsync();
        using var caller = new CancellationTokenSource();
        _answer = async request =>
        {
            if (request.Method == HttpMethod.Delete)
            {
                caller.Cancel(); // the request that started the send disconnects while the hosted DELETE is in flight
                return new HttpResponseMessage(hostedDelete);
            }
            await _store.DeleteAsync(created.Id, Ct);
            return Json(HttpStatusCode.Created, Hosted(created.Id, created: _clock.Now));
        };

        var ex = await Assert.ThrowsAsync<ReviewRequestException>(() => Sender().SendAsync(_project, created.Id, new SendReviewRequest(true), caller.Token));

        Assert.Equal(["POST /api/v1/review-requests", "DELETE /api/v1/review-requests/" + Reference],
            _requests.Select(r => r.Method + " " + r.RequestUri!.AbsolutePath));
        if (hostedDelete == HttpStatusCode.OK)
        {
            Assert.Equal(("not_found", 404), (ex.Code, ex.Status));
            Assert.Null(await _store.GetAsync(created.Id, Ct));
            Assert.DoesNotContain(await _store.ListSentPendingAsync(DateTime.MinValue, Ct), row => row.Id == created.Id);
        }
        else
        {
            Assert.Equal(("remote_pending", 409), (ex.Code, ex.Status));
            var kept = await _store.GetAsync(created.Id, Ct);
            Assert.Equal(("cancelled", Reference, Fingerprint), (kept!.Status, kept.RemoteId, kept.SentKeyFingerprint));
            Assert.Null(kept.RemoteCancelledUTC);
            Assert.Contains(await _store.ListSentPendingAsync(DateTime.MinValue, Ct), row => row.Id == created.Id);
        }
        _archives.VerifyNoOtherCalls();
    }

    /// <summary>
    /// Codex slice E third review, F2 continued: the caller disconnects the moment the host has
    /// accepted the bundle, before anything was persisted. The send runs on, so the reference lands
    /// (or, after a raced delete, the revocation row does) and nothing hosted is left unreferenced.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ACallerCancellationRightAfterTheHostedSuccessStillPersistsTheReference(bool deletedDuringSend)
    {
        var created = await CreateAsync();
        using var caller = new CancellationTokenSource();
        _archives.Setup(a => a.EnsureSessionShareUploadAsync(It.IsAny<string>(), Fingerprint, It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _answer = async request =>
        {
            if (request.Method == HttpMethod.Delete) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            if (deletedDuringSend) await _store.DeleteAsync(created.Id, Ct);
            caller.Cancel(); // the browser is gone as the host answers 201; nothing has been stored yet
            return Json(HttpStatusCode.Created, Hosted(created.Id, created: _clock.Now));
        };

        if (deletedDuringSend)
        {
            var ex = await Assert.ThrowsAsync<ReviewRequestException>(() => Sender().SendAsync(_project, created.Id, new SendReviewRequest(true), caller.Token));
            Assert.Equal(("remote_pending", 409), (ex.Code, ex.Status));
            var kept = await _store.GetAsync(created.Id, Ct);
            Assert.Equal(("cancelled", Reference, Fingerprint), (kept!.Status, kept.RemoteId, kept.SentKeyFingerprint));
            Assert.Contains(await _store.ListSentPendingAsync(DateTime.MinValue, Ct), row => row.Id == created.Id);
            Assert.Equal(["POST /api/v1/review-requests", "DELETE /api/v1/review-requests/" + Reference],
                _requests.Select(r => r.Method + " " + r.RequestUri!.AbsolutePath));
            _archives.Verify(a => a.EnsureSessionShareUploadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        else
        {
            var sent = await Sender().SendAsync(_project, created.Id, new SendReviewRequest(true), caller.Token);
            Assert.Equal(Reference, sent.Request.Remote!.Id);
            var stored = await _store.GetAsync(created.Id, Ct);
            Assert.Equal((Reference, Fingerprint), (stored!.RemoteId, stored.SentKeyFingerprint));
            Assert.NotNull(stored.SessionsQueuedUTC);
            Assert.Equal(["POST /api/v1/review-requests"], _requests.Select(r => r.Method + " " + r.RequestUri!.AbsolutePath));
            _archives.Verify(a => a.EnsureSessionShareUploadAsync(It.IsAny<string>(), Fingerprint, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        }
    }

    [Theory]
    [InlineData("confirmed")]
    [InlineData("host_down")]
    [InlineData("other_account")]
    public async Task CancelCancelsTheHostedCopyWhenItCanAndLeavesTheRetryToThePoller(string variant)
    {
        var created = await SentAsync();
        _requests.Clear();
        _events.Events.Clear();
        _answer = _ => Task.FromResult(new HttpResponseMessage(variant == "host_down" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK));
        if (variant == "other_account") _key = "a-different-account-key";

        var summary = await Sender().CancelAsync(_project, created.Id, Ct);

        Assert.Equal("cancelled", summary.Status);
        var stored = await _store.GetAsync(created.Id, Ct);
        Assert.Equal("cancelled", stored!.Status);
        if (variant == "confirmed")
        {
            var request = Assert.Single(_requests);
            Assert.Equal(HttpMethod.Delete, request.Method);
            Assert.Equal("/api/v1/review-requests/" + Reference, request.RequestUri!.AbsolutePath);
            Assert.Equal(_clock.Now, stored.RemoteCancelledUTC);
            Assert.Equal(_clock.Now, summary.Remote!.CancelledUtc);
        }
        else
        {
            Assert.Equal(variant == "host_down" ? 1 : 0, _requests.Count);
            Assert.Null(stored.RemoteCancelledUTC);
            Assert.Null(summary.Remote!.CancelledUtc);
        }
        Assert.Equal(Reference, stored.RemoteId);
        var evt = Assert.Single(_events.Events);
        Assert.Equal("cancelled", evt.Payload.GetProperty("status").GetString());
    }

    /// <summary>Review F1: the row is the only thing that can still cancel the hosted copy, so it stays until the host confirms.</summary>
    [Theory]
    [InlineData("host_down")]
    [InlineData("other_account")]
    public async Task DeleteIsRefusedWhileTheHostedCopyIsStillReadable_ThenSucceedsOnceItIsCancelled(string variant)
    {
        var created = await SentAsync();
        _requests.Clear();
        _events.Events.Clear();
        _answer = _ => Task.FromResult(new HttpResponseMessage(variant == "host_down" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK));
        if (variant == "other_account") _key = "a-different-account-key";

        var refused = await Assert.ThrowsAsync<ReviewRequestException>(() => Sender().DeleteAsync(_project, created.Id, Ct));

        Assert.Equal(("remote_pending", 409), (refused.Code, refused.Status));
        Assert.Contains(variant == "other_account" ? "different account key" : "retried automatically", refused.Message);
        // Rob's decision (VIBE-213, item 5): the local cancel stays, and the refusal says what it costs.
        Assert.EndsWith(ReviewRequestSender.CancelledApprovalNote, refused.Message);
        var stored = await _store.GetAsync(created.Id, Ct);
        Assert.NotNull(stored);
        Assert.Equal(("cancelled", Reference), (stored.Status, stored.RemoteId));
        Assert.Null(stored.RemoteCancelledUTC);
        Assert.Equal(variant == "host_down" ? 1 : 0, _requests.Count(r => r.Method == HttpMethod.Delete));
        Assert.Equal("cancelled", _events.Events[^1].Payload.GetProperty("status").GetString());

        // The host answers again, or the sending account is back: the hosted copy is cancelled, then the row goes.
        _answer = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        _key = ApiKey;
        await Sender().DeleteAsync(_project, created.Id, Ct);

        Assert.EndsWith("/api/v1/review-requests/" + Reference, _requests[^1].RequestUri!.AbsolutePath);
        Assert.Equal(HttpMethod.Delete, _requests[^1].Method);
        Assert.Null(await _store.GetAsync(created.Id, Ct));
        Assert.Equal("deleted", _events.Events[^1].Payload.GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("unsent")]
    [InlineData("expired")]
    [InlineData("already_cancelled_on_host")]
    public async Task DeleteNeedsNoHostWhenNothingRemainsReadableThere(string variant)
    {
        var created = await CreateAsync();
        if (variant == "expired")
            Assert.NotNull(await _store.MarkSentAsync(created.Id, Reference, Fingerprint, _clock.Now.AddDays(-40), _clock.Now.AddDays(-9), Ct));
        if (variant == "already_cancelled_on_host")
        {
            Assert.NotNull(await _store.MarkSentAsync(created.Id, Reference, Fingerprint, _clock.Now, _clock.Now.AddMonths(1), Ct));
            Assert.True(await _store.MarkRemoteCancelledAsync(created.Id, _clock.Now, Ct));
        }
        _key = null; // no key at all: a delete that needed the host would be refused

        await Sender().DeleteAsync(_project, created.Id, Ct);

        Assert.Empty(_requests);
        Assert.Null(await _store.GetAsync(created.Id, Ct));
        var evt = Assert.Single(_events.Events);
        Assert.Equal("deleted", evt.Payload.GetProperty("status").GetString());
        Assert.Equal(variant != "unsent", evt.Payload.GetProperty("sent").GetBoolean());
    }

    private ReviewRequestSender Sender() => new(_store, _reviews, Transport(request =>
    {
        _requests.Add(request);
        return _answer(request);
    }), _archives.Object, _events, _polls, () => _key, _clock);

    private async Task<ReviewRequestSummary> CreateAsync() => await _reviews.CreateAsync(_project,
        new CreateReviewRequest(Reviewers: ["lead@example.com"], Sessions: [EndedSession, LiveSession], CardKey: "VB-50", Note: "Check the retry bound."),
        _sessions.Object, Ct);

    private async Task<ReviewRequestSummary> SentAsync()
    {
        var created = await CreateAsync();
        _archives.Setup(a => a.EnsureSessionShareUploadAsync(It.IsAny<string>(), Fingerprint, It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        return (await Sender().SendAsync(_project, created.Id, new SendReviewRequest(true), Ct)).Request;
    }

    private static string RequestIdOf(HttpRequestMessage request)
    {
        using var body = JsonDocument.Parse(request.Content!.ReadAsStream());
        return body.RootElement.GetProperty("requestId").GetString()!;
    }
}
