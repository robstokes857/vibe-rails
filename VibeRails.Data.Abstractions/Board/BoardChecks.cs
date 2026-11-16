using System.Text.Json.Serialization;

namespace VibeRails.Services.Board;

/// <summary>One check attempt. Results are immutable after completion and survive reruns.</summary>
public sealed record BoardCheckRecord(
    string Id, string CardId, string RunId, string ActionId, string Tool, string Status,
    string Scope, string? BaseCommit, string? HeadCommit, string? SnapshotHash,
    string? RulesHash, string ToolVersion, DateTime StartedUtc, DateTime? EndedUtc,
    string Summary, int FileCount, int AnalyzedCount, int SkippedCount, int FindingCount,
    IReadOnlyList<string> Limitations, string? ResultJson = null,
    string? WorkspacePath = null, IReadOnlyList<string>? ScopeFiles = null,
    BoardCheckEvidence? Evidence = null, string? MinimumGrade = null, string? MinimumGradeResult = null,
    string? GradeSummary = null, IReadOnlyList<string>? UnlinkedCommits = null,
    IReadOnlyList<BoardCheckSkippedFile>? NotReviewed = null, int NotReviewedOmitted = 0)
{
    /// <summary>At most this many <see cref="NotReviewed"/> files are named; the rest are counted.</summary>
    public const int NotReviewedLimit = 200;

    /// <summary><see cref="MinimumGradeResult"/>: the grade met the minimum.</summary>
    public const string GradeMet = "Met";
    /// <summary><see cref="MinimumGradeResult"/>: the grade was below the minimum.</summary>
    public const string GradeNotMet = "Not met";
    /// <summary><see cref="MinimumGradeResult"/>: no grade could be established (no linked code, unreadable source).</summary>
    public const string GradeNotChecked = "Not checked";

    /// <summary>Whether the action fails: the check could not run, a Code quality minimum grade was not
    /// met or could not be checked (VIBE-278), or a Code quality card check found no linked code.
    /// Otherwise findings stay advisory.</summary>
    [JsonIgnore]
    public bool FailsAction => Status is "Failed to run" or "Cancelled" || MinimumGradeResult is GradeNotMet or GradeNotChecked
        || NoCodeFailsCodeQuality || UnlinkedCommitsFailCodeQuality;

    /// <summary>
    /// Owner decision 2026-11-16 (VIBE-338): a card-scoped Code quality check also fails when a
    /// commit one of the card's own sessions made is not linked to the card, and names it.
    /// </summary>
    [JsonIgnore]
    public bool UnlinkedCommitsFailCodeQuality => Tool == "Code quality" && UnlinkedCommits is { Count: > 0 };

    /// <summary>Why the step failed, in the words the step error and the agent read.</summary>
    [JsonIgnore]
    public string? FailureReason => !FailsAction ? null
        : UnlinkedCommitsFailCodeQuality ? UnlinkedCommitsSentence(UnlinkedCommits!)
        : NoCodeFailsCodeQuality && MinimumGradeResult is null ? NoLinkedCommitsSentence
        : MinimumGradeResult is GradeNotMet or GradeNotChecked ? GradeSummary ?? Summary
        : Summary;

    /// <summary>The step error when a card has no linked commits.</summary>
    public const string NoLinkedCommitsSentence =
        "No commits are linked to this card. Link them with link_board_commit, then retry the step.";

    /// <summary>The step error naming commits the card's sessions made that are not linked.</summary>
    public static string UnlinkedCommitsSentence(IReadOnlyList<string> commits) =>
        $"{(commits.Count == 1 ? "A commit" : $"{commits.Count} commits")} made from this card's sessions "
        + $"{(commits.Count == 1 ? "is" : "are")} not linked to it: {string.Join("; ", commits.Take(10))}"
        + (commits.Count > 10 ? $"; and {commits.Count - 10} more" : "")
        + ". Link them with link_board_commit, then retry the step.";

    /// <summary>
    /// Owner decision 2026-11-16: a card-scoped Code quality check with no linked code fails its step,
    /// minimum grade or not, so an agent that forgets <c>link_board_commit</c> cannot pass the lane.
    /// "Needs review" is only ever the no-code outcome of a card check; VCA keeps it advisory.
    /// </summary>
    [JsonIgnore]
    public bool NoCodeFailsCodeQuality => Status == "Needs review" && Tool == "Code quality";
}

/// <summary>The captured text used by a check, kept separately from its summary. Impact sources
/// whose text equals a scope file's captured <see cref="BoardCheckFile.Content"/> are stored once,
/// in <see cref="Files"/>: the impact corpus is <see cref="ImpactFiles"/> plus those files.</summary>
public sealed record BoardCheckEvidence(
    IReadOnlyList<BoardCheckFile> Files,
    IReadOnlyList<BoardCheckTextFile> RuleFiles,
    IReadOnlyList<BoardCheckTextFile>? ImpactFiles,
    IReadOnlyList<VibeRails.DB.CodeAnalyzerIgnoredFile>? IgnoreRules,
    IReadOnlyList<string>? CommitShas = null);

/// <summary>One scope file, including full text and the exact added-code input when available.</summary>
public sealed record BoardCheckFile(string Path, string? PreviousPath, string ChangeKind,
    bool IsBinary, string? Content, string? PreviousContent, string? AddedContent,
    IReadOnlyList<int>? AddedLineNumbers);

/// <summary>A scope file the check did not analyse, and why (VIBE-338).</summary>
public sealed record BoardCheckSkippedFile(string Path, string Reason);

/// <summary>A captured rule or impact-ranking source.</summary>
public sealed record BoardCheckTextFile(string Path, string Content);

public partial interface IBoardStore
{
    /// <summary>Insert an attempt or complete an unfinished attempt, scoped to a live card. A null
    /// <see cref="BoardCheckRecord.Evidence"/> keeps the evidence an earlier save stored for the attempt.</summary>
    Task<bool> SaveCheckAsync(string projectPath, BoardCheckRecord check, CancellationToken cancellationToken = default);
    /// <summary>Newest summaries first; full evidence is loaded separately. No historical rows are removed.</summary>
    Task<IReadOnlyList<BoardCheckRecord>> GetChecksAsync(string projectPath, string cardId, int offset = 0, CancellationToken cancellationToken = default);
    /// <summary>Latest attempt of each tool, independent of history pagination.</summary>
    Task<IReadOnlyList<BoardCheckRecord>> GetLatestChecksAsync(string projectPath, string cardId, CancellationToken cancellationToken = default);
    /// <summary>One attempt with its engine result, through the same project/card boundary. Captured
    /// source text can run to tens of megabytes, so <see cref="BoardCheckRecord.Evidence"/> stays null
    /// here; <see cref="GetCheckEvidenceAsync"/> reads it.</summary>
    Task<BoardCheckRecord?> GetCheckAsync(string projectPath, string cardId, string checkId, CancellationToken cancellationToken = default);
    /// <summary>The captured inputs of one attempt, or null when the attempt or its evidence is absent.</summary>
    Task<BoardCheckEvidence?> GetCheckEvidenceAsync(string projectPath, string cardId, string checkId, CancellationToken cancellationToken = default);
}
