namespace VibeRails.DB;

/// <summary>Wire values of <see cref="PrReview.Status"/>. A PR has at most one in-progress review.</summary>
public static class PrReviewStatus
{
    public const string InProgress = "in_progress";
    public const string Completed = "completed";
}

/// <summary>
/// The tracker state of a pull request. It is derived from the PR's reviews on every read and
/// never stored, so it cannot drift from them.
/// </summary>
public static class PrTrackerStatus
{
    public const string NotStarted = "not_started";
    public const string InReview = "in_review";
    public const string Blocker = "blocker";
    public const string Complete = "complete";

    /// <summary>
    /// Only the newest completed review decides, so a later clean pass supersedes an earlier
    /// blocker. In review outranks Blocker on purpose: an interrupted run stays visible in the
    /// recovery queue, and the blocker returns as soon as that pass completes.
    /// </summary>
    public static string Derive(bool hasPendingReview, int completedCount, bool newestCompletedIsBlocking) =>
        hasPendingReview ? InReview
        : completedCount == 0 ? NotStarted
        : newestCompletedIsBlocking ? Blocker
        : Complete;
}

/// <summary>
/// What GitHub says the pull request is, orthogonal to the review-derived state. Unknown means
/// "never checked" and is treated as live everywhere: guessing open would be the same guess with
/// a confident label on it.
/// </summary>
public static class PrGitHubState
{
    public const string Unknown = "unknown";
    public const string Open = "open";
    public const string Merged = "merged";
    public const string Closed = "closed";

    public static bool IsRecordable(string? state) => state is Open or Merged or Closed;

    /// <summary>Merged and closed are the only states that retire a PR from the working lists.</summary>
    public static bool IsLive(string? state) => state is not (Merged or Closed);
}

/// <summary>Review verdicts. <see cref="RequestChanges"/> is the blocking verdict by default.</summary>
public static class PrReviewVerdict
{
    public const string RecommendApprove = "recommend_approve";
    public const string Comments = "comments";
    public const string RequestChanges = "request_changes";

    public static bool IsValid(string? verdict) => verdict is RecommendApprove or Comments or RequestChanges;
}

/// <summary>A tracked pull request plus the review facts derived from its history.</summary>
/// <param name="Repository">GitHub <c>owner/name</c>; PR numbers are unique within it.</param>
/// <param name="OpenedUtc">GitHub's own <c>createdAt</c>, not when the tracker first saw the PR.</param>
/// <param name="Status">One of <see cref="PrTrackerStatus"/>, derived on read.</param>
/// <param name="GitHubState">One of <see cref="PrGitHubState"/>; <c>unknown</c> until a sync records it.</param>
/// <param name="GitHubStateUtc">When the PR stopped being open (merged or closed), when known.</param>
/// <param name="ReviewedHeadSha">The head SHA the newest completed review recorded, or null.</param>
/// <param name="UpdatedUtc">The last review write. A GitHub sync never moves it.</param>
public sealed record PrReviewPullRequest(
    string Repository,
    int Number,
    string Title,
    string Author,
    DateTime OpenedUtc,
    string Summary,
    string Status,
    string GitHubState,
    DateTime? GitHubStateUtc,
    DateTime? GitHubCheckedUtc,
    int ReviewCount,
    int CompletedCount,
    string? ReviewedHeadSha,
    DateTime? LastReviewedUtc,
    DateTime CreatedUtc,
    DateTime UpdatedUtc);

/// <summary>One review pass. Completed reviews are history; nothing rewrites them implicitly.</summary>
public sealed record PrReview(
    long Id,
    string Repository,
    int PrNumber,
    string HeadSha,
    string Status,
    string? Verdict,
    bool Blocking,
    string Feedback,
    DateTime StartedUtc,
    DateTime? ReviewedUtc,
    DateTime UpdatedUtc);

/// <summary>
/// Pull request metadata to write. Null leaves a tracked value alone; an untracked PR needs
/// <see cref="Title"/>, <see cref="Author"/> and <see cref="OpenedUtc"/>.
/// </summary>
public sealed record PrReviewPullRequestFields(
    string? Title = null,
    string? Author = null,
    DateTime? OpenedUtc = null,
    string? Summary = null);

/// <summary>Review fields to write. Null leaves the stored value alone.</summary>
public sealed record PrReviewFields(
    string? HeadSha = null,
    string? Verdict = null,
    bool? Blocking = null,
    string? Feedback = null);

/// <summary>One PR's lifecycle as GitHub reported it.</summary>
public sealed record PrGitHubStateUpdate(int Number, string State, DateTime? StateUtc);

/// <summary>How many tracked PRs a sync recorded, and how many of those changed state.</summary>
public sealed record PrGitHubSyncCounts(int Recorded, int Changed);

/// <summary>
/// Why a write did or did not happen. Untracked and tracked-without-a-pending-review are
/// different mistakes with different fixes, so they are reported separately.
/// </summary>
public enum PrReviewWriteOutcome
{
    Saved,
    Created,
    NotTracked,
    NoPendingReview,
    MissingField,
}

/// <summary>The outcome of a write plus the PR and review as they stand afterwards.</summary>
/// <param name="MissingField">The wire name of the first required field an untracked PR lacked.</param>
public sealed record PrReviewWriteResult(
    PrReviewWriteOutcome Outcome,
    PrReviewPullRequest? PullRequest = null,
    PrReview? Review = null,
    string? MissingField = null);

/// <summary>A preserved side of an adoption conflict, including drafts and completed passes.</summary>
public sealed record PrReviewHistory(PrReviewPullRequest? PullRequest, List<PrReview> Reviews);

/// <summary>Two divergent histories and optimistic tokens for an explicit reconciliation.</summary>
public sealed record PrReviewLegacyConflict(int Number, string SourceHash, string TargetHash,
    PrReviewHistory Legacy, PrReviewHistory Current);

/// <summary>
/// Lead-review records for GitHub pull requests, keyed by repository and PR number. Every write
/// that changes a review stamps the PR's last-write time; recording GitHub's lifecycle does not.
/// </summary>
public interface IPrReviewStore
{
    /// <summary>Machine-wide reviewer and outbound publishing switch. Defaults off.</summary>
    Task<PrReviewerSettings> GetReviewerSettingsAsync(CancellationToken ct = default);
    /// <summary>Persists the switch without deleting history or deliveries.</summary>
    Task<PrReviewerSettings> SetReviewerSettingsAsync(bool enabled, CancellationToken ct = default);

    /// <summary>Saves the switch and optional watch list atomically; null preserves the saved list.</summary>
    Task<PrReviewerSettings> SetReviewerSettingsAsync(bool enabled, IReadOnlyList<string>? repositories, CancellationToken ct = default);
    /// <summary>Creates one immutable delivery snapshot per completed pass; never backfills history.</summary>
    Task<PrReviewDelivery> EnsureDeliveryAsync(string repository, long reviewId, CancellationToken ct = default);
    /// <summary>Delivery receipts are distinct from local review state.</summary>
    Task<IReadOnlyList<PrReviewDelivery>> GetDeliveriesAsync(string repository, int number, CancellationToken ct = default);
    /// <summary>Atomically reserves a POST only while enabled. Unknown attempts are never resent.</summary>
    Task<bool> ClaimDeliveryAsync(string repository, long reviewId, string login, CancellationToken ct = default);
    /// <summary>Records a confirmed receipt, safe failure, or uncertain outcome.</summary>
    Task SetDeliveryResultAsync(string repository, long reviewId, string status, long? githubId, string? error,
        CancellationToken ct = default, string? expectedStatus = null);
    /// <summary>Inspectable conflicts; conflicts never block ordinary repository operations.</summary>
    Task<IReadOnlyList<PrReviewLegacyConflict>> GetLegacyConflictsAsync(string repository, CancellationToken ct = default);

    /// <summary>Chooses a history only if both displayed versions are still current. Both snapshots remain archived.</summary>
    Task<bool> ResolveLegacyConflictAsync(string repository, int number, string sourceHash, string targetHash,
        bool useLegacy, CancellationToken ct = default);
    /// <summary>Every tracked PR in the repository, most recent review write first.</summary>
    Task<IReadOnlyList<PrReviewPullRequest>> ListAsync(string repository, CancellationToken ct = default);

    Task<PrReviewPullRequest?> GetAsync(string repository, int number, CancellationToken ct = default);

    /// <summary>The pending review first, then completed reviews newest first.</summary>
    Task<IReadOnlyList<PrReview>> GetReviewsAsync(string repository, int number, CancellationToken ct = default);

    /// <summary>Creates or updates a PR's metadata. <c>Created</c> when the PR was untracked.</summary>
    Task<PrReviewWriteResult> UpsertAsync(string repository, int number, PrReviewPullRequestFields fields, CancellationToken ct = default);

    /// <summary>Updates a tracked PR's metadata; <c>NotTracked</c> when there is nothing to update.</summary>
    Task<PrReviewWriteResult> UpdateAsync(string repository, int number, PrReviewPullRequestFields fields, CancellationToken ct = default);

    /// <summary>Deletes a PR and its reviews. False when it was not tracked.</summary>
    Task<bool> DeleteAsync(string repository, int number, CancellationToken ct = default);

    /// <summary>
    /// Idempotent: upserts the PR, then returns its pending review or starts one. A supplied head
    /// SHA replaces a pending review's. <c>Created</c> means a new review was started.
    /// </summary>
    Task<PrReviewWriteResult> StartReviewAsync(string repository, int number, PrReviewPullRequestFields fields,
        string? headSha, CancellationToken ct = default);

    /// <summary>Checkpoints the supplied fields onto the pending review without finishing it.</summary>
    Task<PrReviewWriteResult> SaveDraftAsync(string repository, int number, PrReviewFields fields, string? summary,
        CancellationToken ct = default);

    /// <summary>
    /// Completes the pending review and stamps its reviewed time. The caller supplies the final
    /// verdict, blocking flag and feedback.
    /// </summary>
    Task<PrReviewWriteResult> CompleteReviewAsync(string repository, int number, string verdict, bool blocking,
        string feedback, string? headSha, string? summary, CancellationToken ct = default);

    /// <summary>Edits one review in this repository; null when it does not exist here.</summary>
    Task<PrReview?> UpdateReviewAsync(string repository, long id, PrReviewFields fields, CancellationToken ct = default);

    /// <summary>Deletes one review in this repository and returns its PR number; null when not found.</summary>
    Task<int?> DeleteReviewAsync(string repository, long id, CancellationToken ct = default);

    /// <summary>Every tracked PR number, ascending.</summary>
    Task<IReadOnlyList<int>> ListNumbersAsync(string repository, CancellationToken ct = default);

    /// <summary>Records GitHub's lifecycle for tracked PRs. Untracked numbers are ignored.</summary>
    Task<PrGitHubSyncCounts> RecordGitHubStatesAsync(string repository, IReadOnlyList<PrGitHubStateUpdate> states,
        CancellationToken ct = default);
}
