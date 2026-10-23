using Microsoft.Data.Sqlite;
using Moq;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services.PrReviews;
using Xunit;

namespace Tests.Services.PrReviews;

public sealed class PrReviewPublishingTests : IDisposable
{
    private const string Project = "C:/fixture";
    private const string Sha = "0123456789012345678901234567890123456789";
    private static readonly GitHubRepository Repo = new("octo", "widgets");
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"pr-delivery-{Guid.NewGuid():N}.db");
    private readonly Mock<IGitHubPullRequestClient> _github = new(MockBehavior.Strict);
    private readonly PrReviewStore _store;
    private readonly PrReviewService _service;
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private string Connection => $"Data Source={_path};Pooling=False";

    public PrReviewPublishingTests()
    {
        _store = new(Connection);
        var resolver = new Mock<IPrReviewRepositoryResolver>();
        resolver.Setup(r => r.ResolveAsync(Project, It.IsAny<CancellationToken>())).ReturnsAsync(new PrReviewProject(Project, Repo, null));
        _service = new(_store, _github.Object, resolver.Object);
        _github.Setup(g => g.GetViewerAsync(Project, It.IsAny<CancellationToken>())).ReturnsAsync("reviewer");
        _github.Setup(g => g.GetAsync(Repo, Project, 7, It.IsAny<CancellationToken>())).ReturnsAsync(
            new GitHubPullRequest(7, "Widgets", "author", DateTime.UtcNow, null, false, Sha, Repo.PullRequestUrl(7)));
        _github.Setup(g => g.PublishReviewAsync(Repo, Project, It.IsAny<PrReviewDelivery>(), It.IsAny<CancellationToken>())).ReturnsAsync(42);
    }

    private async Task Start()
    {
        await _service.SetSettingsAsync(true, _ct);
        await _service.StartAsync(Project, 7, new(Title: "Widgets", Author: "author", OpenedAt: "2026-08-01", HeadSha: Sha), _ct);
    }

    private Task<PrReviewWriteResponse> Complete(string verdict = "comments", bool? blocking = null) =>
        _service.CompleteAsync(Project, 7, new(Feedback: "Finding: fix this", Verdict: verdict, Blocking: blocking), _ct);

    [Fact]
    public async Task Setting_defaults_off_and_persists_across_instances()
    {
        Assert.False((await _service.GetSettingsAsync(_ct)).Enabled);
        Assert.Equal(409, (await Assert.ThrowsAsync<PrReviewException>(() => _service.QueueAsync(Project, _ct))).StatusCode);
        Assert.Equal(409, (await Assert.ThrowsAsync<PrReviewException>(() => _service.StartAsync(Project, 7, null, _ct))).StatusCode);
        _github.VerifyNoOtherCalls();
        await _service.SetSettingsAsync(true, _ct);
        Assert.True((await new PrReviewStore(Connection).GetReviewerSettingsAsync(_ct)).Enabled);
        await new PrReviewStore(Connection).SetReviewerSettingsAsync(false, _ct);
        Assert.False((await _service.GetSettingsAsync(_ct)).Enabled);
    }

    [Fact]
    public async Task Disabling_allows_inflight_local_completion_without_posting_or_erasing_history()
    {
        await Start();
        await _service.SetSettingsAsync(false, _ct);
        var result = await Complete("request_changes");
        Assert.Equal("completed", result.Review!.Status);
        Assert.Equal("disabled", result.Delivery!.Status);
        Assert.True(result.Review.Blocking);
        Assert.Single((await _service.GetAsync(Project, 7, _ct)).Reviews);
        _github.VerifyNoOtherCalls();
        await Assert.ThrowsAsync<PrReviewException>(() => _service.PublishAsync(Project, 7, result.Review.Id, _ct));
        await _service.SetSettingsAsync(true, _ct);
        _github.VerifyNoOtherCalls(); // Enabling never backfills automatically.
        Assert.Equal("published", (await _service.PublishAsync(Project, 7, result.Review.Id, _ct)).Status);
    }

    [Fact]
    public async Task Disable_between_completion_commit_and_publish_gate_returns_the_saved_local_result()
    {
        await Start();
        var resolutions = 0;
        var resolver = new Mock<IPrReviewRepositoryResolver>();
        resolver.Setup(r => r.ResolveAsync(Project, It.IsAny<CancellationToken>())).Returns(async () =>
        {
            // Complete resolves once before committing; Publish resolves again after the enabled read.
            if (Interlocked.Increment(ref resolutions) == 2)
                await _store.SetReviewerSettingsAsync(false, _ct);
            return new PrReviewProject(Project, Repo, null);
        });
        var service = new PrReviewService(_store, _github.Object, resolver.Object);
        var result = await service.CompleteAsync(Project, 7, new(Feedback: "Saved findings", Verdict: "request_changes"), _ct);
        Assert.Equal("completed", result.Review!.Status);
        Assert.Equal("Saved findings", result.Review.Feedback);
        Assert.Equal("disabled", result.Delivery!.Status);
        Assert.Null(result.Delivery.GitHubId);
        Assert.Single((await service.GetAsync(Project, 7, _ct)).Reviews);
        _github.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("request_changes", null, "REQUEST_CHANGES")]
    [InlineData("request_changes", false, "REQUEST_CHANGES")]
    [InlineData("comments", true, "REQUEST_CHANGES")]
    [InlineData("recommend_approve", false, "COMMENT")]
    [InlineData("comments", false, "COMMENT")]
    public async Task Completion_pins_repository_pr_commit_identity_and_real_review_event(string verdict, bool? blocking, string expected)
    {
        await Start();
        var result = await Complete(verdict, blocking);
        var delivery = result.Delivery!;
        Assert.Equal("published", delivery.Status);
        Assert.Equal(42, delivery.GitHubId);
        Assert.Equal(expected, delivery.Event);
        Assert.Equal("reviewer", delivery.Login);
        Assert.Equal(Sha, delivery.HeadSha);
        _github.Verify(g => g.PublishReviewAsync(Repo, Project,
            It.Is<PrReviewDelivery>(d => d.Repository == "octo/widgets" && d.PrNumber == 7 && d.HeadSha == Sha
                && d.Event == expected && d.Login == "reviewer" && d.Body.StartsWith("Finding: fix this") && d.Body.Contains(d.Marker)),
            It.IsAny<CancellationToken>()), Times.Once);
        await _service.PublishAsync(Project, 7, result.Review!.Id, _ct);
        _github.Verify(g => g.PublishReviewAsync(Repo, Project, It.IsAny<PrReviewDelivery>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Single((await _service.GetAsync(Project, 7, _ct)).Deliveries);
    }

    [Fact]
    public async Task Changed_head_is_failed_delivery_and_never_substitutes_the_new_head()
    {
        await Start();
        _github.Setup(g => g.GetAsync(Repo, Project, 7, It.IsAny<CancellationToken>())).ReturnsAsync(
            new GitHubPullRequest(7, "Widgets", "author", DateTime.UtcNow, null, false, new string('a', 40), Repo.PullRequestUrl(7)));
        var result = await Complete();
        Assert.Equal("failed", result.Delivery!.Status);
        Assert.Contains("head changed", result.Delivery.Error);
        Assert.Equal("completed", result.Review!.Status);
        _github.Verify(g => g.PublishReviewAsync(It.IsAny<GitHubRepository>(), It.IsAny<string>(), It.IsAny<PrReviewDelivery>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Authentication_failure_preserves_local_result_and_can_retry_safely()
    {
        await Start();
        _github.SetupSequence(g => g.GetViewerAsync(Project, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubCliException("Sign in with gh auth login."))
            .ReturnsAsync("reviewer");
        var result = await Complete();
        Assert.Equal("failed", result.Delivery!.Status);
        Assert.Contains("gh auth login", result.Delivery.Error);
        Assert.Equal("published", (await _service.PublishAsync(Project, 7, result.Review!.Id, _ct)).Status);
        _github.Verify(g => g.PublishReviewAsync(Repo, Project, It.IsAny<PrReviewDelivery>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Confirmed_permission_refusal_can_retry_but_transport_uncertainty_only_reconciles()
    {
        await Start();
        _github.SetupSequence(g => g.PublishReviewAsync(Repo, Project, It.IsAny<PrReviewDelivery>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubCliException("GitHub refused permission."))
            .ThrowsAsync(new GitHubCliException("Timed out after sending.", true));
        var result = await Complete();
        Assert.Equal("failed", result.Delivery!.Status);
        Assert.Equal("unknown", (await _service.PublishAsync(Project, 7, result.Review!.Id, _ct)).Status);
        _github.Setup(g => g.FindReviewAsync(Repo, Project, It.IsAny<PrReviewDelivery>(), It.IsAny<CancellationToken>())).ReturnsAsync((long?)null);
        Assert.Equal("unknown", (await _service.PublishAsync(Project, 7, result.Review.Id, _ct)).Status);
        await _service.SetSettingsAsync(false, _ct);
        await _service.SetSettingsAsync(true, _ct);
        Assert.Equal("unknown", (await _service.PublishAsync(Project, 7, result.Review.Id, _ct)).Status);
        _github.Setup(g => g.FindReviewAsync(Repo, Project, It.IsAny<PrReviewDelivery>(), It.IsAny<CancellationToken>())).ReturnsAsync(42);
        Assert.Equal("published", (await _service.PublishAsync(Project, 7, result.Review.Id, _ct)).Status);
        _github.Verify(g => g.PublishReviewAsync(Repo, Project, It.IsAny<PrReviewDelivery>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Concurrent_roots_reserve_only_one_post_and_check_the_latest_disable()
    {
        await Start();
        await _service.SetSettingsAsync(false, _ct);
        var result = await Complete();
        await _service.SetSettingsAsync(true, _ct);
        var claims = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() =>
            new PrReviewStore(Connection).ClaimDeliveryAsync(Repo.FullName, result.Review!.Id, "reviewer", _ct), _ct)));
        Assert.Single(claims, claim => claim);
        await _service.SetSettingsAsync(false, _ct);
        Assert.False(await new PrReviewStore(Connection).ClaimDeliveryAsync(Repo.FullName, result.Review!.Id, "reviewer", _ct));
    }

    [Fact]
    public async Task Disable_during_preflight_prevents_dispatch_and_wrong_pr_cannot_retry()
    {
        await Start();
        _github.Setup(g => g.GetViewerAsync(Project, It.IsAny<CancellationToken>())).Returns(async () =>
        {
            await _service.SetSettingsAsync(false, _ct);
            return "reviewer";
        });
        var result = await Complete();
        Assert.Equal("pending", result.Delivery!.Status);
        _github.Verify(g => g.PublishReviewAsync(Repo, Project, It.IsAny<PrReviewDelivery>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(404, (await Assert.ThrowsAsync<PrReviewException>(() => _service.PublishAsync(Project, 8, result.Review!.Id, _ct))).StatusCode);
    }

    [Fact]
    public async Task Store_completion_commits_delivery_with_the_result_and_keeps_snapshot_after_edits()
    {
        await Start();
        var result = await _store.CompleteReviewAsync(Repo.FullName, 7, "comments", false, "original", null, null, _ct);
        var d = Assert.Single(await new PrReviewStore(Connection).GetDeliveriesAsync(Repo.FullName, 7, _ct));
        await _store.UpdateReviewAsync(Repo.FullName, result.Review!.Id, new(Feedback: "local correction"), _ct);
        Assert.Equal(d, await _store.EnsureDeliveryAsync(Repo.FullName, result.Review.Id, _ct));
        Assert.StartsWith("original", d.Body);
        Assert.Empty(await _store.GetDeliveriesAsync("another/repository", 7, _ct));
    }

    [Fact]
    public async Task Legacy_adoption_cannot_reuse_an_archived_delivery_id_after_local_deletion()
    {
        await Start();
        var result = await Complete();
        await _store.DeleteAsync(Repo.FullName, 7, _ct);
        var legacyPath = _path + ".legacy";
        try
        {
            var legacy = new PrReviewStore($"Data Source={legacyPath};Pooling=False");
            var source = await legacy.StartReviewAsync(Repo.FullName, 8, new("Legacy", "author", DateTime.UtcNow), Sha, _ct);
            Assert.Equal(result.Review!.Id, source.Review!.Id); // Both files began at id 1.
            var adopting = new PrReviewStore(Connection, legacyConnectionString: $"Data Source={legacyPath};Pooling=False");
            var adopted = Assert.Single(await adopting.GetReviewsAsync(Repo.FullName, 8, _ct));
            Assert.NotEqual(result.Review.Id, adopted.Id);
            var done = await adopting.CompleteReviewAsync(Repo.FullName, 8, "comments", false, "new findings", null, null, _ct);
            var delivery = Assert.Single(await adopting.GetDeliveriesAsync(Repo.FullName, 8, _ct));
            Assert.Equal(done.Review!.Id, delivery.ReviewId);
            Assert.NotEqual(result.Delivery!.Marker, delivery.Marker);
            Assert.Single(await adopting.GetDeliveriesAsync(Repo.FullName, 7, _ct));
        }
        finally { File.Delete(legacyPath); }
    }

    public void Dispose() { try { File.Delete(_path); } catch (IOException) { } }
}
