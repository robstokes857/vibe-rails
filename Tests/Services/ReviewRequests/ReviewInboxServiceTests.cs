using System.Net;
using VibeRails.Services.ReviewRequests;
using Xunit;

namespace Tests.Services.ReviewRequests;

/// <summary>
/// "Reviews requested of you" (VB-50 slice D): the hosted inbox fetched on the Board sync cadence and
/// persisted beside state.db, a rejection posted to viberails.ai, and an approval that only ever goes
/// through the signing popup the service opens and watches.
/// </summary>
public sealed class ReviewInboxServiceTests : IDisposable
{
    private const string Reference = ReviewRequestTestSupport.Reference;
    private const string Project = @"C:\work\vibe-rails";
    private static readonly DateTime Start = new(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "review-inbox-" + Guid.NewGuid().ToString("N"));
    private readonly ReviewRequestTestSupport.Clock _clock = new(Start);
    private readonly ReviewRequestTestSupport.RecordingEventBus _events = new();
    private readonly List<HttpRequestMessage> _requests = [];
    private readonly FakeLauncher _launcher = new();
    private Func<HttpRequestMessage, Task<HttpResponseMessage>> _answer;
    private string? _key = "key-1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ReviewInboxServiceTests()
    {
        Directory.CreateDirectory(_directory);
        _answer = _ => Task.FromResult(Inbox(InboxItem()));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task FetchesWhenDueAndPacesFifteenMinutesWhileARequestIsOpen()
    {
        var state = new ReviewInboxState();
        var service = Service(state);

        await service.RefreshDueAsync(Ct);

        var seen = Assert.Single(_requests);
        Assert.Equal(("GET", "https://viberails.ai/api/v1/review-requests/inbox", "key-1"),
            (seen.Method.Method, seen.RequestUri!.AbsoluteUri, seen.Headers.GetValues("X-Api-Key").Single()));
        var view = service.View(Project);
        var item = Assert.Single(view.Requests);
        Assert.Equal((Reference, "vibe-rails", "dev@example.test", "open", false, $"https://viberails.ai/Reviews/requests/{Reference}", Project, true),
            (item.Id, item.Repository, item.RequestedBy, item.Status, item.Decided, item.ReadUrl, view.Project, view.PopupAvailable));
        Assert.Equal((true, "me@example.test", Start, Start.AddMinutes(15)), (view.HasApiKey, view.Email, view.FetchedUtc, view.NextDueUtc));
        Assert.Contains(_events.Events, e => e.Type == ReviewInboxEvents.Updated);

        _clock.Now = Start.AddMinutes(14);
        await service.RefreshDueAsync(Ct);
        Assert.Single(_requests); // Not due: never the scheduler's 60 s tick.

        _clock.Now = Start.AddMinutes(15);
        await service.RefreshDueAsync(Ct);
        Assert.Equal(2, _requests.Count);
    }

    [Fact]
    public async Task BacksOffToTwoHoursWhileNothingIsOpenOrTheHostIsUnreachable()
    {
        var state = new ReviewInboxState();
        var service = Service(state);
        _answer = _ => Task.FromResult(Inbox());

        var expected = new[] { 15, 30, 60, 120, 120 };
        foreach (var minutes in expected)
        {
            await service.RefreshDueAsync(Ct);
            Assert.Equal(_clock.Now.AddMinutes(minutes), state.NextDueUtc);
            _clock.Now = state.NextDueUtc;
        }

        _answer = _ => throw new HttpRequestException("offline");
        await service.RefreshDueAsync(Ct);
        var view = service.View(Project);
        Assert.Equal("network_error", view.LastErrorCode);
        Assert.Equal(_clock.Now.AddMinutes(120), view.NextDueUtc);

        // An open request answered again returns to the 15-minute cadence.
        _clock.Now = state.NextDueUtc;
        _answer = _ => Task.FromResult(Inbox(InboxItem()));
        await service.RefreshDueAsync(Ct);
        Assert.Null(service.View(Project).LastErrorCode);
        Assert.Equal(_clock.Now.AddMinutes(15), state.NextDueUtc);
    }

    [Fact]
    public async Task TheListAndTheNextDueTimeArePersistedForEveryRoot()
    {
        var path = Path.Combine(_directory, ReviewInboxState.FileName);
        var service = Service(new ReviewInboxState(path));

        await service.RefreshDueAsync(Ct);

        var other = Service(new ReviewInboxState(path));
        var view = other.View(Project);
        Assert.Single(view.Requests);
        Assert.Equal(Start, view.FetchedUtc);
        await other.RefreshDueAsync(Ct);
        Assert.Single(_requests); // The other root saw the due time and did not fetch again.
        Assert.DoesNotContain("key-1", await File.ReadAllTextAsync(path, Ct));
    }

    [Fact]
    public async Task AnotherKeyStartsFromAnEmptyList()
    {
        var service = Service(new ReviewInboxState());
        await service.RefreshDueAsync(Ct);
        Assert.Single(service.View(Project).Requests);

        _key = "key-2";
        var view = service.View(Project);
        Assert.Empty(view.Requests);
        Assert.Null(view.FetchedUtc);

        _key = null;
        Assert.False(service.View(Project).HasApiKey);
        await service.RefreshDueAsync(Ct);
        Assert.Single(_requests);
    }

    [Fact]
    public async Task ManualRefreshFetchesAtOnceButNotTwiceWithinTheSpacing()
    {
        var service = Service(new ReviewInboxState());

        await service.RefreshNowAsync(Project, Ct);
        await service.RefreshNowAsync(Project, Ct);
        Assert.Single(_requests);

        _clock.Now = Start + ReviewInboxService.ManualRefreshSpacing;
        await service.RefreshNowAsync(Project, Ct);
        Assert.Equal(2, _requests.Count);
    }

    [Fact]
    public async Task RejectPostsTheCommentAndMarksTheEntryDecided()
    {
        var service = Service(new ReviewInboxState());
        await service.RefreshDueAsync(Ct);
        string? body = null;
        _answer = async request =>
        {
            body = await request.Content!.ReadAsStringAsync();
            return ReviewRequestTestSupport.Json(HttpStatusCode.Created,
                new { id = 9, reviewer = "me@example.test", decision = "rejected", comment = "Retry path still swallows the error.", decidedUtc = Start });
        };

        var result = await service.RejectAsync(Reference, new ReviewInboxRejectRequest("  Retry path still swallows the error. "), Ct);

        Assert.Equal((Reference, "rejected"), (result.Reference, result.Status));
        Assert.Equal(("POST", $"https://viberails.ai/api/v1/review-requests/{Reference}/decisions"), (_requests[^1].Method.Method, _requests[^1].RequestUri!.AbsoluteUri));
        Assert.Equal("""{"decision":"rejected","comment":"Retry path still swallows the error."}""", body);
        var item = Assert.Single(service.View(Project).Requests);
        Assert.Equal(("rejected", true), (item.Status, item.Decided));
    }

    [Fact]
    public async Task RejectRefusesWithoutACommentAKeyOrAReference()
    {
        var service = Service(new ReviewInboxState());

        var empty = await Assert.ThrowsAsync<ReviewRequestException>(() => service.RejectAsync(Reference, new ReviewInboxRejectRequest("  "), Ct));
        Assert.Equal(("invalid_request", 400), (empty.Code, empty.Status));
        var bad = await Assert.ThrowsAsync<ReviewRequestException>(() => service.RejectAsync("rvw_nope", new ReviewInboxRejectRequest("x"), Ct));
        Assert.Equal("invalid_request", bad.Code);
        _key = null;
        var noKey = await Assert.ThrowsAsync<ReviewRequestException>(() => service.RejectAsync(Reference, new ReviewInboxRejectRequest("x"), Ct));
        Assert.Equal(("no_api_key", 409), (noKey.Code, noKey.Status));
        Assert.Empty(_requests);
    }

    [Fact]
    public async Task RejectKeepsTheHostsRefusal()
    {
        var service = Service(new ReviewInboxState());
        _answer = _ => Task.FromResult(ReviewRequestTestSupport.Json(HttpStatusCode.Conflict, new { error = "remote prose", code = "not_a_reviewer" }));

        var ex = await Assert.ThrowsAsync<ReviewRequestTransportException>(() => service.RejectAsync(Reference, new ReviewInboxRejectRequest("x"), Ct));

        Assert.Equal(("not_a_reviewer", 409), (ex.Code, ex.HttpStatus));
        Assert.DoesNotContain("remote prose", ex.Message);
    }

    [Fact]
    public async Task ApproveOpensThePopupOnceAndRecordsHowItEnded()
    {
        var service = Service(new ReviewInboxState());
        await service.RefreshDueAsync(Ct);

        var opened = service.BeginApprove(Reference, Project);

        Assert.Equal(("signing", "signing"), (opened.Status, opened.Signing!.Outcome));
        Assert.Equal((Reference, Project), Assert.Single(_launcher.Launched));
        Assert.Equal(["--vca-hook", "sign-review", "--review", Reference, "--workdir", Project, "--console-window"],
            ConsoleReviewSignaturePopupLauncher.Arguments(Reference, Project));
        var again = Assert.Throws<ReviewRequestException>(() => service.BeginApprove(Reference, Project));
        Assert.Equal(("signing_in_progress", 409), (again.Code, again.Status));
        Assert.Equal("signing", service.View(Project).Requests.Single().Signing!.Outcome);

        _launcher.Finish(0);
        var signing = await WaitForOutcomeAsync(service, "approved");
        Assert.Equal(0, signing.ExitCode);
        var item = service.View(Project).Requests.Single();
        Assert.Equal(("approved", true), (item.Status, item.Decided));

        // A later attempt on the same request is allowed again (for a request that was not approved).
        service.BeginApprove(Reference, Project);
        _launcher.Finish(1);
        Assert.Equal(1, (await WaitForOutcomeAsync(service, "not_approved")).ExitCode);

        service.BeginApprove(Reference, Project);
        _launcher.Finish(null);
        Assert.Null((await WaitForOutcomeAsync(service, "popup_unavailable")).ExitCode);
    }

    // VIBE-213 item 7 (Rob, 2026-10-25): with more than one root open the page picks the clone to rebuild in.
    [Fact]
    public void ClonesListThisProjectFirstThenEachOtherOpenRootOnce()
    {
        var here = Path.Combine(_directory, "vibe-rails");
        var zeta = Path.Combine(_directory, "src", "zeta");
        var alpha = Path.Combine(_directory, "src", "Alpha");
        var clones = ReviewInboxService.Clones(here, [here + Path.DirectorySeparatorChar, zeta, alpha, zeta + Path.DirectorySeparatorChar, " "]);

        Assert.Equal([(here, "vibe-rails", true), (alpha, "Alpha", false), (zeta, "zeta", false)],
            clones.Select(clone => (clone.Path, clone.Name, clone.Current)));
        Assert.Equal([(here, "vibe-rails", true)], ReviewInboxService.Clones(here, null).Select(c => (c.Path, c.Name, c.Current)));
        if (OperatingSystem.IsWindows())
            Assert.Equal(3, ReviewInboxService.Clones(here, [zeta, alpha, zeta.ToUpperInvariant()]).Count);
        Assert.Equal(3, Service(new ReviewInboxState()).View(here, [zeta, alpha]).Clones!.Count);
    }

    [Fact]
    public async Task ApproveRebuildsInTheChosenOpenCloneAndRefusesAnyOtherFolder()
    {
        var service = Service(new ReviewInboxState());
        await service.RefreshDueAsync(Ct);
        var review = Path.Combine(_directory, "vibe-rails-review");
        IReadOnlyList<string> open = [review];

        var refused = Assert.Throws<ReviewRequestException>(() =>
            service.BeginApprove(Reference, Project, Path.Combine(_directory, "elsewhere"), open));
        Assert.Equal(("invalid_request", 400), (refused.Code, refused.Status));
        Assert.Empty(_launcher.Launched);

        // The listed spelling is what reaches argv, whatever trailing separator (or, on Windows, case) the page sent.
        service.BeginApprove(Reference, Project, (OperatingSystem.IsWindows() ? review.ToUpperInvariant() : review) + Path.DirectorySeparatorChar, open);
        Assert.Equal((Reference, review), Assert.Single(_launcher.Launched));
        _launcher.Finish(1);
        await WaitForOutcomeAsync(service, "not_approved");

        service.BeginApprove(Reference, Project, null, open);
        Assert.Equal((Reference, Project), _launcher.Launched[^1]);
    }

    [Fact]
    public async Task ApproveRefusesWithoutAPopupOrAKey()
    {
        var service = Service(new ReviewInboxState());
        await service.RefreshDueAsync(Ct);

        _launcher.Available = false;
        var noPopup = Assert.Throws<ReviewRequestException>(() => service.BeginApprove(Reference, Project));
        Assert.Equal(("popup_unavailable", 409), (noPopup.Code, noPopup.Status));
        Assert.False(service.View(Project).PopupAvailable);

        _launcher.Available = true;
        _key = null;
        Assert.Equal("no_api_key", Assert.Throws<ReviewRequestException>(() => service.BeginApprove(Reference, Project)).Code);
        Assert.Empty(_launcher.Launched);
    }

    private ReviewInboxService Service(ReviewInboxState state) => new(
        () => ReviewRequestTestSupport.Transport(request =>
        {
            _requests.Add(request);
            return _answer(request);
        }),
        state,
        () => _key,
        () => _key is null ? null : "me@example.test",
        _events,
        _launcher,
        _clock);

    private static async Task<ReviewInboxSigning> WaitForOutcomeAsync(ReviewInboxService service, string outcome)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var signing = service.View(Project).Requests.Single().Signing;
            if (signing?.Outcome == outcome) return signing;
            await Task.Delay(20, Ct);
        }
        throw new TimeoutException($"The popup outcome never became {outcome}.");
    }

    private static object InboxItem(string status = "open", bool decided = false) => new
    {
        id = Reference, repository = "vibe-rails", requestedBy = "dev@example.test", status, fileCount = 2, sessionCount = 1,
        cardKey = "VIBE-208", createdUtc = Start, expiresUtc = Start.AddMonths(1), decided
    };

    private static HttpResponseMessage Inbox(params object[] items) =>
        ReviewRequestTestSupport.Json(HttpStatusCode.OK, new { emailVerified = true, requests = items, more = false });

    private sealed class FakeLauncher : IReviewSignaturePopupLauncher
    {
        private TaskCompletionSource<int?> _pending = new();
        public bool Available { get; set; } = true;
        public List<(string Reference, string Repository)> Launched { get; } = [];

        public Task<int?> LaunchAsync(string reference, string repositoryPath, CancellationToken ct)
        {
            Launched.Add((reference, repositoryPath));
            _pending = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
            return _pending.Task;
        }

        public void Finish(int? exitCode) => _pending.TrySetResult(exitCode);
    }
}
