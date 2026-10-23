using VibeRails.DB;
using VibeRails.DTOs;

namespace VibeRails.Services.PrReviews;

/// <summary>
/// Which open PR an unattended review run should take next, and why. Selection is arithmetic,
/// never a judgement call: reason rank first, then the oldest PR. A run that decides for itself
/// what "unreviewed" means decides differently on different days; this decides once, here.
/// </summary>
public static class PrReviewQueue
{
    public const string InReview = "in_review";
    public const string NeverReviewed = "never_reviewed";
    public const string NewCommits = "new_commits";

    public const string SkipDraft = "draft";
    public const string SkipReviewed = "reviewed";
    public const string SkipReviewedShaUnknown = "reviewed_sha_unknown";

    private static readonly IReadOnlyDictionary<string, (int Rank, string Why)> Reasons = new Dictionary<string, (int, string)>
    {
        [InReview] = (0, "A pass is already open on this PR. Finish it before starting a new one."),
        [NeverReviewed] = (1, "No completed review pass has been recorded for this PR."),
        [NewCommits] = (2, "The head SHA moved since the newest completed review, so that review is stale."),
    };

    private static readonly IReadOnlyDictionary<string, string> Skips = new Dictionary<string, string>
    {
        [SkipDraft] = "Draft. Out of scope unless asked for by number.",
        [SkipReviewed] = "Reviewed at the current head SHA.",
        [SkipReviewedShaUnknown] = "Reviewed, but the pass recorded no head SHA to compare.",
    };

    public static PrReviewQueueResponse Build(GitHubRepository repository, IReadOnlyList<GitHubPullRequest> open,
        IReadOnlyList<PrReviewPullRequest> tracked)
    {
        var byNumber = tracked.Where(pr => string.Equals(pr.Repository, repository.FullName, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(pr => pr.Number);
        var queued = new List<PrReviewQueueEntry>();
        var skipped = new List<PrReviewQueueEntry>();

        foreach (var pr in open)
        {
            byNumber.TryGetValue(pr.Number, out var record);
            var (reason, skip) = Classify(pr, record);
            var entry = new PrReviewQueueEntry(pr.Number, pr.Title, pr.Author, pr.OpenedUtc, pr.UpdatedUtc, pr.IsDraft,
                pr.HeadSha, pr.Url, record?.Status ?? PrTrackerStatus.NotStarted, record?.ReviewCount ?? 0,
                record?.CompletedCount ?? 0, record?.ReviewedHeadSha, record?.LastReviewedUtc,
                Reason: reason, Rank: reason is null ? null : Reasons[reason].Rank, Position: null,
                SkippedBecause: skip, Why: reason is null ? Skips[skip!] : Reasons[reason].Why)
                { Repository = repository.FullName };
            (reason is null ? skipped : queued).Add(entry);
        }

        // Rank, then oldest. The PR number only breaks a tie between identical timestamps.
        var ordered = queued
            .OrderBy(entry => entry.Rank)
            .ThenBy(entry => entry.OpenedUtc)
            .ThenBy(entry => entry.Number)
            .Select((entry, index) => entry with { Position = index + 1 })
            .ToList();
        var drafts = skipped.Count(entry => entry.SkippedBecause == SkipDraft);

        return new PrReviewQueueResponse(repository.FullName, ordered.FirstOrDefault(), ordered, skipped,
            new PrReviewQueueCounts(open.Count, ordered.Count, drafts, skipped.Count - drafts))
            { Repositories = [repository.FullName] };
    }

    /// <summary>Combines repository queues using the same reason/age ranking across all repositories.</summary>
    public static PrReviewQueueResponse Combine(IReadOnlyList<PrReviewQueueResponse> queues)
    {
        var ordered = queues.SelectMany(q => q.Queue).OrderBy(e => e.Rank).ThenBy(e => e.OpenedUtc)
            .ThenBy(e => e.Repository, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Number)
            .Select((entry, index) => entry with { Position = index + 1 }).ToList();
        return new(queues.Count == 1 ? queues[0].Repository : null, ordered.FirstOrDefault(), ordered,
            queues.SelectMany(q => q.Skipped).ToList(),
            new(queues.Sum(q => q.Counts.Open), ordered.Count, queues.Sum(q => q.Counts.Drafts), queues.Sum(q => q.Counts.UpToDate)))
            { Repositories = queues.SelectMany(q => q.Repositories).ToList() };
    }

    /// <summary>
    /// Exactly one of reason (eligible) or skip comes back, so a PR is never both queued and
    /// skipped. A review recorded without a head SHA counts as up to date: the opposite reading
    /// ("cannot prove it is current, so review it again") would make it eligible forever. A
    /// standing blocker whose author has not pushed is reviewed, not eligible: the review still
    /// stands, and it returns as new commits the moment the author pushes a fix.
    /// </summary>
    private static (string? Reason, string? Skip) Classify(GitHubPullRequest pr, PrReviewPullRequest? record)
    {
        if (pr.IsDraft) return (null, SkipDraft);
        if (record?.Status == PrTrackerStatus.InReview) return (InReview, null);
        if (record is null || record.CompletedCount == 0) return (NeverReviewed, null);
        if (string.IsNullOrEmpty(record.ReviewedHeadSha)) return (null, SkipReviewedShaUnknown);
        if (!SameCommit(record.ReviewedHeadSha, pr.HeadSha)) return (NewCommits, null);
        return (null, SkipReviewed);
    }

    /// <summary>
    /// A review may record an abbreviated SHA. Comparing it exactly against GitHub's full head
    /// would report new commits forever, so an abbreviation of at least seven characters matches.
    /// </summary>
    internal static bool SameCommit(string reviewed, string head)
    {
        if (string.Equals(reviewed, head, StringComparison.OrdinalIgnoreCase)) return true;
        var (shorter, longer) = reviewed.Length <= head.Length ? (reviewed, head) : (head, reviewed);
        return shorter.Length >= 7 && longer.StartsWith(shorter, StringComparison.OrdinalIgnoreCase);
    }
}
