using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services.PrReviews;
using Xunit;

namespace Tests.Services.PrReviews;

public sealed class PrReviewQueueTests
{
    private static readonly GitHubRepository Repo = new("octo", "widgets");
    private static readonly DateTime Base = new(2026, 7, 1, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Ranks_resume_then_never_reviewed_then_new_commits_and_oldest_first_within_a_rank()
    {
        var open = new[]
        {
            Open(1, daysOld: 1, head: "moved1"),
            Open(2, daysOld: 9),
            Open(3, daysOld: 2),
            Open(4, daysOld: 5),
            Open(5, daysOld: 3, head: "moved5"),
        };
        var tracked = new[]
        {
            Tracked(1, PrTrackerStatus.Complete, completed: 1, reviewedHead: "old1"),
            Tracked(4, PrTrackerStatus.InReview, completed: 0),
            Tracked(5, PrTrackerStatus.Blocker, completed: 2, reviewedHead: "old5"),
        };

        var queue = PrReviewQueue.Build(Repo, open, tracked);

        Assert.Equal([4, 2, 3, 5, 1], queue.Queue.Select(entry => entry.Number));
        Assert.Equal([PrReviewQueue.InReview, PrReviewQueue.NeverReviewed, PrReviewQueue.NeverReviewed,
            PrReviewQueue.NewCommits, PrReviewQueue.NewCommits], queue.Queue.Select(entry => entry.Reason));
        Assert.Equal([1, 2, 3, 4, 5], queue.Queue.Select(entry => entry.Position!.Value));
        Assert.Equal(4, queue.Next!.Number);
        Assert.Equal(new PrReviewQueueCounts(5, 5, 0, 0), queue.Counts);
        Assert.Equal("octo/widgets", queue.Repository);
    }

    [Fact]
    public void Skips_drafts_and_prs_reviewed_at_their_current_head()
    {
        var open = new[]
        {
            Open(1, daysOld: 3, draft: true),
            Open(2, daysOld: 2, head: "same"),
            Open(3, daysOld: 1),
        };
        var tracked = new[]
        {
            Tracked(1, PrTrackerStatus.InReview, completed: 0),
            Tracked(2, PrTrackerStatus.Complete, completed: 1, reviewedHead: "same"),
            Tracked(3, PrTrackerStatus.Complete, completed: 1, reviewedHead: null),
        };

        var queue = PrReviewQueue.Build(Repo, open, tracked);

        Assert.Null(queue.Next);
        Assert.Empty(queue.Queue);
        Assert.Equal([PrReviewQueue.SkipDraft, PrReviewQueue.SkipReviewed, PrReviewQueue.SkipReviewedShaUnknown],
            queue.Skipped.Select(entry => entry.SkippedBecause));
        Assert.All(queue.Skipped, entry => Assert.Null(entry.Reason));
        Assert.Equal(new PrReviewQueueCounts(3, 0, 1, 2), queue.Counts);
    }

    [Fact]
    public void A_standing_blocker_waits_for_the_author_to_push()
    {
        var blocked = Tracked(1, PrTrackerStatus.Blocker, completed: 1, reviewedHead: "abc1234");

        Assert.Equal(PrReviewQueue.SkipReviewed,
            PrReviewQueue.Build(Repo, [Open(1, daysOld: 1, head: "abc1234")], [blocked]).Skipped.Single().SkippedBecause);
        Assert.Equal(PrReviewQueue.NewCommits,
            PrReviewQueue.Build(Repo, [Open(1, daysOld: 1, head: "def5678")], [blocked]).Next!.Reason);
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef01234567", "0123456789abcdef0123456789abcdef01234567", true)]
    [InlineData("0123456", "0123456789abcdef0123456789abcdef01234567", true)]
    [InlineData("0123456789ABCDEF", "0123456789abcdef0123456789abcdef01234567", true)]
    [InlineData("012345", "0123456789abcdef0123456789abcdef01234567", false)]
    [InlineData("1123456", "0123456789abcdef0123456789abcdef01234567", false)]
    public void An_abbreviated_reviewed_sha_of_seven_or_more_characters_matches_its_head(string reviewed, string head, bool same)
    {
        Assert.Equal(same, PrReviewQueue.SameCommit(reviewed, head));
    }

    [Fact]
    public void Entries_carry_what_the_tracker_knows_and_why()
    {
        var reviewedAt = Base.AddDays(-1);
        var tracked = Tracked(1, PrTrackerStatus.Complete, completed: 2, reviewedHead: "old") with
        {
            ReviewCount = 3, LastReviewedUtc = reviewedAt,
        };

        var entry = PrReviewQueue.Build(Repo, [Open(1, daysOld: 4, head: "new")], [tracked]).Next!;

        Assert.Equal(PrTrackerStatus.Complete, entry.TrackerStatus);
        Assert.Equal(3, entry.ReviewCount);
        Assert.Equal(2, entry.CompletedCount);
        Assert.Equal("old", entry.ReviewedHeadSha);
        Assert.Equal(reviewedAt, entry.LastReviewedUtc);
        Assert.Equal(2, entry.Rank);
        Assert.Contains("head SHA moved", entry.Why);
        Assert.Equal("https://github.com/octo/widgets/pull/1", entry.Url);
    }

    private static GitHubPullRequest Open(int number, int daysOld, string head = "head", bool draft = false) =>
        new(number, $"PR {number}", "octocat", Base.AddDays(-daysOld), null, draft, head, Repo.PullRequestUrl(number));

    private static PrReviewPullRequest Tracked(int number, string status, int completed, string? reviewedHead = null) =>
        new(Repo.FullName, number, $"PR {number}", "octocat", Base, "", status, PrGitHubState.Open, null, null,
            completed, completed, reviewedHead, null, Base, Base);
}
