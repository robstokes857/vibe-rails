using VibeRails.DTOs;

namespace VibeRails.Services.Board;

/// <summary>
/// The AI board manager (VIBE-52 follow-up, VIBE-204): the machine-wide switch, each board's manager
/// settings, its status, launch and stop. Root-backend only, like the launch service it wraps: the
/// live checks read this root's tabs. While the switch is off the board manager does not exist: the
/// status says so and carries nothing else, and saving or starting one is refused with directions to
/// the switch. Validation matches the card editor's manager picker, so REST and the card path agree
/// on what a manager selection may be.
/// </summary>
public sealed class BoardManagerService(IBoardStore store, IBoardLaunchService launcher)
{
    /// <summary>The one refusal every manager operation gives while the switch is off.</summary>
    public const string DisabledMessage = "The AI board manager is off. Turn it on in Settings > AI board manager.";

    public async Task<BoardManagerFeatureResponse> GetFeatureAsync(CancellationToken cancellationToken = default) =>
        new(await store.GetBoardManagerEnabledAsync(cancellationToken));

    /// <summary>Turning it off stops nothing that is running: a live manager is told by await_board_events and winds down.</summary>
    public async Task<BoardManagerFeatureResponse> SetFeatureAsync(UpdateBoardManagerFeatureRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Enabled is not { } enabled)
            throw new BoardValidationException("enabled is required.");
        return new(await store.SetBoardManagerEnabledAsync(enabled, cancellationToken));
    }

    public async Task<BoardManagerResponse?> GetAsync(string projectPath, string boardId, CancellationToken cancellationToken = default)
    {
        var record = await store.GetBoardManagerAsync(projectPath, boardId, cancellationToken);
        if (record is null) return null;
        if (!await store.GetBoardManagerEnabledAsync(cancellationToken)) return Disabled;
        return await ToResponseAsync(projectPath, boardId, record, cancellationToken);
    }

    public async Task<BoardManagerResponse?> SaveAsync(string projectPath, string boardId, UpdateBoardManagerRequest request, CancellationToken cancellationToken = default)
    {
        if (request.ExpectedRevision is null)
            throw new BoardValidationException("expectedRevision is required.");
        var current = await store.GetBoardManagerAsync(projectPath, boardId, cancellationToken);
        if (current is null) return null;
        if (!await store.GetBoardManagerEnabledAsync(cancellationToken))
            throw new BoardConflictException(DisabledMessage);
        var selection = BoardService.NormalizeAssignee(request.Selection);
        BaseLlmOptions? options = null;
        if (selection is not null)
        {
            if (!BoardSelection.TryParse(selection, out var parsed) || parsed is null)
                throw new BoardValidationException("Choose a manager agent from the list.");
            if (request.BaseLlmOptions is not null)
            {
                if (parsed.IsEnvironment)
                    throw new BoardValidationException("Manager base options require a base CLI manager. Saved environments use their own launch settings.");
                try { options = LlmClis.BaseLlmOptionsBuilder.Normalize(parsed.Llm, request.BaseLlmOptions); }
                catch (ArgumentException ex) { throw new BoardValidationException(ex.Message); }
            }
            // As on cards: omitted options stay while the manager is unchanged, and go with it otherwise.
            else if (string.Equals(selection, current.Settings.Selection, StringComparison.Ordinal))
                options = current.Settings.BaseLlmOptions;
        }
        // Null keeps what is saved (the store still checks the revision, so a stale editor conflicts).
        var instructions = request.Instructions ?? current.Settings.Instructions;
        if (instructions?.Length > BoardManagerSettings.MaxInstructionsLength)
            throw new BoardValidationException($"Manager instructions are limited to {BoardManagerSettings.MaxInstructionsLength:N0} characters.");
        var maxWorkers = request.MaxWorkers ?? current.Settings.MaxWorkers;
        if (maxWorkers is < 1 or > BoardManagerSettings.MaxWorkersLimit)
            throw new BoardValidationException($"Workers at a time must be between 1 and {BoardManagerSettings.MaxWorkersLimit}.");
        var saved = await store.SaveBoardManagerAsync(projectPath, boardId, new BoardManagerSettings(selection, options, instructions, maxWorkers),
            request.ExpectedRevision.Value, cancellationToken);
        return saved is null ? null : await ToResponseAsync(projectPath, boardId, saved, cancellationToken);
    }

    public async Task<LaunchBoardManagerResponse?> LaunchAsync(string projectPath, string boardId, CancellationToken cancellationToken = default)
    {
        if (await store.GetBoardAsync(projectPath, boardId, cancellationToken) is null) return null;
        if (!await store.GetBoardManagerEnabledAsync(cancellationToken))
            throw new BoardConflictException(DisabledMessage);
        return await launcher.LaunchBoardManagerAsync(projectPath, boardId, cancellationToken);
    }

    /// <summary>Allowed with the switch off as well: closing a manager that is still running is always safe.</summary>
    public Task<StopBoardManagerResponse?> StopAsync(string projectPath, string boardId, CancellationToken cancellationToken = default) =>
        launcher.StopBoardManagerAsync(projectPath, boardId, cancellationToken);

    private static BoardManagerResponse Disabled { get; } = new(null, null, 0, null, 0, 0, Enabled: false);

    private async Task<BoardManagerResponse> ToResponseAsync(string projectPath, string boardId, BoardManagerSettingsRecord record, CancellationToken cancellationToken)
    {
        var live = await launcher.GetBoardManagerLiveStateAsync(projectPath, boardId, cancellationToken);
        var cards = await store.GetManagedCardsAsync(projectPath, boardId, cancellationToken);
        var settings = record.Settings;
        return new BoardManagerResponse(BoardService.PresentAssignee(settings.Selection), settings.BaseLlmOptions, record.Revision, live.Active,
            cards.Count(card => !card.OwnManager), cards.Count(card => card.OwnManager),
            Enabled: true, Instructions: settings.Instructions, MaxWorkers: settings.MaxWorkers, RunningWorkers: live.Workers.Count,
            FlaggedCards: cards.Count(card => !card.OwnManager && card.Card.Flagged));
    }
}
