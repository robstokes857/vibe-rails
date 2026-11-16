namespace VibeRails.Services.Board;

/// <summary>
/// A commit a VibeRails terminal session made while it was attached to a card (VIBE-338). The
/// post-commit hook records one row per card the session was attached to, then links the commit
/// to those cards. A card-scoped Code quality check names rows whose commit is still not linked.
/// </summary>
public sealed record BoardSessionCommitRecord(string Sha, string SessionId, string CardId,
    string RepositoryPath, string? Message, DateTime RecordedUtc);

/// <summary>
/// An agent's skip of one lane step, with its reason. It stays on the card's tile and rail until a
/// person dismisses it (VIBE-338, owner decision 2026-11-16: the lane carries on at once).
/// </summary>
public sealed record BoardAgentSkipRecord(string Id, string CardId, string EventId, string StepName,
    string Reason, BoardAuthor Author, DateTime CreatedUtc);

public partial interface IBoardStore
{
    /// <summary>
    /// Records that <paramref name="sessionId"/> made <paramref name="sha"/> while attached to
    /// <paramref name="cardId"/> and to every other card in the project it is attached to. Repeats
    /// keep the first row. Returns the cards recorded, or none when the card is unavailable.
    /// </summary>
    Task<IReadOnlyList<string>> RecordSessionCommitAsync(string projectPath, string cardId, string sessionId,
        string sha, string repositoryPath, string? message, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<string>>([]);

    /// <summary>Recorded commits of this card that are not linked to it and were not unlinked by a person.</summary>
    Task<IReadOnlyList<BoardSessionCommitRecord>> GetUnlinkedSessionCommitsAsync(string projectPath, string cardId,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<BoardSessionCommitRecord>>([]);

    /// <summary>
    /// Skips one current lane entry for an agent, with its reason, in one transaction: the entry is
    /// marked skipped (its report row names the session), the agent's comment is added and the
    /// skip marker is saved for the card's tile and rail. False when the entry is not current, has
    /// already passed or been skipped, or the card is unavailable.
    /// </summary>
    Task<BoardAgentSkipRecord?> SkipLaunchEventForAgentAsync(string projectPath, string cardId, string eventId,
        string stepName, string reason, BoardAuthor author, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    /// <summary>Agent skips a person has not dismissed yet, newest first, for these cards.</summary>
    Task<IReadOnlyList<BoardAgentSkipRecord>> GetOpenAgentSkipsAsync(string projectPath, IReadOnlyList<string> cardIds,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<BoardAgentSkipRecord>>([]);

    /// <summary>A person dismisses every open agent skip on the card. Returns how many were open.</summary>
    Task<int> DismissAgentSkipsAsync(string projectPath, string cardId, CancellationToken cancellationToken = default)
        => Task.FromResult(0);
}
