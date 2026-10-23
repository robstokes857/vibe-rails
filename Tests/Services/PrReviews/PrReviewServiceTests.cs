using System.Text.Json;
using Microsoft.Data.Sqlite;
using Moq;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services.PrReviews;
using Xunit;

namespace Tests.Services.PrReviews;

public sealed class PrReviewServiceTests : IDisposable
{
    private const string Project = "C:/work/widgets";
    private static readonly GitHubRepository Repo = new("octo", "widgets");
    private static readonly PrReviewRequest NewPr = new(Title: "feat: widgets", Author: "octocat", OpenedAt: "2026-08-01T14:00:00Z");

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"viberails-pr-review-service-{Guid.NewGuid():N}.db");
    private readonly string _connectionString;
    private readonly Mock<IGitHubPullRequestClient> _github = new(MockBehavior.Strict);
    private readonly Mock<IPrReviewRepositoryResolver> _repositories = new();
    private readonly PrReviewService _service;
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public PrReviewServiceTests()
    {
        _connectionString = $"Data Source={_dbPath};Mode=ReadWriteCreate";
        _repositories.Setup(r => r.ResolveAsync(Project, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PrReviewProject(Project, Repo, null));
        _service = new PrReviewService(new PrReviewStore(_connectionString), _github.Object, _repositories.Object);
    }

    public void Dispose()
    {
        SqliteConnection.ClearPool(new SqliteConnection(_connectionString));
        try { File.Delete(_dbPath); } catch (IOException) { }
    }

    [Fact]
    public async Task Start_complete_and_read_back_a_review()
    {
        var started = await _service.StartAsync(Project, 7, NewPr with { HeadSha = " abc123 " }, _ct);
        var completed = await _service.CompleteAsync(Project, 7,
            new PrReviewRequest(Feedback: "## Lead review", Verdict: PrReviewVerdict.RequestChanges), _ct);

        var detail = await _service.GetAsync(Project, 7, _ct);

        Assert.True(started.Created);
        Assert.Equal("abc123", started.Review!.HeadSha);
        Assert.True(completed.Review!.Blocking);
        Assert.Equal(PrTrackerStatus.Blocker, detail.PullRequest.Status);
        Assert.Equal("https://github.com/octo/widgets/pull/7", detail.Url);
        Assert.Single(detail.Reviews);
    }

    [Fact]
    public async Task Starting_at_the_current_head_keeps_later_commits_eligible()
    {
        await _service.StartAsync(Project, 7, NewPr with { HeadSha = "old1234" }, _ct);
        await _service.CompleteAsync(Project, 7, new PrReviewRequest(Feedback: "old pass", Verdict: "comments"), _ct);
        var live = new GitHubPullRequest(7, "Updated title", "hubot", new DateTime(2026, 8, 1), null,
            false, "current1234", Repo.PullRequestUrl(7));
        _github.Setup(g => g.GetAsync(Repo, Project, 7, It.IsAny<CancellationToken>())).ReturnsAsync(live);

        var started = await _service.StartAsync(Project, 7, new PrReviewRequest(UseCurrentHead: true), _ct);
        var completed = await _service.CompleteAsync(Project, 7,
            new PrReviewRequest(Feedback: "new pass", Verdict: "comments"), _ct);
        _github.Setup(g => g.ListOpenAsync(Repo, Project, It.IsAny<CancellationToken>()))
            .ReturnsAsync([live with { HeadSha = "later5678" }]);
        var queue = await _service.QueueAsync(Project, _ct);

        Assert.True(started.Created);
        Assert.Equal("Updated title", started.PullRequest.Title);
        Assert.Equal("current1234", completed.PullRequest.ReviewedHeadSha);
        Assert.Equal(PrReviewQueue.NewCommits, queue.Next!.Reason);
    }

    [Fact]
    public async Task A_current_head_lookup_can_create_an_untracked_pr_by_number()
    {
        _github.Setup(g => g.GetAsync(Repo, Project, 7, It.IsAny<CancellationToken>())).ReturnsAsync(
            new GitHubPullRequest(7, "Live title", "hubot", new DateTime(2026, 8, 1), null,
                false, "current1234", Repo.PullRequestUrl(7)));

        var started = await _service.StartAsync(Project, 7, new PrReviewRequest(UseCurrentHead: true), _ct);

        Assert.True(started.Created);
        Assert.Equal("current1234", started.Review!.HeadSha);
        Assert.Equal("Live title", started.PullRequest.Title);
    }

    [Fact]
    public async Task A_failed_current_head_lookup_does_not_start_a_review()
    {
        await _service.UpsertAsync(Project, NewPr with { Number = 7 }, _ct);
        _github.Setup(g => g.GetAsync(Repo, Project, 7, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubCliException("The GitHub CLI (gh) is not signed in."));

        await AssertError(502, "The GitHub CLI (gh) is not signed in.", () =>
            _service.StartAsync(Project, 7, new PrReviewRequest(UseCurrentHead: true), _ct));

        Assert.Empty((await _service.GetAsync(Project, 7, _ct)).Reviews);
    }

    [Fact]
    public async Task An_explicit_blocking_flag_overrides_the_verdict_default()
    {
        await _service.StartAsync(Project, 7, NewPr, _ct);

        var completed = await _service.CompleteAsync(Project, 7,
            new PrReviewRequest(Feedback: "body", Verdict: PrReviewVerdict.Comments, Blocking: true), _ct);

        Assert.True(completed.Review!.Blocking);
        Assert.Equal(PrTrackerStatus.Blocker, completed.PullRequest.Status);
    }

    [Theory]
    [InlineData("createdAt")]
    [InlineData("updatedAt")]
    [InlineData("startedAt")]
    [InlineData("reviewedAt")]
    public async Task Server_owned_timestamps_are_refused_by_name(string field)
    {
        var request = JsonSerializer.Deserialize($$"""{"title":"t","{{field}}":null}""",
            AppJsonSerializerContext.Default.PrReviewRequest)!;

        var error = await Assert.ThrowsAsync<PrReviewException>(() => _service.StartAsync(Project, 7, request, _ct));

        Assert.Equal(400, error.StatusCode);
        Assert.Equal($"{field} is managed by the server", error.Message);
    }

    [Fact]
    public async Task Validation_names_the_field_and_the_fix()
    {
        await AssertError(400, "title is required for a new PR", () => _service.StartAsync(Project, 7, null, _ct));
        await AssertError(400, "openedAt must be a valid date or timestamp",
            () => _service.StartAsync(Project, 7, NewPr with { OpenedAt = "yesterday-ish" }, _ct));
        await AssertError(400, "title must be 500 characters or fewer",
            () => _service.StartAsync(Project, 7, NewPr with { Title = new string('x', 501) }, _ct));
        await AssertError(400, "PR number must be a positive integer", () => _service.StartAsync(Project, 0, NewPr, _ct));
        await AssertError(400, "number is required", () => _service.UpsertAsync(Project, NewPr, _ct));

        await _service.StartAsync(Project, 7, NewPr, _ct);
        await AssertError(400, "feedback is required",
            () => _service.CompleteAsync(Project, 7, new PrReviewRequest(Feedback: "   ", Verdict: "comments"), _ct));
        await AssertError(400, "verdict is required",
            () => _service.CompleteAsync(Project, 7, new PrReviewRequest(Feedback: "body"), _ct));
        await AssertError(400, "verdict must be recommend_approve, comments, or request_changes",
            () => _service.DraftAsync(Project, 7, new PrReviewRequest(Verdict: "approve"), _ct));
    }

    [Fact]
    public async Task Untracked_and_not_started_are_different_errors()
    {
        await AssertError(404, "PR #7 is not tracked; start a review for it first",
            () => _service.DraftAsync(Project, 7, new PrReviewRequest(Feedback: "x"), _ct));

        await _service.UpsertAsync(Project, NewPr with { Number = 7 }, _ct);

        await AssertError(409, "PR #7 does not have an in-progress review",
            () => _service.CompleteAsync(Project, 7, new PrReviewRequest(Feedback: "x", Verdict: "comments"), _ct));
    }

    [Fact]
    public async Task A_blank_title_keeps_the_stored_one_but_a_blank_summary_clears()
    {
        await _service.UpsertAsync(Project, NewPr with { Number = 7, Summary = "overview" }, _ct);

        var updated = await _service.UpdateAsync(Project, 7, new PrReviewRequest(Title: "  ", Summary: ""), _ct);

        Assert.Equal("feat: widgets", updated.Title);
        Assert.Equal("", updated.Summary);
    }

    [Fact]
    public async Task Missing_records_are_404s()
    {
        await AssertError(404, "PR #7 was not found.", () => _service.GetAsync(Project, 7, _ct));
        await AssertError(404, "PR #7 was not found.", () => _service.UpdateAsync(Project, 7, new PrReviewRequest(Title: "x"), _ct));
        await AssertError(404, "PR #7 was not found.", () => _service.DeleteAsync(Project, 7, _ct));
        await AssertError(404, "Review 99 was not found.", () => _service.UpdateReviewAsync(Project, 99, new PrReviewRequest(), _ct));
        await AssertError(404, "Review 99 was not found.", () => _service.DeleteReviewAsync(Project, 99, _ct));
    }

    [Fact]
    public async Task A_project_without_a_github_repository_lists_nothing_and_says_why()
    {
        _repositories.Setup(r => r.ResolveAsync("C:/elsewhere", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PrReviewProject("C:/elsewhere", null, "This project's origin remote is not a github.com repository."));

        var list = await _service.ListAsync("C:/elsewhere", _ct);

        Assert.Null(list.Repository);
        Assert.Equal("This project's origin remote is not a github.com repository.", list.Problem);
        Assert.Empty(list.PullRequests);
        await AssertError(409, "This project's origin remote is not a github.com repository.",
            () => _service.StartAsync("C:/elsewhere", 7, NewPr, _ct));
    }

    [Fact]
    public async Task The_queue_merges_github_with_the_tracker()
    {
        await _service.StartAsync(Project, 7, NewPr, _ct);
        _github.Setup(g => g.ListOpenAsync(Repo, Project, It.IsAny<CancellationToken>())).ReturnsAsync([
            new GitHubPullRequest(7, "feat: widgets", "octocat", new DateTime(2026, 8, 1), null, false, "abc", Repo.PullRequestUrl(7)),
            new GitHubPullRequest(8, "fix: gears", "hubot", new DateTime(2026, 7, 1), null, false, "def", Repo.PullRequestUrl(8)),
        ]);

        var queue = await _service.QueueAsync(Project, _ct);

        Assert.Equal(7, queue.Next!.Number);
        Assert.Equal(PrReviewQueue.InReview, queue.Next.Reason);
        Assert.Equal(PrReviewQueue.NeverReviewed, queue.Queue[1].Reason);
    }

    [Fact]
    public async Task Cannot_see_github_is_a_502_not_an_empty_queue()
    {
        _github.Setup(g => g.ListOpenAsync(Repo, Project, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubCliException("Unable to list open pull requests for octo/widgets: offline"));

        await AssertError(502, "Unable to list open pull requests for octo/widgets: offline",
            () => _service.QueueAsync(Project, _ct));
    }

    [Fact]
    public async Task Sync_records_lifecycles_and_reports_counts()
    {
        await _service.UpsertAsync(Project, NewPr with { Number = 7 }, _ct);
        await _service.UpsertAsync(Project, NewPr with { Number = 8 }, _ct);
        _github.Setup(g => g.GetStatesAsync(Repo, Project, It.Is<IReadOnlyList<int>>(n => n.SequenceEqual(new[] { 7, 8 })),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new PrGitHubStateUpdate(7, PrGitHubState.Merged, new DateTime(2026, 8, 25, 0, 0, 0, DateTimeKind.Utc)),
                new PrGitHubStateUpdate(8, PrGitHubState.Open, null),
            ]);

        var sync = await _service.SyncAsync(Project, _ct);

        Assert.Equal(new PrReviewSyncResponse(2, 2, 2, 1, 1, 0), sync);
        Assert.Equal(PrGitHubState.Merged, (await _service.GetAsync(Project, 7, _ct)).PullRequest.GitHubState);
    }

    private static async Task AssertError(int status, string message, Func<Task> action)
    {
        var error = await Assert.ThrowsAsync<PrReviewException>(action);
        Assert.Equal(status, error.StatusCode);
        Assert.Equal(message, error.Message);
    }
}
