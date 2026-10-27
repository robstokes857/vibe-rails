namespace VibeRails.Services.Board;

/// <summary>
/// A card waiting for its one post-processing pass (VIBE-185): live, not yet processed, settled in
/// the last lane of a board that has at least two lanes. It carries what the current processors
/// read, so a pass needs no per-card lookups; a processor that needs more reads it through
/// <see cref="IBoardStore"/>. <see cref="UpdatedUtc"/> is the card's activity mark when it was read:
/// a pass only applies while it is unchanged.
/// </summary>
public sealed record BoardPostProcessingCandidate(
    string CardId,
    string ProjectPath,
    string Key,
    string Type,
    string BoardId,
    string BoardName,
    string ColumnId,
    string LaneName,
    DateTime UpdatedUtc,
    int SessionCount,
    int CodeLinkCount,
    IReadOnlyList<string> CommitShas)
{
    public IReadOnlyList<BoardCodeLink> CodeLinks { get; init; } = [];
}

/// <summary>A coherent snapshot for a targeted check, independent of the run-once queue.</summary>
public sealed record BoardGhostCheck(BoardPostProcessingCandidate Card, string? GhostReason, bool IsFinished);

/// <summary>The result of an explicit, single-card landing check.</summary>
public sealed record BoardGhostCheckResponse(string Status, string Message, string? GhostReason);

/// <summary>What a card's post-processing pass decided; applied together with marking it processed.</summary>
public sealed record BoardPostProcessingResult(string? GhostReason = null);

/// <summary>Why a card shows the ghost: an agent worked on it but no code reached the default branch.</summary>
public static class BoardGhostReasons
{
    /// <summary>Sessions are attached, but no commit or GitHub link is.</summary>
    public const string NoCode = "no-code";
    /// <summary>Commits are linked, but none is on the project's default branch.</summary>
    public const string NotLanded = "not-landed";
}
