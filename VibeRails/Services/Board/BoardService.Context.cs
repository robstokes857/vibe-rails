using VibeRails.DTOs;

namespace VibeRails.Services.Board;

public partial interface IBoardService
{
    Task<BoardContextSettingsRecord?> GetContextSettingsAsync(string projectPath, string boardId, CancellationToken cancellationToken = default);
    Task<BoardContextSettingsRecord?> SaveContextSettingsAsync(string projectPath, string boardId, UpdateBoardContextRequest request, CancellationToken cancellationToken = default);
}

public sealed partial class BoardService
{
    public const int MaxContextMessageLength = 4_000;

    public Task<BoardContextSettingsRecord?> GetContextSettingsAsync(string projectPath, string boardId, CancellationToken cancellationToken = default) =>
        store.GetContextSettingsAsync(projectPath, boardId, cancellationToken);

    public Task<BoardContextSettingsRecord?> SaveContextSettingsAsync(string projectPath, string boardId, UpdateBoardContextRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Context is null || request.ExpectedRevision is null or < 0)
            throw new BoardValidationException("Context and its expected revision are required.");
        var context = request.Context;
        var defaultMessage = NormalizeContextMessage(context.DefaultMessage);
        if (context.TypeOverrides is null || context.TypeOverrides.Count > BoardCardTypes.All.Count)
            throw new BoardValidationException("Provide at most one context override for each card type.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var overrides = new List<BoardTypeContext>();
        foreach (var item in context.TypeOverrides)
        {
            if (item is null || !BoardCardTypes.IsValid(item.Type) || !seen.Add(item.Type))
                throw new BoardValidationException("Context overrides must use distinct supported card types.");
            if (item.Mode is not ("default" or "replace" or "append"))
                throw new BoardValidationException("Context mode must be default, replace, or append.");
            overrides.Add(item with { Message = NormalizeContextMessage(item.Message) });
        }
        return store.SaveContextSettingsAsync(projectPath, boardId, new(defaultMessage, overrides), request.ExpectedRevision.Value, cancellationToken);
    }

    private static string NormalizeContextMessage(string? text)
    {
        text = (text ?? "").Trim();
        if (text.Length > MaxContextMessageLength)
            throw new BoardValidationException($"Each board context message must be {MaxContextMessageLength:N0} characters or fewer.");
        return text;
    }
}
