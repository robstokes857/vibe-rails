using Microsoft.Data.Sqlite;
using VibeRails.DB;
using Xunit;

namespace Tests.DB;

public sealed class PrReviewStoreTests : IDisposable
{
    private const string Repo = "octo/widgets";
    private static readonly PrReviewPullRequestFields NewPr =
        new("feat: add widgets", "octocat", new DateTime(2026, 8, 1, 14, 0, 0, DateTimeKind.Utc));

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"viberails-pr-review-test-{Guid.NewGuid():N}.db");
    private readonly string _connectionString;
    private readonly SteppingClock _clock = new(new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero));
    private readonly PrReviewStore _store;
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public PrReviewStoreTests()
    {
        _connectionString = $"Data Source={_dbPath};Mode=ReadWriteCreate";
        _store = new PrReviewStore(_connectionString, _clock);
    }

    public void Dispose()
    {
        SqliteConnection.ClearPool(new SqliteConnection(_connectionString));
        try { File.Delete(_dbPath); } catch (IOException) { }
    }

    [Fact]
    public async Task Start_creates_the_pr_and_one_pending_review_then_reuses_it()
    {
        var first = await _store.StartReviewAsync(Repo, 12, NewPr, "aaa111", _ct);
        var second = await _store.StartReviewAsync(Repo, 12, new PrReviewPullRequestFields(), null, _ct);

        Assert.Equal(PrReviewWriteOutcome.Created, first.Outcome);
        Assert.Equal(PrReviewWriteOutcome.Saved, second.Outcome);
        Assert.Equal(first.Review!.Id, second.Review!.Id);
        Assert.Equal("aaa111", second.Review.HeadSha);
        Assert.Equal(PrTrackerStatus.InReview, second.PullRequest!.Status);
        Assert.Single(await _store.GetReviewsAsync(Repo, 12, _ct));
    }

    [Fact]
    public async Task Start_replaces_the_pending_head_sha_only_when_one_is_supplied()
    {
        await _store.StartReviewAsync(Repo, 12, NewPr, "aaa111", _ct);

        var moved = await _store.StartReviewAsync(Repo, 12, new PrReviewPullRequestFields(), "bbb222", _ct);

        Assert.Equal("bbb222", moved.Review!.HeadSha);
    }

    [Theory]
    [InlineData(null, "octocat", true, "title")]
    [InlineData("A title", null, true, "author")]
    [InlineData("A title", "octocat", false, "openedAt")]
    public async Task Start_on_an_untracked_pr_names_the_missing_field_and_writes_nothing(
        string? title, string? author, bool opened, string expected)
    {
        var fields = new PrReviewPullRequestFields(title, author, opened ? NewPr.OpenedUtc : null);

        var result = await _store.StartReviewAsync(Repo, 12, fields, null, _ct);

        Assert.Equal(PrReviewWriteOutcome.MissingField, result.Outcome);
        Assert.Equal(expected, result.MissingField);
        Assert.Null(await _store.GetAsync(Repo, 12, _ct));
    }

    [Fact]
    public async Task A_tracked_pr_with_no_review_is_not_started()
    {
        var result = await _store.UpsertAsync(Repo, 12, NewPr, _ct);

        Assert.Equal(PrReviewWriteOutcome.Created, result.Outcome);
        Assert.Equal(PrTrackerStatus.NotStarted, result.PullRequest!.Status);
        Assert.Equal(PrGitHubState.Unknown, result.PullRequest.GitHubState);
    }

    [Fact]
    public async Task Draft_writes_only_supplied_fields_and_keeps_the_review_open()
    {
        await _store.StartReviewAsync(Repo, 12, NewPr, "aaa111", _ct);
        await _store.SaveDraftAsync(Repo, 12, new PrReviewFields(Feedback: "first finding", Blocking: true), null, _ct);

        var result = await _store.SaveDraftAsync(Repo, 12, new PrReviewFields(Verdict: PrReviewVerdict.Comments), "overview", _ct);

        Assert.Equal(PrReviewStatus.InProgress, result.Review!.Status);
        Assert.Equal("first finding", result.Review.Feedback);
        Assert.True(result.Review.Blocking);
        Assert.Equal(PrReviewVerdict.Comments, result.Review.Verdict);
        Assert.Equal("aaa111", result.Review.HeadSha);
        Assert.Equal("overview", result.PullRequest!.Summary);
        Assert.Null(result.Review.ReviewedUtc);
    }

    [Fact]
    public async Task Draft_and_complete_tell_untracked_apart_from_tracked_without_a_pending_review()
    {
        Assert.Equal(PrReviewWriteOutcome.NotTracked,
            (await _store.SaveDraftAsync(Repo, 12, new PrReviewFields(Feedback: "x"), null, _ct)).Outcome);

        await _store.UpsertAsync(Repo, 12, NewPr, _ct);

        Assert.Equal(PrReviewWriteOutcome.NoPendingReview,
            (await _store.SaveDraftAsync(Repo, 12, new PrReviewFields(Feedback: "x"), null, _ct)).Outcome);
        Assert.Equal(PrReviewWriteOutcome.NoPendingReview,
            (await _store.CompleteReviewAsync(Repo, 12, PrReviewVerdict.Comments, false, "x", null, null, _ct)).Outcome);
    }

    [Fact]
    public async Task Complete_stamps_the_review_and_exposes_its_head_for_the_queue()
    {
        await _store.StartReviewAsync(Repo, 12, NewPr, "aaa111", _ct);

        var result = await _store.CompleteReviewAsync(
            Repo, 12, PrReviewVerdict.RecommendApprove, false, "## Lead review", "bbb222", null, _ct);

        Assert.Equal(PrReviewStatus.Completed, result.Review!.Status);
        Assert.NotNull(result.Review.ReviewedUtc);
        Assert.Equal(PrTrackerStatus.Complete, result.PullRequest!.Status);
        Assert.Equal("bbb222", result.PullRequest.ReviewedHeadSha);
        Assert.Equal(1, result.PullRequest.CompletedCount);
        Assert.Equal(result.Review.ReviewedUtc, result.PullRequest.LastReviewedUtc);
    }

    [Fact]
    public async Task A_blocking_newest_review_makes_the_pr_a_blocker_until_a_later_clean_pass()
    {
        await CompleteAsync(blocking: true);
        Assert.Equal(PrTrackerStatus.Blocker, (await _store.GetAsync(Repo, 12, _ct))!.Status);

        await CompleteAsync(blocking: false);

        Assert.Equal(PrTrackerStatus.Complete, (await _store.GetAsync(Repo, 12, _ct))!.Status);
    }

    [Fact]
    public async Task An_open_review_outranks_a_standing_blocker()
    {
        await CompleteAsync(blocking: true);

        await _store.StartReviewAsync(Repo, 12, new PrReviewPullRequestFields(), null, _ct);

        Assert.Equal(PrTrackerStatus.InReview, (await _store.GetAsync(Repo, 12, _ct))!.Status);
    }

    [Fact]
    public async Task Flipping_blocking_on_the_newest_review_moves_the_derived_status()
    {
        var review = await CompleteAsync(blocking: false);

        var updated = await _store.UpdateReviewAsync(Repo, review.Id, new PrReviewFields(Blocking: true), _ct);

        Assert.True(updated!.Blocking);
        Assert.Equal(PrReviewStatus.Completed, updated.Status);
        Assert.Equal(PrTrackerStatus.Blocker, (await _store.GetAsync(Repo, 12, _ct))!.Status);
    }

    [Fact]
    public async Task History_lists_the_pending_review_first_then_completed_newest_first()
    {
        var older = await CompleteAsync(blocking: false);
        var newer = await CompleteAsync(blocking: true);
        var pending = (await _store.StartReviewAsync(Repo, 12, new PrReviewPullRequestFields(), null, _ct)).Review!;

        var history = await _store.GetReviewsAsync(Repo, 12, _ct);

        Assert.Equal([pending.Id, newer.Id, older.Id], history.Select(r => r.Id));
    }

    [Fact]
    public async Task The_list_is_ordered_by_last_review_write()
    {
        await _store.UpsertAsync(Repo, 1, NewPr, _ct);
        await _store.UpsertAsync(Repo, 2, NewPr, _ct);
        await _store.StartReviewAsync(Repo, 1, new PrReviewPullRequestFields(), null, _ct);

        var list = await _store.ListAsync(Repo, _ct);

        Assert.Equal([1, 2], list.Select(pr => pr.Number));
    }

    [Fact]
    public async Task Recording_github_states_never_moves_the_last_review_write()
    {
        await _store.UpsertAsync(Repo, 1, NewPr, _ct);
        await _store.UpsertAsync(Repo, 2, NewPr, _ct);
        var before = await _store.ListAsync(Repo, _ct);
        var mergedAt = new DateTime(2026, 8, 25, 12, 0, 0, DateTimeKind.Utc);

        var counts = await _store.RecordGitHubStatesAsync(Repo,
        [
            new PrGitHubStateUpdate(1, PrGitHubState.Merged, mergedAt),
            new PrGitHubStateUpdate(2, PrGitHubState.Open, null),
            new PrGitHubStateUpdate(99, PrGitHubState.Closed, null),
        ], _ct);
        var again = await _store.RecordGitHubStatesAsync(Repo, [new PrGitHubStateUpdate(2, PrGitHubState.Open, null)], _ct);

        var after = await _store.ListAsync(Repo, _ct);
        Assert.Equal(new PrGitHubSyncCounts(2, 2), counts);
        Assert.Equal(new PrGitHubSyncCounts(1, 0), again);
        Assert.Equal(before.Select(pr => (pr.Number, pr.UpdatedUtc)), after.Select(pr => (pr.Number, pr.UpdatedUtc)));
        var merged = after.Single(pr => pr.Number == 1);
        Assert.Equal(PrGitHubState.Merged, merged.GitHubState);
        Assert.Equal(mergedAt, merged.GitHubStateUtc);
        Assert.NotNull(merged.GitHubCheckedUtc);
    }

    [Fact]
    public async Task An_unrecognised_github_state_is_refused_before_anything_is_written()
    {
        await _store.UpsertAsync(Repo, 1, NewPr, _ct);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _store.RecordGitHubStatesAsync(Repo, [new PrGitHubStateUpdate(1, "draft", null)], _ct));

        Assert.Equal(PrGitHubState.Unknown, (await _store.GetAsync(Repo, 1, _ct))!.GitHubState);
    }

    [Fact]
    public async Task Repositories_are_separate_and_matched_case_insensitively()
    {
        await _store.StartReviewAsync(Repo, 12, NewPr, null, _ct);
        await _store.UpsertAsync("someone/else", 12, NewPr, _ct);

        Assert.Equal(PrTrackerStatus.InReview, (await _store.GetAsync("OCTO/Widgets", 12, _ct))!.Status);
        Assert.Equal(PrTrackerStatus.NotStarted, (await _store.GetAsync("someone/else", 12, _ct))!.Status);
        Assert.Equal([12], await _store.ListNumbersAsync("Octo/Widgets", _ct));
    }

    [Fact]
    public async Task A_review_from_another_repository_cannot_be_edited_or_deleted()
    {
        var review = await CompleteAsync(blocking: false);

        Assert.Null(await _store.UpdateReviewAsync("someone/else", review.Id, new PrReviewFields(Blocking: true), _ct));
        Assert.Null(await _store.DeleteReviewAsync("someone/else", review.Id, _ct));
        Assert.False((await _store.GetReviewsAsync(Repo, 12, _ct)).Single().Blocking);
    }

    [Fact]
    public async Task Deleting_a_review_returns_its_pr_and_re_derives_the_status()
    {
        await CompleteAsync(blocking: false);
        var blocker = await CompleteAsync(blocking: true);

        Assert.Equal(12, await _store.DeleteReviewAsync(Repo, blocker.Id, _ct));

        Assert.Equal(PrTrackerStatus.Complete, (await _store.GetAsync(Repo, 12, _ct))!.Status);
    }

    [Fact]
    public async Task Deleting_a_pr_removes_its_reviews()
    {
        await CompleteAsync(blocking: false);

        Assert.True(await _store.DeleteAsync(Repo, 12, _ct));
        Assert.False(await _store.DeleteAsync(Repo, 12, _ct));

        Assert.Empty(await _store.GetReviewsAsync(Repo, 12, _ct));
        await _store.UpsertAsync(Repo, 12, NewPr, _ct);
        Assert.Equal(0, (await _store.GetAsync(Repo, 12, _ct))!.ReviewCount);
    }

    [Fact]
    public async Task Update_refuses_an_untracked_pr_and_keeps_omitted_fields()
    {
        Assert.Equal(PrReviewWriteOutcome.NotTracked,
            (await _store.UpdateAsync(Repo, 12, new PrReviewPullRequestFields(Title: "x"), _ct)).Outcome);
        await _store.UpsertAsync(Repo, 12, NewPr with { Summary = "keep me" }, _ct);

        var result = await _store.UpdateAsync(Repo, 12, new PrReviewPullRequestFields(Title: "renamed"), _ct);

        Assert.Equal("renamed", result.PullRequest!.Title);
        Assert.Equal("octocat", result.PullRequest.Author);
        Assert.Equal("keep me", result.PullRequest.Summary);
    }

    [Fact]
    public async Task The_database_allows_only_one_pending_review_per_pr()
    {
        await _store.StartReviewAsync(Repo, 12, NewPr, null, _ct);

        await using var db = new SqliteConnection(_connectionString);
        await db.OpenAsync(_ct);
        await using var insert = db.CreateCommand();
        insert.CommandText = """
            INSERT INTO PrReviews (Repository, PrNumber, Status, StartedUTC, UpdatedUTC)
            VALUES ('octo/widgets', 12, 'in_progress', 'x', 'x');
            """;
        var error = await Assert.ThrowsAsync<SqliteException>(() => insert.ExecuteNonQueryAsync(_ct));
        Assert.Contains("UNIQUE", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<PrReview> CompleteAsync(bool blocking)
    {
        await _store.StartReviewAsync(Repo, 12, NewPr, null, _ct);
        var verdict = blocking ? PrReviewVerdict.RequestChanges : PrReviewVerdict.RecommendApprove;
        return (await _store.CompleteReviewAsync(Repo, 12, verdict, blocking, "review body", null, null, _ct)).Review!;
    }

    /// <summary>Moves one second forward on every read so each write gets a distinct timestamp.</summary>
    private sealed class SteppingClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now = _now.AddSeconds(1);
    }
}
