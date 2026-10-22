namespace VibeRails.Services.Board;

public partial interface IBoardStore
{
    /// <summary>
    /// Live cards across every local project that still need their one post-processing pass: in the
    /// last lane of a board with at least two lanes and unchanged since <paramref name="settledBeforeUtc"/>.
    /// Cards that existed before post-processing, or that an older binary created, are never returned.
    /// </summary>
    Task<IReadOnlyList<BoardPostProcessingCandidate>> GetPostProcessingCandidatesAsync(DateTime settledBeforeUtc, int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks the card processed and applies <paramref name="result"/> in one write, only while it is
    /// still unprocessed, live, in the candidate's lane and unchanged since it was read. False means
    /// another root got there first or the card moved on; a later pass sees it again if it qualifies.
    /// </summary>
    Task<bool> CompletePostProcessingAsync(BoardPostProcessingCandidate candidate, BoardPostProcessingResult result,
        CancellationToken cancellationToken = default);
}
