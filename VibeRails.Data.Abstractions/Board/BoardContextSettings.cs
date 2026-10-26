namespace VibeRails.Services.Board;

/// <summary>User-authored instructions sent to every agent launched from this board.</summary>
public sealed record BoardContextSettings(string DefaultMessage, IReadOnlyList<BoardTypeContext> TypeOverrides)
{
    public static BoardContextSettings Empty => new("", []);
}

/// <summary>Mode is default (default only), replace (type only), or append (default then type).</summary>
public sealed record BoardTypeContext(string Type, string Mode, string Message);

/// <summary>Revision is independent of card description history; zero means settings have never been saved.</summary>
public sealed record BoardContextSettingsRecord(BoardContextSettings Context, int Revision);

/// <summary>Board instructions for all Nudge-enabled CLI launches, saved independently of Agent context.</summary>
public sealed record BoardEveryContextSettingsRecord(string Message, int Revision);

public partial interface IBoardStore
{
    Task<BoardContextSettingsRecord?> GetContextSettingsAsync(string projectPath, string boardId, CancellationToken cancellationToken = default);
    Task<BoardContextSettingsRecord?> SaveContextSettingsAsync(string projectPath, string boardId, BoardContextSettings context, int expectedRevision, CancellationToken cancellationToken = default);
    /// <summary>Reads one project's board message; null for a missing or foreign board.</summary>
    Task<BoardEveryContextSettingsRecord?> GetEveryContextSettingsAsync(string projectPath, string boardId, CancellationToken cancellationToken = default);
    /// <summary>Saves Every Context with its own revision, preserving Agent context and older writers.</summary>
    Task<BoardEveryContextSettingsRecord?> SaveEveryContextSettingsAsync(string projectPath, string boardId, string message, int expectedRevision, CancellationToken cancellationToken = default);
}
