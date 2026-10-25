using System.Globalization;
using System.Text;
using System.Text.Json;
using VibeRails.DB;
using VibeRails.Services.Git;
using VibeRails.Services.GitPreflight;
using VibeRails.Services.VCA;

namespace VibeRails.Services.ReviewRequests;

/// <summary>The tree id of each commit the checkout has, keyed by the sha as it was asked (a full id or a prefix).</summary>
public delegate Task<IReadOnlyDictionary<string, string>> CommitTreeResolver(string projectPath, IReadOnlyList<string> shas, CancellationToken cancellationToken);

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
    TimeProvider? clock = null,
    CommitTreeResolver? commitTrees = null)
{
    public const int MaxReviewers = 10;
    public const int MaxSessions = 10;
    public const int MaxNoteChars = 2000;
    public const int MaxCommentChars = 4000;
    /// <summary>
    /// Per-line comments on one decision (slice F): how many, how long each, the largest line number, and the
    /// total of path and text characters across the decision. The total is what keeps a maximal decision within
    /// the decision body bounds and fifty of them within <see cref="ReviewRequestTransport.MaxResponseBytes"/>
    /// once the host escapes every non-ASCII or HTML-sensitive character as six bytes (Codex slice F review, F1).
    /// </summary>
    public const int MaxLineComments = 50;
    public const int MaxLineCommentChars = 1000;
    public const int MaxLineCommentLine = 1_000_000;
    public const int MaxLineCommentTotalChars = 16_000;
    public const int MaxCardKeyChars = 64;
    public const int ListLimit = 100;

    /// <summary>Commits one card can ask about in a single lookup; the card rail shows at most 30.</summary>
    public const int MaxCommitLookups = 50;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly CommitTreeResolver _commitTrees = commitTrees ?? ResolveCommitTreesAsync;

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
        var snapshot = await CaptureStagedAsync(projectPath, ct);

        var now = _clock.GetUtcNow().UtcDateTime;
        var bundle = await ReviewBundleCapture.BuildAsync(snapshot, ReviewBundleCapture.BaseTextReader(snapshot),
            ReviewBundleCapture.StagedBytesReader(snapshot), attached, reviewers, cardKey, note, now, ct);
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

    /// <summary>
    /// What a request made now would capture (slice E): the staged files with their line counts and
    /// omissions, the tree id, the <c>Signed Approval</c> rules the staged paths touch (their approvers
    /// are the suggested reviewers) and the fixed disclosure a send shows. Reads the index the way
    /// <see cref="CreateAsync"/> does and stores nothing; the same refusals apply when nothing is staged.
    /// </summary>
    public async Task<ReviewStagedPreview> PreviewAsync(string projectPath, CancellationToken ct)
    {
        projectPath = NormalizeProject(projectPath);
        var snapshot = await CaptureStagedAsync(projectPath, ct);
        var now = _clock.GetUtcNow().UtcDateTime;
        var bundle = await ReviewBundleCapture.BuildAsync(snapshot, ReviewBundleCapture.BaseTextReader(snapshot),
            ReviewBundleCapture.StagedBytesReader(snapshot), [], [], null, null, now, ct);

        var files = new List<ReviewStagedFile>(bundle.Files.Count);
        long textBytes = 0, carriedBytes = 0;
        var withText = 0;
        var withBytes = 0;
        foreach (var file in bundle.Files)
        {
            ct.ThrowIfCancellationRequested();
            var included = file.Omitted is null;
            var bytesIncluded = file.AfterBytes is not null;
            if (included) withText++;
            if (bytesIncluded)
            {
                withBytes++;
                carriedBytes += ReviewBundleCapture.DecodedLength(file.AfterBytes);
            }
            textBytes += Utf8(file.Before) + Utf8(file.After);
            var counts = included ? ReviewLineDiff.Count(file.Before, file.After) : null;
            files.Add(new ReviewStagedFile(file.Path, file.Change, file.PreviousPath, file.ChangedLines,
                counts?.Added, counts?.Removed, included, file.Omitted, bytesIncluded));
        }

        var rules = MatchApprovalRules(snapshot);
        var suggested = rules.SelectMany(rule => rule.Approvers)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxReviewers)
            .ToList();
        return new ReviewStagedPreview(bundle.Repository, bundle.TreeId, bundle.BaseTree, bundle.BaseCommit, files, withText,
            files.Count - withText, textBytes, suggested, rules, ReviewRequestTransport.Destination, ReviewRequestSender.Audience,
            ReviewRequestSender.SessionDisclosure, now.AddMonths(1), withBytes, carriedBytes);
    }

    /// <summary>
    /// The <c>Signed Approval</c> rules of the staged <c>vc.rules.md</c> files whose path one of the
    /// staged files touches, resolved and matched exactly as the hook resolves and matches them
    /// (<see cref="PathLockRule"/>, path relative to the declaring file, the declaring file itself
    /// ordinary content). Unparsable or unresolvable rules are skipped: they suggest nobody.
    /// </summary>
    internal static IReadOnlyList<ReviewApprovalRule> MatchApprovalRules(GitStagedSnapshot snapshot)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var result = new List<ReviewApprovalRule>();
        foreach (var agentFile in snapshot.AgentFiles.OrderBy(file => file.RelativePath, StringComparer.Ordinal))
        {
            var sourceFullPath = Path.Combine(snapshot.RepositoryPath, agentFile.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            foreach (var line in AgentRuleSectionReader.Read(agentFile.Content))
            {
                if (line.Enforcement is "SKIP" or "DISABLED") continue;
                if (!HumanSignatureRule.TryParseSignedApproval(line.RuleText, out var approval)) continue;
                var specification = new PathLockSpecification(PathLockKind.Directory, approval.RelativePath);
                if (!PathLockRule.TryResolveRepositoryPath(specification, sourceFullPath, snapshot.RepositoryPath,
                        out var approvalPath, out _, "approval path"))
                    continue;
                var matched = snapshot.Files
                    .Where(file => PathLockRule.Matches(specification, approvalPath, file.RelativePath, file.PreviousRelativePath,
                        sourceAgentRepositoryPath: string.Empty, comparison))
                    .Select(file => file.RelativePath)
                    .Distinct(comparer)
                    .Order(StringComparer.Ordinal)
                    .ToList();
                if (matched.Count == 0) continue;
                result.Add(new ReviewApprovalRule(line.RuleText, agentFile.RelativePath, approvalPath, approval.Approvers, matched));
            }
        }
        return result;
    }

    /// <summary>
    /// For each linked commit, the verified approvals over its tree, found exactly as the commit hook
    /// finds them (<see cref="IReviewRequestStore.FindVerifiedApprovalsAsync"/>): one entry per
    /// reviewer, newest first. A sha the checkout does not have answers with no tree and no approvals.
    /// </summary>
    public async Task<ReviewCommitApprovals> CommitApprovalsAsync(string projectPath, IReadOnlyList<string> shas, CancellationToken ct)
    {
        projectPath = NormalizeProject(projectPath);
        var requested = new List<string>();
        foreach (var raw in shas)
        {
            var sha = raw?.Trim().ToLowerInvariant() ?? string.Empty;
            if (sha.Length is < 7 or > 64 || !sha.All(character => character is (>= '0' and <= '9') or (>= 'a' and <= 'f')))
                throw Invalid("Each sha is 7 to 64 hexadecimal characters.");
            if (!requested.Contains(sha, StringComparer.Ordinal)) requested.Add(sha);
        }
        if (requested.Count > MaxCommitLookups)
            throw Invalid($"Ask about at most {MaxCommitLookups} commits at a time.");

        var trees = requested.Count == 0
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : await _commitTrees(projectPath, requested, ct);
        var byTree = new Dictionary<string, IReadOnlyList<ReviewCommitApproval>>(StringComparer.Ordinal);
        var entries = new List<ReviewCommitApprovalEntry>(requested.Count);
        foreach (var sha in requested)
        {
            if (!trees.TryGetValue(sha, out var tree) || !CommitSignatureFormat.IsObjectId(tree))
            {
                entries.Add(new ReviewCommitApprovalEntry(sha, null, []));
                continue;
            }
            if (!byTree.TryGetValue(tree, out var approvals))
            {
                var rows = await store.FindVerifiedApprovalsAsync(tree, ct);
                approvals = rows
                    .Where(row => row.SignatureBase64 is not null)
                    .GroupBy(row => row.Reviewer, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .Select(row => new ReviewCommitApproval(row.Reviewer, row.RequestId, row.VerifiedUTC ?? row.DecidedUTC))
                    .ToList();
                byTree[tree] = approvals;
            }
            entries.Add(new ReviewCommitApprovalEntry(sha, tree, approvals));
        }
        return new ReviewCommitApprovals(entries);
    }

    /// <summary>
    /// The default <see cref="CommitTreeResolver"/>: one <c>git log --no-walk</c> through
    /// <see cref="GitCli"/> (argv only, bounded output), <c>--ignore-missing</c> so a sha the checkout
    /// lacks is simply absent from the answer. A git failure answers an empty map: no approval is shown
    /// rather than a wrong one.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, string>> ResolveCommitTreesAsync(string projectPath, IReadOnlyList<string> shas, CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (shas.Count == 0) return result;
        var arguments = new List<string> { "--no-pager", "log", "--no-walk=unsorted", "--ignore-missing", "--format=%H %T" };
        arguments.AddRange(shas);
        arguments.Add("--");
        var run = await GitCli.RunAsync(projectPath, arguments, ct, maxOutputChars: 64 * 1024);
        if (!run.Succeeded) return result;
        foreach (var line in run.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || !CommitSignatureFormat.IsObjectId(parts[0]) || !CommitSignatureFormat.IsObjectId(parts[1])) continue;
            foreach (var sha in shas)
            {
                if (parts[0].StartsWith(sha, StringComparison.Ordinal)) result.TryAdd(sha, parts[1]);
            }
        }
        return result;
    }

    private async Task<GitStagedSnapshot> CaptureStagedAsync(string projectPath, CancellationToken ct)
    {
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
        return snapshot;
    }

    private static int Utf8(string? text) => text is null ? 0 : Encoding.UTF8.GetByteCount(text);

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
        var lineComments = NormalizeLineComments(request?.LineComments);
        var lineCommentsJson = lineComments is null ? null
            : JsonSerializer.Serialize(lineComments, ReviewRequestJsonContext.Default.IReadOnlyListReviewLineComment);
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
                    null, null, null, null, now, null, remoteDecisionId, lineCommentsJson);
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
                    signature.KeyId, signature.Fingerprint, signature.TreeId, signature.SignatureBase64, now, now, remoteDecisionId, lineCommentsJson);
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
    /// Validates and cleans a decision's line comments (slice F): at most <see cref="MaxLineComments"/>,
    /// each naming a Git path, a line from 1 to <see cref="MaxLineCommentLine"/> on <c>before</c> or
    /// <c>after</c>, and text of 1 to <see cref="MaxLineCommentChars"/> characters. The text is kept as
    /// typed except that CRLF becomes LF and every other control, format or separator character becomes
    /// a space (newline and tab stay), so a reviewer's remark cannot repaint a screen or hide text
    /// wherever it is shown. Null when there are none.
    /// </summary>
    internal static IReadOnlyList<ReviewLineComment>? NormalizeLineComments(IReadOnlyList<ReviewLineComment>? comments)
    {
        if (comments is null || comments.Count == 0) return null;
        if (LineCommentsProblem(comments) is { } problem) throw Invalid(problem);
        var result = new List<ReviewLineComment>(comments.Count);
        foreach (var comment in comments)
            result.Add(comment with { Side = comment.Side.ToLowerInvariant(), Text = CleanLineCommentText(comment.Text) });
        return result;
    }

    /// <summary>
    /// Why a decision's line comments are not acceptable as a whole, or null: more than <see cref="MaxLineComments"/>,
    /// one of them malformed (<see cref="LineCommentProblem"/>), or more than <see cref="MaxLineCommentTotalChars"/>
    /// characters of paths and text together. The transport refuses a hosted answer that carries such a list.
    /// </summary>
    internal static string? LineCommentsProblem(IReadOnlyList<ReviewLineComment?> comments)
    {
        if (comments.Count > MaxLineComments) return $"A decision carries at most {MaxLineComments} line comments.";
        long total = 0;
        foreach (var comment in comments)
        {
            if (LineCommentProblem(comment) is { } problem) return problem;
            total += comment!.Path.Length + comment.Text.Length;
        }
        return total > MaxLineCommentTotalChars
            ? $"A decision's line comments hold at most {MaxLineCommentTotalChars} characters of paths and text together."
            : null;
    }

    /// <summary>Why one line comment is not acceptable, or null.</summary>
    internal static string? LineCommentProblem(ReviewLineComment? comment)
    {
        if (comment is null) return "A line comment is missing.";
        if (comment.Path is not { Length: > 0 and <= 4096 } || comment.Path.Contains('\0'))
            return "A line comment names a file path of 1 to 4096 characters.";
        if (comment.Line is < 1 or > MaxLineCommentLine)
            return $"A line comment names a line from 1 to {MaxLineCommentLine}.";
        if (comment.Side is null
            || !(comment.Side.Equals(ReviewLineCommentSide.Before, StringComparison.OrdinalIgnoreCase)
                 || comment.Side.Equals(ReviewLineCommentSide.After, StringComparison.OrdinalIgnoreCase)))
            return "A line comment's side is \"before\" or \"after\".";
        if (comment.Text is not { Length: <= MaxLineCommentChars } || CleanLineCommentText(comment.Text).Length == 0)
            return $"A line comment has text of 1 to {MaxLineCommentChars} characters.";
        return null;
    }

    /// <summary>CRLF to LF; every other control, format or separator character becomes a space (newline and tab stay); trimmed.</summary>
    internal static string CleanLineCommentText(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Replace("\r\n", "\n", StringComparison.Ordinal))
        {
            if (c is '\n' or '\t') builder.Append(c);
            else if (char.IsControl(c) || CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.Format
                     or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator) builder.Append(' ');
            else builder.Append(c);
        }
        return builder.ToString().Trim();
    }

    /// <summary>The stored line comments of a decision row, or null for a row without any or one this build cannot read.</summary>
    internal static IReadOnlyList<ReviewLineComment>? ReadLineComments(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            var list = JsonSerializer.Deserialize(json, ReviewRequestJsonContext.Default.IReadOnlyListReviewLineComment);
            return list is { Count: > 0 } ? list : null;
        }
        catch (JsonException)
        {
            return null;
        }
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
                : null,
            // Without a reference this says a send began and its answer was lost (VIBE-212): the card
            // shows the request as unconfirmed rather than unsent, and Delete refuses it meanwhile.
            record.SendAttemptedUTC);

    private static ReviewDecisionDto ToDto(ReviewDecisionRecord decision) =>
        new(decision.Id, decision.Reviewer, decision.Decision, decision.Comment,
            decision is { KeyId: { } key, Fingerprint: { } fingerprint, TreeId: { } tree, SignatureBase64: { } signature }
                ? CommitSignatureFormat.Format(new CommitSignature(key, fingerprint, tree, signature))
                : null,
            decision.DecidedUTC, decision.VerifiedUTC, ReadLineComments(decision.LineCommentsJson));

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
