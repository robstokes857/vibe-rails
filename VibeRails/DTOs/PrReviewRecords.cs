using System.Text.Json;
using VibeRails.DB;

namespace VibeRails.DTOs
{
    // ---------------------------------------------------------------- PR reviews (Services/PrReviews)
    // One request shape for every PR review write; each endpoint reads the fields it needs and
    // ignores the rest. Omitted and null mean "not supplied". The four timestamps are server-owned:
    // they are JsonElement only so a request that carries one at all can be refused by name.
    public record PrReviewRequest(
        int? Number = null,
        string? Title = null,
        string? Author = null,
        string? OpenedAt = null,
        string? Summary = null,
        string? HeadSha = null,
        string? Feedback = null,
        string? Verdict = null,
        bool? Blocking = null,
        JsonElement CreatedAt = default,
        JsonElement UpdatedAt = default,
        JsonElement StartedAt = default,
        JsonElement ReviewedAt = default);

    /// <summary>
    /// Every tracked PR in the current project's GitHub repository. Repository is null when the
    /// project has none (no git checkout, or an origin that is not github.com) and Problem says why.
    /// </summary>
    public record PrReviewListResponse(
        string? Repository,
        string? RepositoryUrl,
        string? Problem,
        List<PrReviewPullRequest> PullRequests);

    public record PrReviewDetailResponse(
        string Repository,
        string Url,
        PrReviewPullRequest PullRequest,
        List<PrReview> Reviews);

    /// <summary>A write's result. Created is true when a PR (upsert) or a review (start) was new.</summary>
    public record PrReviewWriteResponse(PrReviewPullRequest PullRequest, PrReview? Review, bool Created);

    /// <summary>What a delete removed: a whole PR (ReviewId null) or one review on that PR.</summary>
    public record PrReviewDeleteResponse(int Number, long? ReviewId);

    public record PrReviewSyncResponse(int Tracked, int Recorded, int Changed, int Open, int Merged, int Closed);

    /// <summary>One open pull request as the GitHub CLI reported it.</summary>
    public record GitHubPullRequest(
        int Number,
        string Title,
        string Author,
        DateTime OpenedUtc,
        DateTime? UpdatedUtc,
        bool IsDraft,
        string HeadSha,
        string Url);

    /// <summary>
    /// An open PR merged with what the tracker knows about it. Exactly one of Reason (queued) or
    /// SkippedBecause (not eligible) is set; Why is the long-form explanation of either.
    /// </summary>
    public record PrReviewQueueEntry(
        int Number,
        string Title,
        string Author,
        DateTime OpenedUtc,
        DateTime? UpdatedUtc,
        bool IsDraft,
        string HeadSha,
        string Url,
        string TrackerStatus,
        int ReviewCount,
        int CompletedCount,
        string? ReviewedHeadSha,
        DateTime? LastReviewedUtc,
        string? Reason,
        int? Rank,
        int? Position,
        string? SkippedBecause,
        string Why);

    public record PrReviewQueueCounts(int Open, int Eligible, int Drafts, int UpToDate);

    /// <summary>Which open PR to review next. Next is null when nothing is eligible.</summary>
    public record PrReviewQueueResponse(
        string Repository,
        PrReviewQueueEntry? Next,
        List<PrReviewQueueEntry> Queue,
        List<PrReviewQueueEntry> Skipped,
        PrReviewQueueCounts Counts);
}
