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
    string? GradeSummary = null)
{
    /// <summary><see cref="MinimumGradeResult"/>: the grade met the minimum.</summary>
    public const string GradeMet = "Met";
    /// <summary><see cref="MinimumGradeResult"/>: the grade was below the minimum.</summary>
    public const string GradeNotMet = "Not met";
    /// <summary><see cref="MinimumGradeResult"/>: no grade could be established (no linked code, unreadable source).</summary>
    public const string GradeNotChecked = "Not checked";

    /// <summary>Whether the action fails: the check could not run, or a Code quality minimum grade was not
    /// met or could not be checked (VIBE-278). Without a minimum, findings stay advisory.</summary>
    [JsonIgnore]
    public bool FailsAction => Status is "Failed to run" or "Cancelled" || MinimumGradeResult is GradeNotMet or GradeNotChecked;
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
