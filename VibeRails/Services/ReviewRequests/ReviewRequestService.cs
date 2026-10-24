using System.Text.Json;
using VibeRails.DB;
using VibeRails.Services.GitPreflight;
using VibeRails.Services.VCA;

namespace VibeRails.Services.ReviewRequests;

/// <summary>
/// Review requests before a commit (VB-50, slice A): capture the staged changes as a bundle, keep
/// the request and its decisions in state.db, and accept a decision only after it is verified.
/// Sending a request and collecting its hosted decisions is <see cref="ReviewRequestSender"/> and
/// <see cref="ReviewRequestPoller"/> (slice C); both land decisions through this class.
/// </summary>
/// <remarks>
/// An approval is accepted exactly the way the commit hook would accept it: the trailer must cover
/// this request's tree, viberails.ai must confirm the signature and name the signer's verified email,
/// and that email must be one of the request's reviewers. Nothing here signs; the hook later
/// re-verifies whatever it applies from the ledger, so the ledger is a convenience, never a gate.
/// </remarks>
public sealed class ReviewRequestService(
    IReviewRequestStore store,
    IGitStagedSnapshotProvider snapshots,
    ICommitSignatureVerifier verifier,
    TimeProvider? clock = null)
{
    public const int MaxReviewers = 10;
    public const int MaxSessions = 10;
    public const int MaxNoteChars = 2000;
    public const int MaxCommentChars = 4000;
    public const int MaxCardKeyChars = 64;
    public const int ListLimit = 100;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<ReviewRequestSummary> CreateAsync(
        string projectPath,
        CreateReviewRequest? request,
        ISessionStore? sessions,
        CancellationToken ct)
    {
        projectPath = NormalizeProject(projectPath);
        var scope = (request?.Scope ?? ReviewScope.Staged).Trim().ToLowerInvariant();
        if (scope != ReviewScope.Staged)
            throw Invalid("Only the staged changes can be sent for review in this version: use scope \"staged\".");
        var reviewers = NormalizeReviewers(request?.Reviewers);
        var cardKey = Bounded(request?.CardKey, MaxCardKeyChars, "cardKey");
        var note = Bounded(request?.Note, MaxNoteChars, "note");
        var attached = await ResolveSessionsAsync(request?.Sessions, sessions, ct);

        GitStagedSnapshot snapshot;
        try
        {
            snapshot = await snapshots.CaptureAsync(projectPath, ct);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            throw new ReviewRequestException("not_a_repository",
                "VibeRails could not read the staged changes of this project. Check that it is a Git repository.", 409);
        }
        if (snapshot.StagedIdentity is null || !CommitSignatureFormat.IsObjectId(snapshot.StagedIdentity.StagedTree))
            throw new ReviewRequestException("not_a_repository", "VibeRails could not determine the staged tree of this project.", 409);
        if (snapshot.Files.Count == 0)
            throw new ReviewRequestException("nothing_staged", "Nothing is staged. Stage the changes to review with git add first.", 409);

        var now = _clock.GetUtcNow().UtcDateTime;
        var bundle = await ReviewBundleCapture.BuildAsync(snapshot, ReviewBundleCapture.BaseTextReader(snapshot),
            attached, reviewers, cardKey, note, now, ct);
        var record = new ReviewRequestRecord(
            Guid.NewGuid().ToString("D"),
            projectPath,
            scope,
            bundle.TreeId,
            bundle.BaseCommit,
            cardKey,
            ReviewRequestStatus.Open,
            JsonSerializer.Serialize(bundle, ReviewRequestJsonContext.Default.ReviewBundle),
            now,
            now);
        await store.AddAsync(record, ct);
        return Summarize(record, bundle, []);
    }

    public async Task<ReviewRequestList> ListAsync(string projectPath, CancellationToken ct)
    {
        var records = await store.ListAsync(NormalizeProject(projectPath), ListLimit, ct);
        var summaries = new List<ReviewRequestSummary>(records.Count);
        foreach (var record in records)
            summaries.Add(Summarize(record, Bundle(record), await store.GetDecisionsAsync(record.Id, ct)));
        return new ReviewRequestList(summaries);
    }

    public async Task<ReviewRequestDetail> GetAsync(string projectPath, string id, CancellationToken ct)
    {
        var record = await RequireAsync(projectPath, id, ct);
        var bundle = Bundle(record);
        return new ReviewRequestDetail(Summarize(record, bundle, await store.GetDecisionsAsync(id, ct)), bundle);
    }

    public async Task<ReviewRequestSummary> CancelAsync(string projectPath, string id, CancellationToken ct)
    {
        var record = await RequireAsync(projectPath, id, ct);
        var now = _clock.GetUtcNow().UtcDateTime;
        if (record.Status != ReviewRequestStatus.Cancelled)
            await store.UpdateStatusAsync(id, ReviewRequestStatus.Cancelled, now, ct);
        record = record with { Status = ReviewRequestStatus.Cancelled, UpdatedUTC = now };
        return Summarize(record, Bundle(record), await store.GetDecisionsAsync(id, ct));
    }

    public async Task DeleteAsync(string projectPath, string id, CancellationToken ct)
    {
        await RequireAsync(projectPath, id, ct);
        await store.DeleteAsync(id, ct);
    }

    public Task<ReviewRequestSummary> RecordDecisionAsync(
        string projectPath,
        string id,
        RecordReviewDecisionRequest? request,
        CancellationToken ct) => RecordDecisionAsync(projectPath, id, request, null, ct);

    /// <summary>
    /// Records a decision. <paramref name="remoteDecisionId"/> is the hosted decision id when the
    /// decision arrived through polling (slice C); it is stored with the row so a later poll skips it.
    /// A polled approval takes exactly the same path as one pasted locally: tree match, viberails.ai
    /// verification, listed reviewer, one transaction with the status.
    /// </summary>
    public async Task<ReviewRequestSummary> RecordDecisionAsync(
        string projectPath,
        string id,
        RecordReviewDecisionRequest? request,
        long? remoteDecisionId,
        CancellationToken ct)
    {
        var record = await RequireAsync(projectPath, id, ct);
        if (record.Status == ReviewRequestStatus.Cancelled)
            throw new ReviewRequestException("not_open", "This review request was cancelled, so it takes no decisions.", 409);
        var bundle = Bundle(record);
        var comment = Bounded(request?.Comment, MaxCommentChars, "comment");
        var now = _clock.GetUtcNow().UtcDateTime;

        ReviewDecisionRecord decision;
        switch ((request?.Decision ?? string.Empty).Trim().ToLowerInvariant())
        {
            case ReviewDecisionKind.Rejected:
            {
                var reviewer = NormalizeEmail(request?.Reviewer)
                    ?? throw Invalid("A rejection names the reviewer's email address.");
                if (!bundle.Reviewers.Contains(reviewer, StringComparer.OrdinalIgnoreCase))
                    throw new ReviewRequestException("not_a_reviewer", $"{reviewer} is not a reviewer on this request.", 409);
                decision = new ReviewDecisionRecord(0, id, reviewer, ReviewDecisionKind.Rejected, comment,
                    null, null, null, null, now, null, remoteDecisionId);
                break;
            }
            case ReviewDecisionKind.Approved:
            {
                var trailer = (request?.Trailer ?? string.Empty).Trim();
                if (!CommitSignatureFormat.TryParseTrailer(trailer, out var signature))
                    throw Invalid($"An approval is the reviewer's \"{CommitSignatureFormat.Trailer}\" line, exactly as their signing popup printed it.");
                if (!string.Equals(signature.TreeId, record.TreeId, StringComparison.Ordinal))
                    throw new ReviewRequestException("tree_mismatch",
                        $"The approval was signed for tree {signature.TreeId[..12]}, but this request is for tree {record.TreeId[..12]}. "
                        + "The reviewer approved different content.", 409);

                var verification = await verifier.VerifyAsync(signature, ct);
                if (verification.Status == CommitSignatureStatus.Unavailable)
                    throw new ReviewRequestException("verification_unavailable",
                        $"viberails.ai could not verify the approval: {verification.Detail}. Try again when it is reachable.", 503);
                if (verification.Status == CommitSignatureStatus.Rejected || verification.SignerEmail is null)
                    throw new ReviewRequestException("signature_rejected",
                        $"viberails.ai did not accept the approval: {verification.Detail}.", 409);
                var signer = verification.SignerEmail;
                if (!bundle.Reviewers.Contains(signer, StringComparer.OrdinalIgnoreCase))
                    throw new ReviewRequestException("not_a_reviewer",
                        $"The approval was signed by {signer}, who is not a reviewer on this request.", 409);
                decision = new ReviewDecisionRecord(0, id, signer, ReviewDecisionKind.Approved, comment,
                    signature.KeyId, signature.Fingerprint, signature.TreeId, signature.SignatureBase64, now, now, remoteDecisionId);
                break;
            }
            default:
                throw Invalid("decision must be \"approved\" or \"rejected\".");
        }

        // One transaction decides: a cancel that landed while the signature was being verified wins,
        // and nothing is stored, so a cancelled request can never be revived into a hook-eligible approval.
        var outcome = await store.RecordDecisionAsync(decision, now, ct)
            ?? throw new ReviewRequestException("not_open", "This review request was cancelled before the decision was recorded.", 409);
        var decisions = await store.GetDecisionsAsync(id, ct);
        return Summarize(record with { Status = outcome.Status, UpdatedUTC = now }, bundle, decisions);
    }

    /// <summary>
    /// The newest verified approval over <paramref name="treeId"/> from one of <paramref name="approvers"/>
    /// (any verified signer when the list is empty), as the commit hook needs it. Rows without the
    /// signature fields cannot be applied and are skipped.
    /// </summary>
    public static ReviewDecisionRecord? SelectApproval(IReadOnlyList<ReviewDecisionRecord> verifiedApprovals, IReadOnlyList<string> approvers)
    {
        foreach (var row in verifiedApprovals)
        {
            if (row.KeyId is null || row.Fingerprint is null || row.SignatureBase64 is null || row.TreeId is null)
                continue;
            if (approvers.Count > 0 && !approvers.Contains(row.Reviewer, StringComparer.OrdinalIgnoreCase))
                continue;
            return row;
        }
        return null;
    }

    internal static IReadOnlyList<string> NormalizeReviewers(IReadOnlyList<string>? reviewers)
    {
        var result = new List<string>();
        foreach (var entry in reviewers ?? [])
        {
            var email = NormalizeEmail(entry) ?? throw Invalid($"'{(entry ?? string.Empty).Trim()}' is not an email address.");
            if (!result.Contains(email, StringComparer.OrdinalIgnoreCase)) result.Add(email);
        }
        if (result.Count == 0)
            throw Invalid("Name at least one reviewer by email address.");
        if (result.Count > MaxReviewers)
            throw Invalid($"A review request names at most {MaxReviewers} reviewers.");
        return result;
    }

    private static async Task<IReadOnlyList<ReviewBundleSession>> ResolveSessionsAsync(
        IReadOnlyList<string>? ids, ISessionStore? sessions, CancellationToken ct)
    {
        var result = new List<ReviewBundleSession>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in ids ?? [])
        {
            if (!Guid.TryParseExact(raw?.Trim(), "D", out var id) || id == Guid.Empty)
                throw Invalid("Each attached session is its VibeRails session id.");
            var key = id.ToString("D");
            if (!seen.Add(key)) continue;
            if (result.Count >= MaxSessions)
                throw Invalid($"A review request attaches at most {MaxSessions} sessions.");
            var session = sessions is null ? null : await sessions.GetSessionByIdAsync(key, ct);
            if (session is null)
                throw new ReviewRequestException("session_not_found", $"Session {key} was not found on this computer.", 404);
            var (cli, displayName) = await sessions!.GetSessionDisplayInfoAsync(key);
            var name = FirstNonBlank(displayName, session.EnvironmentName, cli, session.Cli) ?? key;
            result.Add(new ReviewBundleSession(key, name, cli ?? session.Cli, session.StartedUTC, session.EndedUTC));
        }
        return result;
    }

    /// <summary>The request as stored, with its decisions; for callers that changed the row and answer with it.</summary>
    internal async Task<ReviewRequestSummary> SummaryAsync(string id, CancellationToken ct)
    {
        var record = await store.GetAsync(id, ct) ?? throw new ReviewRequestException("not_found", "Review request not found.", 404);
        return Summarize(record, Bundle(record), await store.GetDecisionsAsync(id, ct));
    }

    internal async Task<ReviewRequestRecord> RequireAsync(string projectPath, string id, CancellationToken ct)
    {
        var record = Guid.TryParseExact(id, "D", out _) ? await store.GetAsync(id, ct) : null;
        if (record is null || !string.Equals(record.ProjectPath, NormalizeProject(projectPath), StringComparison.OrdinalIgnoreCase))
            throw new ReviewRequestException("not_found", "Review request not found.", 404);
        return record;
    }

    /// <summary>One spelling per project: a full path without a trailing separator, as the Board stores it.</summary>
    internal static string NormalizeProject(string projectPath)
    {
        var full = Path.GetFullPath(projectPath);
        var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Length == 0 ? full : trimmed;
    }

    internal static ReviewBundle Bundle(ReviewRequestRecord record) =>
        JsonSerializer.Deserialize(record.BundleJson, ReviewRequestJsonContext.Default.ReviewBundle)
        ?? throw new ReviewRequestException("corrupt", "The stored review bundle could not be read.", 500);

    private static ReviewRequestSummary Summarize(ReviewRequestRecord record, ReviewBundle bundle, IReadOnlyList<ReviewDecisionRecord> decisions) =>
        new(record.Id, record.Scope, record.TreeId, record.BaseCommit, record.Status, bundle.Reviewers, record.CardKey,
            bundle.Files.Count, bundle.Sessions.Count, record.CreatedUTC, record.UpdatedUTC,
            decisions.Select(ToDto).ToList(),
            record.RemoteId is { } remote
                ? new ReviewRequestRemote(remote, record.SentUTC ?? record.UpdatedUTC, record.ExpiresUTC, record.RemoteCancelledUTC)
                : null);

    private static ReviewDecisionDto ToDto(ReviewDecisionRecord decision) =>
        new(decision.Id, decision.Reviewer, decision.Decision, decision.Comment,
            decision is { KeyId: { } key, Fingerprint: { } fingerprint, TreeId: { } tree, SignatureBase64: { } signature }
                ? CommitSignatureFormat.Format(new CommitSignature(key, fingerprint, tree, signature))
                : null,
            decision.DecidedUTC, decision.VerifiedUTC);

    private static string? NormalizeEmail(string? value)
    {
        var email = value?.Trim();
        return HumanSignatureRule.IsEmail(email) ? email : null;
    }

    private static string? Bounded(string? value, int maxChars, string field)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        if (text.Length > maxChars) throw Invalid($"{field} is at most {maxChars} characters.");
        return text;
    }

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static ReviewRequestException Invalid(string message) => new("invalid_request", message, 400);
}
