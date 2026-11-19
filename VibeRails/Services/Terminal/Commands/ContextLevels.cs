using VibeRails.Utils;
using VibeRails.Services.Board;
using VibeRails.Services.Git;

namespace VibeRails.Services.Terminal;

/// <summary>
/// User-written context from App, Project, Board, Card and Card type, broadest first.
/// App and Board context follow the launch's Vibe Board nudge. Project context follows
/// the execution repository and the card levels always reach card agents.
/// Card prompts, plain launches, the context estimate and get_board_card all render through
/// <see cref="Format"/>, so each shows the same labelled block.
/// </summary>
public static class ContextLevels
{
    public const string AppKey = "appContext";
    public const string ProjectKey = "projectContext";
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

    /// <summary>Adds context to the starting prompt. Context alone leaves the agent waiting for work.</summary>
    internal static string? PrependToPrompt(IEnumerable<Level> levels, string? prompt, bool hasUserMessage = false)
    {
        var block = Format(levels);
        if (block.Length == 0) return prompt;
        if (!hasUserMessage && string.IsNullOrWhiteSpace(prompt))
            prompt = "Reply only that you're ready to help, then wait for the user to give you a task.";
        return block + "\n\n" + prompt;
    }

    /// <summary>The non-empty levels, for measuring each one.</summary>
    public static IReadOnlyList<Level> NonEmpty(IEnumerable<Level> levels) =>
        levels.Where(level => BoardPromptComposer.Sanitize(level.Message, BoardService.MaxContextMessageLength).Length > 0).ToList();

    /// <summary>Finds the Git project for a launch that has no recorded project scope.</summary>
    internal static async Task<string?> FindLaunchRepositoryAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        var root = ParserConfigs.GetRootPath();
        if (ParserConfigs.GetIsInGit() && ProjectPathComparer.Matches(root, workingDirectory))
            return root;
        if (!File.Exists(PathExecutable.Resolve("git"))) return null;

        var result = await GitCli.RunAsync(workingDirectory, ["rev-parse", "--show-toplevel"], cancellationToken,
            TimeSpan.FromSeconds(5), maxOutputChars: 32_768);
        return result.Succeeded && !string.IsNullOrWhiteSpace(result.StdOut) ? result.StdOut.Trim() : null;
    }

    /// <summary>
    /// Resolves the levels shared by managed launches. An ordinary launch can fall back to the
    /// first board, whereas a card without a board only receives the applicable wider levels.
    /// </summary>
    internal static async Task<IReadOnlyList<Level>> ResolveForLaunchAsync(IBoardStore store, string? projectPath,
        string? boardId, bool nudgeEnabled, CancellationToken cancellationToken, string? boardProjectPath = null, bool useFirstBoard = true)
    {
        var levels = new List<Level>();
        if (nudgeEnabled)
            levels.Add(new(AppKey, "App context", (await store.GetAppContextSettingsAsync(cancellationToken)).Message));
        if (!string.IsNullOrWhiteSpace(projectPath) &&
            await store.GetProjectContextSettingsAsync(projectPath, cancellationToken) is { Message.Length: > 0 } project)
            levels.Add(new(ProjectKey, "Project context", project.Message));

        boardProjectPath ??= projectPath;
        if (!nudgeEnabled || string.IsNullOrWhiteSpace(boardProjectPath)) return levels;
        if (useFirstBoard && string.IsNullOrWhiteSpace(boardId))
            boardId = (await store.GetLocalBoardsAsync(cancellationToken))
                .Where(board => ProjectPathComparer.Matches(board.ProjectPath, boardProjectPath))
                .OrderBy(board => board.Position).FirstOrDefault()?.Id;
        if (!string.IsNullOrWhiteSpace(boardId) &&
            await store.GetEveryContextSettingsAsync(boardProjectPath, boardId, cancellationToken) is { } board)
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

    /// <summary>Every applicable level a card agent receives.</summary>
    internal static async Task<IReadOnlyList<Level>> ResolveForCardAsync(IBoardStore store, string projectPath, string? boardId, string cardType,
        bool nudgeEnabled, CancellationToken cancellationToken)
    {
        var levels = await ResolveForLaunchAsync(store, projectPath, boardId, nudgeEnabled, cancellationToken, useFirstBoard: false);
        if (string.IsNullOrEmpty(boardId)) return levels;
        return [.. levels, .. ForCard((await store.GetContextSettingsAsync(projectPath, boardId, cancellationToken))?.Context, cardType)];
    }

    /// <summary>
    /// The nudge that applies to a card launched with <paramref name="selection"/> (a Board picker
    /// key): a saved environment uses the Custom Envs switch, anything else the base switch.
    /// </summary>
    public static bool NudgeEnabledFor(Settings settings, string? selection) =>
        NudgeEnabled(settings, BoardSelection.TryParse(selection, out var parsed) && parsed?.IsEnvironment == true);
}
