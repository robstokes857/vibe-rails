using VibeRails.DTOs;

namespace VibeRails.Services.Board;

/// <summary>
/// The one manager a board runs for its Agent-workflow cards (VIBE-52 follow-up, VIBE-204). A card
/// in Agent mode without its own <c>ManagerSelection</c> is managed by the board; a card that keeps
/// its own manager is not. A null <see cref="Selection"/> means the board has no manager.
/// <see cref="Instructions"/> are the user's standing orders for this board's manager and
/// <see cref="MaxWorkers"/> how many implementation workers it may run at once on the board.
/// </summary>
public sealed record BoardManagerSettings(string? Selection, BaseLlmOptions? BaseLlmOptions, string? Instructions = null, int MaxWorkers = BoardManagerSettings.DefaultMaxWorkers)
{
    /// <summary>One implementation worker at a time: the board's cards usually share one checkout.</summary>
    public const int DefaultMaxWorkers = 1;
    /// <summary>The most implementation workers a board manager may run at once.</summary>
    public const int MaxWorkersLimit = 4;
    /// <summary>Standing orders are user context, capped like every other context level.</summary>
    public const int MaxInstructionsLength = 4_000;

    public static BoardManagerSettings None => new(null, null);
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Selection);
}

/// <summary>Revision is zero until the board's manager has been saved once, and again after it is cleared.</summary>
public sealed record BoardManagerSettingsRecord(BoardManagerSettings Settings, int Revision);

/// <summary>A board manager launch. Liveness is the root tab host's call; this row says only that it started.</summary>
public sealed record BoardManagerSessionRecord(string SessionId, string BoardId, string? TabId, string Selection, string Cli, DateTime StartedUtc);

/// <summary>
/// One Card Log or discussion entry on a board as the manager sees it: which card, when, what kind
/// (<c>comment</c>, <c>note</c>, <c>created</c>, <c>change</c>, <c>deleted</c>), who wrote it and a
/// bounded body. The body is card text and stays untrusted data.
/// </summary>
public sealed record BoardEventRecord(DateTime Utc, string CardId, string CardKey, string? DisplayId, string LaneName,
    string Kind, string AuthorKind, string AuthorLabel, string Summary, bool IsAttention = false);

/// <summary>An Agent-workflow card on the board: managed by the board manager unless it keeps its own manager.</summary>
public sealed record BoardManagedCardRecord(BoardCardRecord Card, string LaneName, DateTime LastActivityUtc, bool OwnManager);

/// <summary>A worker session linked to a board-managed card, live or not; the host decides liveness from its tabs.</summary>
public sealed record BoardManagedWorkerSession(string SessionId, string CardId, string CardKey, string? DisplayId, string Origin, DateTime StartedUtc);

public partial interface IBoardStore
{
    /// <summary>
    /// The machine-wide "AI board manager" switch (VIBE-204), off until the user turns it on. While
    /// it is off the board manager does not exist as far as any reader is concerned: Agent cards
    /// need their own manager again and nothing launches or waits on a board manager.
    /// </summary>
    Task<bool> GetBoardManagerEnabledAsync(CancellationToken cancellationToken = default);

    /// <summary>Saves the switch and returns the stored value.</summary>
    Task<bool> SetBoardManagerEnabledAsync(bool enabled, CancellationToken cancellationToken = default);

    /// <summary>The board's manager settings; null when the board is not in the project.</summary>
    Task<BoardManagerSettingsRecord?> GetBoardManagerAsync(string projectPath, string boardId, CancellationToken cancellationToken = default);

    /// <summary>Saves (or, with a null selection, clears) the board manager under its expected revision.</summary>
    Task<BoardManagerSettingsRecord?> SaveBoardManagerAsync(string projectPath, string boardId, BoardManagerSettings settings, int expectedRevision, CancellationToken cancellationToken = default);

    /// <summary>Records a started board manager session; null when the board is not in the project.</summary>
    Task<BoardManagerSessionRecord?> RecordBoardManagerSessionAsync(string projectPath, string boardId, string sessionId, string? tabId, string selection, string cli, CancellationToken cancellationToken = default);

    /// <summary>The newest board manager launches first.</summary>
    Task<IReadOnlyList<BoardManagerSessionRecord>> GetBoardManagerSessionsAsync(string projectPath, string boardId, int limit = 10, CancellationToken cancellationToken = default);

    /// <summary>
    /// Card Log and discussion entries on the board strictly after <paramref name="sinceUtc"/>, oldest
    /// first. With <paramref name="managedOnly"/> only the cards the board manager owns count, plus any
    /// card's workflow or manager change, so a card leaving the manager's hands still wakes it. Entries
    /// written by <paramref name="excludeSessionId"/> (the caller's own writes) are left out. A full
    /// batch never ends inside a group of entries that share a timestamp, and a batch that is one
    /// timestamp throughout is returned whole, so the timestamp cursor never skips an entry.
    /// </summary>
    Task<IReadOnlyList<BoardEventRecord>> GetBoardEventsAsync(string projectPath, string boardId, DateTime sinceUtc, int limit = 100,
        bool managedOnly = false, string? excludeSessionId = null, CancellationToken cancellationToken = default);

    /// <summary>Live Agent-workflow cards on the board in lane order, flagged first within a lane.</summary>
    Task<IReadOnlyList<BoardManagedCardRecord>> GetManagedCardsAsync(string projectPath, string boardId, CancellationToken cancellationToken = default);

    /// <summary>Worker sessions (origins <c>agent_worker</c> and <c>agent_worker_planning</c>) linked to the board's board-managed cards.</summary>
    Task<IReadOnlyList<BoardManagedWorkerSession>> GetManagedWorkerSessionsAsync(string projectPath, string boardId, CancellationToken cancellationToken = default);
}
