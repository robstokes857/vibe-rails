using VibeRails.Utils;
using VibeRails.Services.Board;

namespace VibeRails.Services.Terminal;

/// <summary>
/// User-written context as one system of scopes (VIBE-229), broadest first: App (every LLM launch),
/// Board (launches for one board, stored as Every Context), Card (agents opened from any card on the
/// board, the Agent context default message) and Card type (a type's override). App and Board context
/// follow the launch's Vibe Board nudge; Card and Card type context always reach card agents.
/// Card prompts, plain launches, the context estimate and get_board_card all render through
/// <see cref="Format"/>, so each shows the same labelled block.
/// </summary>
public static class ContextLevels
{
    public const string AppKey = "appContext";
    public const string BoardKey = "boardContext";
    public const string CardKey = "cardContext";
    public const string CardTypeKey = "cardTypeContext";
    internal const string Heading = "Context supplied by the user, broadest first:";

    /// <summary>One level as stored; <see cref="Format"/> sanitizes it and leaves it out when empty.</summary>
    public sealed record Level(string Key, string Label, string Message);

    /// <summary>The Nudge switch that governs App and Board context: base launches or saved environments.</summary>
    public static bool NudgeEnabled(Settings settings, bool savedEnvironment) =>
        savedEnvironment ? settings.CreateVibeStoryTrackingCustomEnvs : settings.CreateVibeStoryTracking;

    /// <summary>
    /// The labelled block, or "" when every level is empty. Text is sanitized like card text because a
    /// card prompt goes through the placeholder pass; the other launch paths use the same rule so the
    /// agent reads the same block wherever it comes from.
    /// </summary>
    public static string Format(IEnumerable<Level> levels)
    {
        var parts = levels
            .Select(level => (level.Label, Text: BoardPromptComposer.Sanitize(level.Message, BoardService.MaxContextMessageLength)))
            .Where(level => level.Text.Length > 0)
            .Select(level => level.Label + ":\n" + level.Text)
            .ToList();
        return parts.Count == 0 ? "" : Heading + "\n\n" + string.Join("\n\n", parts);
    }

    /// <summary>The block placed before a launch's own prompt.</summary>
    internal static string Prefix(IEnumerable<Level> levels)
    {
        var block = Format(levels);
        return block.Length == 0 ? "" : block + "\n\n";
    }

    /// <summary>The non-empty levels, for measuring each one.</summary>
    public static IReadOnlyList<Level> NonEmpty(IEnumerable<Level> levels) =>
        levels.Where(level => BoardPromptComposer.Sanitize(level.Message, BoardService.MaxContextMessageLength).Length > 0).ToList();

    /// <summary>
    /// App context, then the Board context of <paramref name="boardId"/>. Without a board, the
    /// project's first board is used, as Every Context always did; without a project, App context alone.
    /// </summary>
    internal static async Task<IReadOnlyList<Level>> ResolveNudgedAsync(IBoardStore store, string? projectPath, string? boardId, CancellationToken cancellationToken)
    {
        var levels = new List<Level> { new(AppKey, "App context", (await store.GetAppContextSettingsAsync(cancellationToken)).Message) };
        if (string.IsNullOrWhiteSpace(projectPath)) return levels;
        if (string.IsNullOrWhiteSpace(boardId))
            boardId = (await store.GetLocalBoardsAsync(cancellationToken))
                .Where(board => ProjectPathComparer.Matches(board.ProjectPath, projectPath))
                .OrderBy(board => board.Position).FirstOrDefault()?.Id;
        if (boardId is not null && await store.GetEveryContextSettingsAsync(projectPath, boardId, cancellationToken) is { } board)
            levels.Add(new(BoardKey, "Board context", board.Message));
        return levels;
    }

    /// <summary>
    /// Card context and Card type context from the board's saved settings. A type's mode keeps its
    /// meaning: default sends the card context, replace sends the type's message instead, append sends both.
    /// </summary>
    internal static IReadOnlyList<Level> ForCard(BoardContextSettings? settings, string cardType)
    {
        if (settings is null) return [];
        var selected = settings.TypeOverrides.FirstOrDefault(item => item.Type == cardType);
        var levels = new List<Level>();
        if (selected?.Mode != "replace")
            levels.Add(new(CardKey, "Card context", settings.DefaultMessage));
        if (selected?.Mode is "replace" or "append")
            levels.Add(new(CardTypeKey, $"Card type context ({BoardCardTypes.Label(cardType)})", selected.Message));
        return levels;
    }

    /// <summary>
    /// App context plus the card's own Board context. A card on no board has no Board context; it
    /// never borrows the project's first board the way a launch outside a card does.
    /// </summary>
    internal static async Task<IReadOnlyList<Level>> ResolveNudgedForCardAsync(IBoardStore store, string projectPath, string? boardId, CancellationToken cancellationToken) =>
        string.IsNullOrEmpty(boardId)
            ? [new(AppKey, "App context", (await store.GetAppContextSettingsAsync(cancellationToken)).Message)]
            : await ResolveNudgedAsync(store, projectPath, boardId, cancellationToken);

    /// <summary>Every level a card agent receives: App and Board only when its launch's nudge is on.</summary>
    internal static async Task<IReadOnlyList<Level>> ResolveForCardAsync(IBoardStore store, string projectPath, string? boardId, string cardType,
        bool nudgeEnabled, CancellationToken cancellationToken)
    {
        var levels = new List<Level>();
        if (nudgeEnabled)
            levels.AddRange(await ResolveNudgedForCardAsync(store, projectPath, boardId, cancellationToken));
        if (!string.IsNullOrEmpty(boardId))
            levels.AddRange(ForCard((await store.GetContextSettingsAsync(projectPath, boardId, cancellationToken))?.Context, cardType));
        return levels;
    }

    /// <summary>
    /// The nudge that applies to a card launched with <paramref name="selection"/> (a Board picker
    /// key): a saved environment uses the Custom Envs switch, anything else the base switch.
    /// </summary>
    public static bool NudgeEnabledFor(Settings settings, string? selection) =>
        NudgeEnabled(settings, BoardSelection.TryParse(selection, out var parsed) && parsed?.IsEnvironment == true);
}
