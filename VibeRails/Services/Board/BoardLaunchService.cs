using System.Collections.Concurrent;
using Serilog;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services.Terminal;
using VibeRails.Utils;

namespace VibeRails.Services.Board;

/// <summary>"Start work": open a web terminal tab for a card with the card prepended to the environment's Initial Message.</summary>
public interface IBoardLaunchService
{
    Task<LaunchBoardCardResponse?> LaunchAsync(string projectPath, string idOrKey, string? selectionOverride, CancellationToken cancellationToken = default, string intent = "work");
}

/// <summary>
/// Root-backend only (it needs the in-process tab host). Copies the create/start/cleanup shape of
/// <c>TerminalTabHostService.CreatePythonScriptTabAsync</c>. The composed prompt is handed to the
/// tab child as <c>StartTerminalRequest.InitialPrompt</c>: TerminalRoutes prefers a request prompt
/// over the environment's, so the combined TEMPLATE reaches the one PromptPlaceholderService pass
/// unchanged — the once-only resolution invariant holds and no prompt plumbing changes.
///
/// TODO(board): "Auto Launch" — an option on the card (and/or a lane) that runs this automatically
/// when an assigned card lands in that lane. Not built yet; plan 2026-09-09.
/// </summary>
public sealed class BoardLaunchService(
    IBoardStore store,
    IRepository repository,
    ITerminalTabHostService tabHost) : IBoardLaunchService
{
    // Only in-flight launches are retained. TryAdd is an immediate reservation, not a
    // queued semaphore, so removal cannot strand waiters or create two gates for one card.
    private static readonly ConcurrentDictionary<string, byte> LaunchingCards = new(StringComparer.Ordinal);

    public async Task<LaunchBoardCardResponse?> LaunchAsync(string projectPath, string idOrKey, string? selectionOverride, CancellationToken cancellationToken = default, string intent = "work")
    {
        if (intent is not ("work" or "chat"))
            throw new BoardValidationException("Launch intent must be work or chat.");
        var card = await store.FindCardAsync(projectPath, idOrKey, cancellationToken);
        if (card is null) return null;
        // Card ids are globally unique in the shared database; VB numbers are project-local.
        if (!LaunchingCards.TryAdd(card.Id, 0))
            throw new BoardConflictException("An agent is already starting on this card. Wait for it to finish starting.");
        try
        {
            // Re-read after the reservation: fields may have changed while resolving the key.
            return await LaunchCoreAsync(projectPath, card.Id, selectionOverride, cancellationToken, intent);
        }
        finally { LaunchingCards.TryRemove(card.Id, out _); }
    }

    private async Task<LaunchBoardCardResponse?> LaunchCoreAsync(string projectPath, string idOrKey, string? selectionOverride, CancellationToken cancellationToken, string intent)
    {
        var card = await store.FindCardAsync(projectPath, idOrKey, cancellationToken);
        if (card is null)
            return null;

        var selectionText = string.IsNullOrWhiteSpace(selectionOverride) ? card.Assignee : selectionOverride;
        if (string.IsNullOrWhiteSpace(selectionText))
            throw new BoardValidationException("Assign an LLM to this card first, or pick one to start with.");
        if (!BoardSelection.TryParse(selectionText, out var parsed) || parsed is null)
            throw new BoardValidationException("That assignee is not a launchable LLM selection.");

        LLM_Environment? environment = null;
        if (parsed.IsEnvironment)
        {
            // Same discipline as EnvironmentLaunchService.ResolveEnvironmentAsync: resolve by id,
            // require the provider to still match, and never fall back to a display name.
            var byId = await repository.GetEnvironmentByIdAsync(parsed.EnvironmentId!.Value, cancellationToken);
            environment = byId is not null && byId.LLM == parsed.Llm ? byId : null;
            if (environment is null)
                throw new BoardValidationException("The assigned environment no longer exists (or changed CLI). Pick another assignee.");
            if (!ProjectPathComparer.IsVisibleIn(environment.ProjectPath, projectPath))
                throw new BoardValidationException("The assigned environment belongs to another project.");
        }

        var boardId = string.IsNullOrEmpty(card.BoardId) ? null : card.BoardId;
        var columns = await store.GetColumnsAsync(projectPath, cancellationToken, boardId);
        var column = columns.FirstOrDefault(c => c.Id == card.ColumnId);
        var boardName = boardId is null ? null : (await store.GetBoardAsync(projectPath, boardId, cancellationToken))?.Name;
        var boardContext = boardId is null ? null : await store.GetContextSettingsAsync(projectPath, boardId, cancellationToken);
        var assigneeLabel = environment is not null
            ? $"{environment.CustomName} ({parsed.Cli})"
            : parsed.Cli;
        var detail = await store.GetCardDetailAsync(projectPath, card.Id, cancellationToken);
        // Lanes, linked commits and attachment names ride along so the agent does not spend its
        // first minutes discovering which lane names exist or which change "the big refactor" was.
        // Lane Automations ride along too, so the agent can sequence its moves (VB-34).
        var orderedColumns = columns.OrderBy(c => c.Position).ToList();
        var laneAutomations = await BoardService.DescribeLaneAutomationsByLaneAsync(store, projectPath, orderedColumns.Select(c => c.Id).ToList(), cancellationToken);
        var context = new BoardPromptComposer.LaunchContext(
            orderedColumns.Select(c => c.Name).ToList(),
            detail?.Commits ?? [],
            detail?.Attachments ?? [],
            boardName,
            boardContext?.Context,
            orderedColumns.Select(c => (IReadOnlyList<string>)laneAutomations[c.Id].Select(a => a.Name).ToList()).ToList());
        var prompt = BoardPromptComposer.Compose(card, column?.Name ?? "(no lane)", assigneeLabel, environment?.CustomPrompt, context, intent);
        var title = $"{card.Key} · {Truncate(card.Title, 60)}";

        var tabs = await tabHost.ListTabsAsync(cancellationToken);
        var linkedSessionIds = detail?.Sessions.Select(session => session.SessionId).ToHashSet(StringComparer.Ordinal) ?? [];
        if (tabs.Any(tab => tab.HasActiveSession && tab.SessionId is not null && linkedSessionIds.Contains(tab.SessionId)))
            throw new BoardConflictException("An agent is already running on this card. Open it from Sessions.");
        if (tabs.Count >= tabHost.MaxTabs)
            throw new BoardConflictException($"All {tabHost.MaxTabs} terminal tabs are in use. Close one first.");

        TerminalTabStatusResponse? tab = null;
        try
        {
            tab = await tabHost.CreateTabAsync(cancellationToken);
            var session = await tabHost.StartSessionAsync(
                tab.TabId,
                new StartTerminalRequest(
                    WorkingDirectory: projectPath,
                    Cli: parsed.Cli,
                    EnvironmentName: environment?.CustomName,
                    Title: title,
                    InitialPrompt: prompt,
                    BaseLlmOptions: !parsed.IsEnvironment && string.Equals(parsed.Key, card.Assignee, StringComparison.Ordinal)
                        ? card.BaseLlmOptions : null,
                    AuthorizeBoardTools: true),
                cancellationToken);

            if (!string.IsNullOrWhiteSpace(session.SessionId))
            {
                try
                {
                    var linked = await store.LinkSessionAsync(projectPath, card.Id, session.SessionId!, tab.TabId, parsed.Key, parsed.Cli,
                        title, BoardSessionRecord.LaunchOrigin, cancellationToken);
                    if (linked is null)
                        throw new BoardValidationException("The card was deleted while its agent was starting. The terminal has been closed.");
                }
                catch (BoardConflictException ex)
                {
                    Log.Warning("[Board] Session {SessionId} was already linked: {Message}", session.SessionId, ex.Message);
                }
            }
            else
            {
                Log.Warning("[Board] Tab {TabId} started for {Card} without a session id; the card will not show it", tab.TabId, card.Key);
            }

            return new LaunchBoardCardResponse(tab.TabId, session.SessionId, session.Cli, session.WorkingDirectory, card.Id, card.Key, parsed.Key);
        }
        catch
        {
            if (tab is not null)
            {
                try { await tabHost.DeleteTabAsync(tab.TabId, CancellationToken.None); }
                catch (Exception ex)
                {
                    Log.Debug(ex, "[Board] Failed to clean up tab {TabId} after a failed launch", tab.TabId);
                }
            }
            throw;
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max].TrimEnd() + "…";
}
