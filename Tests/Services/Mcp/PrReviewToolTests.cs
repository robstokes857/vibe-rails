using System.Text.Json;
using Microsoft.Data.Sqlite;
using Moq;
using VibeRails.DB;
using VibeRails.Services.Board;
using VibeRails.Services.Mcp.Tools;
using VibeRails.Services.PrReviews;
using Xunit;

namespace Tests.Services.Mcp;

public sealed class PrReviewToolTests : IDisposable
{
    private const string Project = "C:/work/widgets";
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"viberails-pr-review-tool-{Guid.NewGuid():N}.db");
    private readonly Mock<IGitHubPullRequestClient> _github = new();
    private readonly PrReviewTool _tool;
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public PrReviewToolTests()
    {
        var repositories = new Mock<IPrReviewRepositoryResolver>();
        repositories.Setup(r => r.ResolveAsync(Project, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PrReviewProject(Project, new GitHubRepository("octo", "widgets"), null));
        var projects = new Mock<IBoardProjectResolver>();
        projects.Setup(p => p.ResolveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Project);
        var store = new PrReviewStore($"Data Source={_dbPath};Mode=ReadWriteCreate");
        store.SetReviewerSettingsAsync(true).GetAwaiter().GetResult();
        var service = new PrReviewService(store,
            _github.Object, repositories.Object);
        _tool = new PrReviewTool(service, projects.Object);
    }

    public void Dispose()
    {
        SqliteConnection.ClearPool(new SqliteConnection($"Data Source={_dbPath};Mode=ReadWriteCreate"));
        try { File.Delete(_dbPath); } catch (IOException) { }
    }

    [Fact]
    public async Task An_agent_can_start_checkpoint_and_complete_a_review()
    {
        var started = await _tool.StartPrReview(7, "feat: widgets", "octocat", "2026-08-01T14:00:00Z", "abc123",
            cancellationToken: _ct);
        var draft = await _tool.SavePrReviewDraft(7, feedback: "first finding", blocking: true, cancellationToken: _ct);
        var completed = await _tool.CompletePrReview(7, "## Lead review", "recommend_approve", cancellationToken: _ct);
        var detail = await _tool.GetPrReview(7, _ct);

        Assert.True(Parse(started).GetProperty("created").GetBoolean());
        Assert.True(Parse(draft).GetProperty("review").GetProperty("blocking").GetBoolean());
        // The verdict's default replaces the draft's flag when the completion does not say.
        Assert.False(Parse(completed).GetProperty("review").GetProperty("blocking").GetBoolean());
        Assert.Equal("complete", Parse(detail).GetProperty("pullRequest").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Completion_and_retry_report_confirmed_github_delivery_separately()
    {
        var repo = new GitHubRepository("octo", "widgets");
        var sha = new string('a', 40);
        _github.Setup(g => g.GetViewerAsync(Project, It.IsAny<CancellationToken>())).ReturnsAsync("reviewer");
        _github.Setup(g => g.GetAsync(repo, Project, 7, It.IsAny<CancellationToken>())).ReturnsAsync(
            new VibeRails.DTOs.GitHubPullRequest(7, "Widgets", "author", DateTime.UtcNow, null, false, sha, repo.PullRequestUrl(7)));
        _github.Setup(g => g.PublishReviewAsync(repo, Project, It.IsAny<PrReviewDelivery>(), It.IsAny<CancellationToken>())).ReturnsAsync(42);
        var start = Parse(await _tool.StartPrReview(7, "Widgets", "author", "2026-08-01", sha, cancellationToken: _ct));
        var id = start.GetProperty("review").GetProperty("id").GetInt64();
        var completed = Parse(await _tool.CompletePrReview(7, "Blocker", "request_changes", cancellationToken: _ct));
        Assert.Equal("completed", completed.GetProperty("review").GetProperty("status").GetString());
        Assert.Equal("published", completed.GetProperty("delivery").GetProperty("status").GetString());
        Assert.Equal("REQUEST_CHANGES", completed.GetProperty("delivery").GetProperty("event").GetString());
        var retry = Parse(await _tool.RetryPrReviewPublish(7, id, _ct));
        Assert.Equal(42, retry.GetProperty("gitHubId").GetInt64());
        _github.Verify(g => g.PublishReviewAsync(repo, Project, It.IsAny<PrReviewDelivery>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.StartsWith("FAIL: No delivery exists", await _tool.RetryPrReviewPublish(8, id, _ct));
    }

    [Fact]
    public async Task Rule_violations_come_back_as_fail_sentences()
    {
        Assert.Equal("FAIL: title is required for a new PR", await _tool.StartPrReview(7, cancellationToken: _ct));
        Assert.Equal("FAIL: PR #7 is not tracked; start a review for it first",
            await _tool.CompletePrReview(7, "body", "comments", cancellationToken: _ct));
        Assert.Equal("FAIL: PR #7 was not found.", await _tool.GetPrReview(7, _ct));
    }

    [Fact]
    public async Task Adoption_conflicts_explain_that_both_histories_are_preserved()
    {
        _github.Setup(g => g.ListOpenAsync(It.IsAny<GitHubRepository>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PrReviewAdoptionConflictException(7));
        Assert.Equal("FAIL: " + new PrReviewAdoptionConflictException(7).Message, await _tool.GetPrReviewQueue(_ct));
    }

    [Fact]
    public async Task Unexpected_failures_do_not_leak_detail()
    {
        _github.Setup(g => g.ListOpenAsync(It.IsAny<GitHubRepository>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("C:/secret/path exploded"));

        var result = await _tool.GetPrReviewQueue(_ct);

        Assert.Equal("FAIL: could not read the PR review queue. See the VibeRails log for details.", result);
    }

    private static JsonElement Parse(string json)
    {
        Assert.False(json.StartsWith("FAIL:", StringComparison.Ordinal), json);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
