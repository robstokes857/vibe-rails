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

public partial interface IBoardStore
{
    Task<BoardContextSettingsRecord?> GetContextSettingsAsync(string projectPath, string boardId, CancellationToken cancellationToken = default);
    Task<BoardContextSettingsRecord?> SaveContextSettingsAsync(string projectPath, string boardId, BoardContextSettings context, int expectedRevision, CancellationToken cancellationToken = default);
}
