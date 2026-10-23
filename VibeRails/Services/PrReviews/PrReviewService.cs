using System.Globalization;
using System.Text.Json;
using VibeRails.DB;
using VibeRails.DTOs;

namespace VibeRails.Services.PrReviews;

/// <summary>A PR review rule violation, with the HTTP status that tells the caller what to fix.</summary>
public sealed class PrReviewException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>The current project and the GitHub repository its <c>origin</c> remote names, if any.</summary>
public sealed record PrReviewProject(string Path, GitHubRepository? Repository, string? Problem);

/// <summary>Maps a project checkout to its GitHub repository. The project path is never taken from a request.</summary>
public interface IPrReviewRepositoryResolver
{
    Task<PrReviewProject> ResolveAsync(string projectPath, CancellationToken ct = default);
}

/// <summary>Reads <c>git remote get-url origin</c> through <see cref="GitService"/>; argv only, bounded.</summary>
public sealed class PrReviewRepositoryResolver : IPrReviewRepositoryResolver
{
    public async Task<PrReviewProject> ResolveAsync(string projectPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
            return new PrReviewProject("", null, "No project is open.");
        var remote = await new GitService(projectPath).GetRemoteUrlAsync(ct);
        if (string.IsNullOrWhiteSpace(remote))
            return new PrReviewProject(projectPath, null, "This project has no git remote named origin, so there is no GitHub repository to review.");
        // The remote can embed a credential, so it is never echoed back.
        return GitHubRepository.TryParseRemote(remote, out var repository)
            ? new PrReviewProject(projectPath, repository, null)
            : new PrReviewProject(projectPath, null, "This project's origin remote is not a github.com repository.");
    }
}

/// <summary>
/// The lead-review tracker: which GitHub PRs were reviewed, what each pass found, and which PR
/// to review next. The tracker state is derived from reviews, timestamps are server-owned, and
/// delivery to GitHub is tracked separately from local results.
/// </summary>
public sealed class PrReviewService(IPrReviewStore store, IGitHubPullRequestClient github,
    IPrReviewRepositoryResolver repositories)
{
    internal const int MaxTitle = 500;
    internal const int MaxAuthor = 200;
    internal const int MaxOpenedAt = 100;
    internal const int MaxSummary = 10_000;
    internal const int MaxFeedback = 100_000;
    internal const int MaxHeadSha = 100;
    internal const int MaxVerdict = 40;

    private static readonly PrReviewRequest Empty = new();

    public async Task<PrReviewListResponse> ListAsync(string projectPath, CancellationToken ct = default)
    {
        var project = await repositories.ResolveAsync(projectPath, ct);
        if (project.Repository is not { } repository)
            return new PrReviewListResponse(null, null, project.Problem, []);
        var pullRequests = await store.ListAsync(repository.FullName, ct);
        return new PrReviewListResponse(repository.FullName, repository.Url, null, pullRequests.ToList())
        {
            LegacyConflicts = (await store.GetLegacyConflictsAsync(repository.FullName, ct)).ToList()
        };
    }

    /// <summary>Explicitly reconciles inspected histories; concurrent edits require a fresh inspection.</summary>
    public async Task ResolveLegacyConflictAsync(string projectPath, int number, PrReviewResolveLegacyRequest request,
        CancellationToken ct = default)
    {
        var repository = await RequireRepositoryNameAsync(projectPath, ct);
        RequireNumber(number);
        if (request.SourceHash?.Length != 64 || request.TargetHash?.Length != 64)
            throw new PrReviewException(400, "Both history version tokens are required.");
        if (!await store.ResolveLegacyConflictAsync(repository, number, request.SourceHash, request.TargetHash, request.UseLegacy, ct))
            throw new PrReviewException(409, "The histories changed. Refresh Reviews and inspect both copies again before choosing.");
    }

    public async Task<PrReviewDetailResponse> GetAsync(string projectPath, int number, CancellationToken ct = default)
    {
        var repository = (await RequireRepositoryAsync(projectPath, ct)).Repository!;
        RequireNumber(number);
        var pullRequest = await store.GetAsync(repository.FullName, number, ct)
            ?? throw new PrReviewException(404, $"PR #{number} was not found.");
        var reviews = await store.GetReviewsAsync(repository.FullName, number, ct);
        return new PrReviewDetailResponse(repository.FullName, repository.PullRequestUrl(number), pullRequest, reviews.ToList())
        {
            Deliveries = (await store.GetDeliveriesAsync(repository.FullName, number, ct)).ToList()
        };
    }

    /// <summary>
    /// Merges the live open-PR list with the tracker's records. Answers 502 when GitHub cannot be
    /// reached, so "nothing to review" and "cannot see GitHub" are never the same response.
    /// </summary>
    public async Task<PrReviewQueueResponse> QueueAsync(string projectPath, CancellationToken ct = default)
    {
        await RequireEnabledAsync(ct);
        var project = await RequireRepositoryAsync(projectPath, ct);
        var repository = project.Repository!;
        IReadOnlyList<GitHubPullRequest> open;
        try { open = await github.ListOpenAsync(repository, project.Path, ct); }
        catch (GitHubCliException ex) { throw new PrReviewException(502, ex.Message); }
        var tracked = await store.ListAsync(repository.FullName, ct);
        return PrReviewQueue.Build(repository, open, tracked);
    }

    /// <summary>Refreshes every tracked PR's GitHub lifecycle. Review records are never changed by it.</summary>
    public async Task<PrReviewSyncResponse> SyncAsync(string projectPath, CancellationToken ct = default)
    {
        var project = await RequireRepositoryAsync(projectPath, ct);
        var repository = project.Repository!;
        var numbers = await store.ListNumbersAsync(repository.FullName, ct);
        IReadOnlyList<PrGitHubStateUpdate> states;
        try { states = await github.GetStatesAsync(repository, project.Path, numbers, ct); }
        catch (GitHubCliException ex) { throw new PrReviewException(502, ex.Message); }
        var counts = await store.RecordGitHubStatesAsync(repository.FullName, states, ct);
        int CountOf(string state) => states.Count(entry => entry.State == state);
        return new PrReviewSyncResponse(numbers.Count, counts.Recorded, counts.Changed,
            CountOf(PrGitHubState.Open), CountOf(PrGitHubState.Merged), CountOf(PrGitHubState.Closed));
    }

    /// <summary>Adds or updates a PR's metadata; the request names the PR by <c>number</c>.</summary>
    public async Task<PrReviewWriteResponse> UpsertAsync(string projectPath, PrReviewRequest request, CancellationToken ct = default)
    {
        var repository = await RequireRepositoryNameAsync(projectPath, ct);
        RejectManagedTimes(request);
        var number = request.Number ?? throw new PrReviewException(400, "number is required");
        RequireNumber(number);
        var result = await store.UpsertAsync(repository, number, ReadPullRequestFields(request), ct);
        RequireSaved(result, number);
        return new PrReviewWriteResponse(result.PullRequest!, null, result.Outcome == PrReviewWriteOutcome.Created);
    }

    public async Task<PrReviewPullRequest> UpdateAsync(string projectPath, int number, PrReviewRequest request,
        CancellationToken ct = default)
    {
        var repository = await RequireRepositoryNameAsync(projectPath, ct);
        RejectManagedTimes(request);
        RequireNumber(number);
        var result = await store.UpdateAsync(repository, number, ReadPullRequestFields(request), ct);
        if (result.Outcome == PrReviewWriteOutcome.NotTracked)
            throw new PrReviewException(404, $"PR #{number} was not found.");
        return result.PullRequest!;
    }

    public async Task DeleteAsync(string projectPath, int number, CancellationToken ct = default)
    {
        var repository = await RequireRepositoryNameAsync(projectPath, ct);
        RequireNumber(number);
        if (!await store.DeleteAsync(repository, number, ct))
            throw new PrReviewException(404, $"PR #{number} was not found.");
    }

    /// <summary>Idempotent: creates the PR if needed and returns the pending review if one is open.</summary>
    public async Task<PrReviewWriteResponse> StartAsync(string projectPath, int number, PrReviewRequest? request,
        CancellationToken ct = default)
    {
        await RequireEnabledAsync(ct);
        var project = await RequireRepositoryAsync(projectPath, ct);
        var repository = project.Repository!.FullName;
        request ??= Empty;
        RejectManagedTimes(request);
        RequireNumber(number);
        var headSha = ReadText(request.HeadSha, "headSha", MaxHeadSha);
        var fields = ReadPullRequestFields(request);
        if (request.UseCurrentHead)
        {
            GitHubPullRequest current;
            try { current = await github.GetAsync(project.Repository, project.Path, number, ct); }
            catch (GitHubCliException ex) { throw new PrReviewException(502, ex.Message); }
            headSha = current.HeadSha;
            fields = fields with { Title = current.Title, Author = current.Author, OpenedUtc = current.OpenedUtc };
        }
        var result = await store.StartReviewAsync(repository, number, fields, headSha, ct);
        RequireSaved(result, number);
        return new PrReviewWriteResponse(result.PullRequest!, result.Review, result.Outcome == PrReviewWriteOutcome.Created);
    }

    /// <summary>Checkpoints partial work so an interrupted run resumes from findings, not from nothing.</summary>
    public async Task<PrReviewWriteResponse> DraftAsync(string projectPath, int number, PrReviewRequest? request,
        CancellationToken ct = default)
    {
        var repository = await RequireRepositoryNameAsync(projectPath, ct);
        request ??= Empty;
        RejectManagedTimes(request);
        RequireNumber(number);
        var fields = new PrReviewFields(
            ReadText(request.HeadSha, "headSha", MaxHeadSha),
            ReadVerdict(request.Verdict, required: false),
            request.Blocking,
            ReadText(request.Feedback, "feedback", MaxFeedback));
        var summary = ReadText(request.Summary, "summary", MaxSummary);
        var result = await store.SaveDraftAsync(repository, number, fields, summary, ct);
        RequireSaved(result, number);
        return new PrReviewWriteResponse(result.PullRequest!, result.Review, false);
    }

    /// <summary>
    /// Finishes the pending review and stamps its reviewed time. A caller that says nothing about
    /// blockers gets the default mapping: request_changes is exactly "a blocker was found".
    /// </summary>
    public async Task<PrReviewWriteResponse> CompleteAsync(string projectPath, int number, PrReviewRequest? request,
        CancellationToken ct = default)
    {
        var repository = await RequireRepositoryNameAsync(projectPath, ct);
        request ??= Empty;
        RejectManagedTimes(request);
        RequireNumber(number);
        var feedback = ReadText(request.Feedback, "feedback", MaxFeedback, required: true)!;
        var verdict = ReadVerdict(request.Verdict, required: true)!;
        var blocking = request.Blocking ?? verdict == PrReviewVerdict.RequestChanges;
        var headSha = ReadText(request.HeadSha, "headSha", MaxHeadSha);
        var summary = ReadText(request.Summary, "summary", MaxSummary);
        var result = await store.CompleteReviewAsync(repository, number, verdict, blocking, feedback, headSha, summary, ct);
        RequireSaved(result, number);
        var delivery = await store.EnsureDeliveryAsync(repository, result.Review!.Id, ct);
        if ((await store.GetReviewerSettingsAsync(ct)).Enabled)
            delivery = await PublishAsync(projectPath, number, result.Review.Id, ct);
        return new PrReviewWriteResponse(result.PullRequest!, result.Review, false) { Delivery = delivery };
    }

    /// <summary>Reads the shared reviewer and outbound publishing switch.</summary>
    public Task<PrReviewerSettings> GetSettingsAsync(CancellationToken ct = default) => store.GetReviewerSettingsAsync(ct);

    /// <summary>Disables new queue/start work and POST reservations; history and running local work survive.</summary>
    public Task<PrReviewerSettings> SetSettingsAsync(bool enabled, CancellationToken ct = default) => store.SetReviewerSettingsAsync(enabled, ct);

    private async Task RequireEnabledAsync(CancellationToken ct)
    {
        if (!(await store.GetReviewerSettingsAsync(ct)).Enabled)
            throw new PrReviewException(409, "PR Reviewer is off. Enable PR Reviewer and GitHub publishing in Settings > PR Reviewer to start new reviews.");
    }

    /// <summary>Retries safe failures or reconciles an uncertain POST without ever resending it.</summary>
    public async Task<PrReviewDelivery> PublishAsync(string projectPath, int number, long id, CancellationToken ct = default)
    {
        var project = await RequireRepositoryAsync(projectPath, ct);
        RequireNumber(number);
        RequireReviewId(id);
        var repository = project.Repository!;
        var delivery = (await store.GetDeliveriesAsync(repository.FullName, number, ct)).FirstOrDefault(d => d.ReviewId == id)
            ?? throw new PrReviewException(404, "No delivery exists for that review in this project and PR.");
        if (delivery.Status == "published") return delivery;
        await RequireEnabledAsync(ct);
        var claimed = false;
        try
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(delivery.HeadSha, "^[0-9a-fA-F]{40}$"))
                throw new GitHubCliException("Publishing requires the full 40-character reviewed commit SHA. Start a new pass at the current head.");
            if (delivery.Body.Length > 60_000)
                throw new GitHubCliException("Review feedback exceeds the GitHub publishing limit of 60,000 characters. Start a shorter review pass.");
            var login = await github.GetViewerAsync(project.Path, ct);
            if (delivery.Status == "unknown")
            {
                if (!string.Equals(login, delivery.Login, StringComparison.OrdinalIgnoreCase))
                    throw new GitHubCliException("Sign in with the original publishing account to check this unconfirmed review.");
                var receipt = await github.FindReviewAsync(repository, project.Path, delivery, ct);
                if (receipt is { } existing)
                    await store.SetDeliveryResultAsync(repository.FullName, id, "published", existing, null, CancellationToken.None);
                // Absence cannot prove that a timed-out request will not arrive later.
            }
            else
            {
                // Recheck the target before dispatch, never substitute today's SHA for the reviewed SHA.
                var current = await github.GetAsync(repository, project.Path, number, ct);
                if (current.Number != number || !string.Equals(current.HeadSha, delivery.HeadSha, StringComparison.OrdinalIgnoreCase))
                    throw new GitHubCliException("The PR head changed since this review. Review the new head in a new pass before publishing.");
                claimed = await store.ClaimDeliveryAsync(repository.FullName, id, login, ct);
                if (claimed)
                {
                    var receipt = await github.PublishReviewAsync(repository, project.Path, delivery with { Login = login }, ct);
                    await store.SetDeliveryResultAsync(repository.FullName, id, "published", receipt, null, CancellationToken.None);
                }
            }
        }
        catch (GitHubCliException ex)
        {
            var status = delivery.Status == "unknown" || (claimed && ex.MayHavePosted) ? "unknown" : "failed";
            await store.SetDeliveryResultAsync(repository.FullName, id, status, null, ex.Message, CancellationToken.None,
                claimed ? "unknown" : delivery.Status);
        }
        catch (OperationCanceledException)
        {
            // A cancelled POST may still have reached GitHub; its durable reservation stays unknown.
            if (!claimed) throw;
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[PrReviews] GitHub delivery failed for review {ReviewId}", id);
            await store.SetDeliveryResultAsync(repository.FullName, id,
                claimed || delivery.Status == "unknown" ? "unknown" : "failed", null,
                "GitHub delivery was not confirmed. Check authentication and repository permission, then retry to check delivery.",
                CancellationToken.None, claimed ? "unknown" : delivery.Status);
        }
        return (await store.GetDeliveriesAsync(repository.FullName, number, CancellationToken.None)).Single(d => d.ReviewId == id);
    }

    /// <summary>Edits feedback, verdict, blocking or head SHA on any review, including a completed one.</summary>
    public async Task<PrReview> UpdateReviewAsync(string projectPath, long id, PrReviewRequest request,
        CancellationToken ct = default)
    {
        var repository = await RequireRepositoryNameAsync(projectPath, ct);
        RejectManagedTimes(request);
        RequireReviewId(id);
        var fields = new PrReviewFields(
            ReadText(request.HeadSha, "headSha", MaxHeadSha),
            ReadVerdict(request.Verdict, required: false),
            request.Blocking,
            ReadText(request.Feedback, "feedback", MaxFeedback));
        return await store.UpdateReviewAsync(repository, id, fields, ct)
            ?? throw new PrReviewException(404, $"Review {id} was not found.");
    }

    /// <summary>Deletes one review and returns the number of the PR it belonged to.</summary>
    public async Task<int> DeleteReviewAsync(string projectPath, long id, CancellationToken ct = default)
    {
        var repository = await RequireRepositoryNameAsync(projectPath, ct);
        RequireReviewId(id);
        return await store.DeleteReviewAsync(repository, id, ct)
            ?? throw new PrReviewException(404, $"Review {id} was not found.");
    }

    private async Task<PrReviewProject> RequireRepositoryAsync(string projectPath, CancellationToken ct)
    {
        var project = await repositories.ResolveAsync(projectPath, ct);
        return project.Repository is null
            ? throw new PrReviewException(409, project.Problem ?? "This project has no GitHub repository.")
            : project;
    }

    private async Task<string> RequireRepositoryNameAsync(string projectPath, CancellationToken ct) =>
        (await RequireRepositoryAsync(projectPath, ct)).Repository!.FullName;

    /// <summary>
    /// Untracked and tracked-but-not-started are different mistakes with different fixes, so they
    /// get different statuses instead of one ambiguous conflict.
    /// </summary>
    private static void RequireSaved(PrReviewWriteResult result, int number)
    {
        switch (result.Outcome)
        {
            case PrReviewWriteOutcome.MissingField:
                throw new PrReviewException(400, $"{result.MissingField} is required for a new PR");
            case PrReviewWriteOutcome.NotTracked:
                throw new PrReviewException(404, $"PR #{number} is not tracked; start a review for it first");
            case PrReviewWriteOutcome.NoPendingReview:
                throw new PrReviewException(409, $"PR #{number} does not have an in-progress review");
        }
    }

    /// <summary>
    /// Started, reviewed, created and updated times are the server's. A caller that sends one is
    /// refused by name rather than silently ignored, so it learns the value was never used.
    /// </summary>
    internal static void RejectManagedTimes(PrReviewRequest request)
    {
        foreach (var (field, value) in new[]
        {
            ("createdAt", request.CreatedAt), ("updatedAt", request.UpdatedAt),
            ("startedAt", request.StartedAt), ("reviewedAt", request.ReviewedAt),
        })
        {
            if (value.ValueKind != JsonValueKind.Undefined)
                throw new PrReviewException(400, $"{field} is managed by the server");
        }
    }

    /// <summary>A blank title, author or opened time keeps the stored one; a blank summary clears it.</summary>
    private static PrReviewPullRequestFields ReadPullRequestFields(PrReviewRequest request)
    {
        var openedAt = ReadText(request.OpenedAt, "openedAt", MaxOpenedAt);
        DateTime? opened = null;
        if (!string.IsNullOrEmpty(openedAt))
        {
            opened = DateTimeOffset.TryParse(openedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed.UtcDateTime
                : throw new PrReviewException(400, "openedAt must be a valid date or timestamp");
        }
        return new PrReviewPullRequestFields(
            NullIfEmpty(ReadText(request.Title, "title", MaxTitle)),
            NullIfEmpty(ReadText(request.Author, "author", MaxAuthor)),
            opened,
            ReadText(request.Summary, "summary", MaxSummary));
    }

    internal static string? ReadText(string? value, string field, int max, bool required = false)
    {
        if (value is null)
            return required ? throw new PrReviewException(400, $"{field} is required") : null;
        var trimmed = value.Trim();
        if (required && trimmed.Length == 0) throw new PrReviewException(400, $"{field} is required");
        if (trimmed.Length > max) throw new PrReviewException(400, $"{field} must be {max} characters or fewer");
        return trimmed;
    }

    private static string? ReadVerdict(string? value, bool required)
    {
        var verdict = ReadText(value, "verdict", MaxVerdict, required);
        if (verdict is not null && !PrReviewVerdict.IsValid(verdict))
            throw new PrReviewException(400, "verdict must be recommend_approve, comments, or request_changes");
        return verdict;
    }

    private static void RequireNumber(int number)
    {
        if (number <= 0) throw new PrReviewException(400, "PR number must be a positive integer");
    }

    private static void RequireReviewId(long id)
    {
        if (id <= 0) throw new PrReviewException(400, "Review id is invalid");
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
