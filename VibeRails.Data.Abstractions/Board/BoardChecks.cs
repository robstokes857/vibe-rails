namespace VibeRails.Services.Board;

/// <summary>One check attempt. Results are immutable after completion and survive reruns.</summary>
public sealed record BoardCheckRecord(
    string Id, string CardId, string RunId, string ActionId, string Tool, string Status,
    string Scope, string? BaseCommit, string? HeadCommit, string? SnapshotHash,
    string? RulesHash, string ToolVersion, DateTime StartedUtc, DateTime? EndedUtc,
    string Summary, int FileCount, int AnalyzedCount, int SkippedCount, int FindingCount,
    IReadOnlyList<string> Limitations, string? ResultJson = null,
    string? WorkspacePath = null, IReadOnlyList<string>? ScopeFiles = null,
    BoardCheckEvidence? Evidence = null);

/// <summary>The captured text used by a check, kept separately from its summary.</summary>
public sealed record BoardCheckEvidence(
    IReadOnlyList<BoardCheckFile> Files,
    IReadOnlyList<BoardCheckTextFile> RuleFiles,
    IReadOnlyList<BoardCheckTextFile>? ImpactFiles,
    IReadOnlyList<VibeRails.DB.CodeAnalyzerIgnoredFile>? IgnoreRules);

/// <summary>One scope file, including full text and the exact added-code input when available.</summary>
public sealed record BoardCheckFile(string Path, string? PreviousPath, string ChangeKind,
    bool IsBinary, string? Content, string? PreviousContent, string? AddedContent,
    IReadOnlyList<int>? AddedLineNumbers);

/// <summary>A captured rule or impact-ranking source.</summary>
public sealed record BoardCheckTextFile(string Path, string Content);

public partial interface IBoardStore
{
    /// <summary>Insert an attempt or complete an unfinished attempt, scoped to a live card.</summary>
    Task<bool> SaveCheckAsync(string projectPath, BoardCheckRecord check, CancellationToken cancellationToken = default);
    /// <summary>Newest summaries first; full evidence is loaded separately. No historical rows are removed.</summary>
    Task<IReadOnlyList<BoardCheckRecord>> GetChecksAsync(string projectPath, string cardId, int offset = 0, CancellationToken cancellationToken = default);
    /// <summary>Latest attempt of each tool, independent of history pagination.</summary>
    Task<IReadOnlyList<BoardCheckRecord>> GetLatestChecksAsync(string projectPath, string cardId, CancellationToken cancellationToken = default);
    /// <summary>Read full evidence through the same project/card boundary.</summary>
    Task<BoardCheckRecord?> GetCheckAsync(string projectPath, string cardId, string checkId, CancellationToken cancellationToken = default);
}
