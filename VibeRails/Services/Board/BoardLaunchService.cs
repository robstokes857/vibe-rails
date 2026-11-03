using System.Collections.Concurrent;
using Serilog;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services.Terminal;
using VibeRails.Utils;

namespace VibeRails.Services.Board;

/// <summary>Open a card terminal for work, its Agent workflow manager, a delegated worker, review or discussion.</summary>
public interface IBoardLaunchService
{
    Task<LaunchBoardCardResponse?> LaunchAsync(string projectPath, string idOrKey, string? selectionOverride, CancellationToken cancellationToken = default, string intent = "work", ReviewLaunchRequest? reviewRequest = null, string? question = null, string? task = null);

    /// <summary>Starts the board's manager, or returns the one already running. Null when the board is not in the project.</summary>
    Task<LaunchBoardManagerResponse?> LaunchBoardManagerAsync(string projectPath, string boardId, CancellationToken cancellationToken = default);

    /// <summary>The board manager session whose tab is live in this root, if any. Root-local like every live check.</summary>
    Task<BoardManagerActiveSession?> FindLiveBoardManagerAsync(string projectPath, string boardId, CancellationToken cancellationToken = default);

    /// <summary>The live board manager and the board's live implementation workers, from one read of this root's tabs.</summary>
    Task<BoardManagerLiveState> GetBoardManagerLiveStateAsync(string projectPath, string boardId, CancellationToken cancellationToken = default);

    /// <summary>Closes the board manager's terminal in this root. Null when the board is not in the project.</summary>
    Task<StopBoardManagerResponse?> StopBoardManagerAsync(string projectPath, string boardId, CancellationToken cancellationToken = default);
}

/// <summary>
/// What is running for a board manager in this root (VIBE-204): its own session, and the live
/// implementation workers on the cards it manages, which its worker limit counts.
/// </summary>
public sealed record BoardManagerLiveState(BoardManagerActiveSession? Active, IReadOnlyList<BoardManagedWorkerSession> Workers);

/// <summary>
/// A composed launch prompt and the inputs that shaped it, so the context estimator (VB-63) can
/// break the same prompt down without composing a second one. <see cref="ContextLevels"/> are the
/// user's context levels the prompt carries, broadest first (VIBE-229).
/// </summary>
public sealed record BoardLaunchPrompt(string Prompt, string? EnvironmentPrompt, IReadOnlyList<ContextLevels.Level> ContextLevels, string? BoardId, string? BoardName,
    BoardCardDetailRecord? Detail = null, string Intent = "work");

/// <summary>
/// Root-backend only (it needs the in-process tab host). Copies the create/start/cleanup shape of
/// <c>TerminalTabHostService.CreatePythonScriptTabAsync</c>. The composed prompt is handed to the
/// tab child as <c>StartTerminalRequest.InitialPrompt</c>: TerminalRoutes prefers a request prompt
/// over the environment's, so the combined TEMPLATE reaches the one PromptPlaceholderService pass
/// unchanged — the once-only resolution invariant holds and no prompt plumbing changes.
///
/// After the session is linked and the launch reservation released, the launch records how much
/// context the card put in front of the agent (VB-63). That measurement never fails a launch: a
/// terminal that started is reported as started, and the missing sample is a warning in the log.
///
/// Agent workflow managers and their delegated workers have separate saved assignments and
/// terminal origins. One manager and one worker may run together; the manager owns lane decisions
/// through the ordinary Board tools. Nothing injects input into their live TUIs or launches a daemon.
/// </summary>
public sealed class BoardLaunchService(
    IBoardStore store,
    IRepository repository,
    ITerminalTabHostService tabHost,
    IBoardContextEstimator contextEstimator, ReviewRoutingService? reviewRouting = null,
    Func<Settings>? launchSettings = null) : IBoardLaunchService
{
    // Only in-flight launches are retained. TryAdd is an immediate reservation, not a
    // queued semaphore, so removal cannot strand waiters or create two gates for one card.
    private static readonly ConcurrentDictionary<string, byte> LaunchingCards = new(StringComparer.Ordinal);

    public async Task<LaunchBoardCardResponse?> LaunchAsync(string projectPath, string idOrKey, string? selectionOverride, CancellationToken cancellationToken = default, string intent = "work", ReviewLaunchRequest? reviewRequest = null, string? question = null, string? task = null)
    {
        if (intent is not ("work" or "chat" or "code_review" or "manager" or "worker"))
            throw new BoardValidationException("Launch intent must be work, chat, code_review, manager or worker.");
        if (task?.Length > BoardPromptComposer.MaxWorkerTaskChars)
            throw new BoardValidationException($"A delegated worker task is limited to {BoardPromptComposer.MaxWorkerTaskChars} characters.");
        if (!string.IsNullOrWhiteSpace(task) && intent is not ("worker" or "code_review"))
            throw new BoardValidationException("Task instructions apply only to a delegated worker launch.");
        var card = await store.FindCardAsync(projectPath, idOrKey, cancellationToken);
        if (card is null) return null;
        // Card ids are globally unique in the shared database; VB numbers are project-local.
        var reservation = card.Id + (intent == "chat" ? ":chat" : ":work");
        if (!LaunchingCards.TryAdd(reservation, 0))
            throw new BoardConflictException("An agent is already starting on this card. Wait for it to finish starting.");
        Launched? launched;
        // Further reservations the launch takes (the board's worker slot) live until it has linked its session.
        var held = new List<string>();
        try
        {
            // Re-read after the reservation: fields may have changed while resolving the key.
            launched = await LaunchCoreAsync(projectPath, card.Id, selectionOverride, cancellationToken, intent, reviewRequest, question, task, held);
        }
        finally
        {
            LaunchingCards.TryRemove(reservation, out _);
            foreach (var key in held) LaunchingCards.TryRemove(key, out _);
        }
        if (launched is null)
            return null;
        // Measured after the reservation is released, so the measurement never holds the gate: a second
        // Start work is answered by the live agent it would join, not refused while this one is counted.
        if (!launched.Reused)
            await RecordContextSampleAsync(projectPath, launched.Card, launched.Prompt, launched.Intent, launched.Response.SessionId, launched.Cli, launched.Selection);
        return launched.Response;
    }

    /// <summary>A started launch and what its context sample needs.</summary>
    private sealed record Launched(LaunchBoardCardResponse Response, BoardCardRecord Card, BoardLaunchPrompt Prompt, string Cli, string Selection, string Intent, bool Reused = false);

    private async Task<Launched?> LaunchCoreAsync(string projectPath, string idOrKey, string? selectionOverride, CancellationToken cancellationToken, string intent, ReviewLaunchRequest? reviewRequest, string? question, string? task,
        List<string> held)
    {
        var card = await store.FindCardAsync(projectPath, idOrKey, cancellationToken);
        if (card is null)
            return null;

        await BoardAccess.RequireWritableAsync(store, projectPath, card.BoardId, cancellationToken);

        if (intent == "work" && card.WorkflowMode == "agent") intent = "manager";
        var isManager = intent == "manager";
        var isWorker = intent == "worker" || (intent == "code_review" && card.WorkflowMode == "agent");
        if ((isManager || isWorker) && card.WorkflowMode != "agent")
            throw new BoardValidationException("Manager and delegated worker launches require the card's Agent workflow.");
        if (!string.IsNullOrWhiteSpace(task) && !isWorker)
            throw new BoardValidationException("Task instructions apply only to a delegated worker launch.");

        // An Agent card without its own manager belongs to the board manager (VIBE-204): Start Agent
        // workflow starts (or focuses) that one and makes sure this card is attached to it. With the
        // AI board manager switch off, such a card (saved while it was on) needs its own manager again.
        if (isManager && string.IsNullOrWhiteSpace(selectionOverride) && string.IsNullOrWhiteSpace(card.ManagerSelection))
        {
            if (!await store.GetBoardManagerEnabledAsync(cancellationToken))
                throw new BoardValidationException("This card uses the board manager, and the AI board manager is off. Turn it on in Settings > AI board manager, or pick a manager agent for this card.");
            var board = string.IsNullOrEmpty(card.BoardId) ? null : await store.GetBoardAsync(projectPath, card.BoardId, cancellationToken);
            var settings = board is null ? null : (await store.GetBoardManagerAsync(projectPath, board.Id, cancellationToken))?.Settings;
            if (settings?.IsConfigured != true)
                throw new BoardValidationException("Pick a manager agent for this card first, or set up this board's manager in Board settings > Manager.");
            var manager = await LaunchBoardManagerReservedAsync(projectPath, board!, cancellationToken);
            if (manager.SessionId is not null)
                await LinkManagerToCardAsync(projectPath, card, manager.SessionId, manager.TabId, manager.Selection, manager.Cli ?? "", cancellationToken);
            return new Launched(new LaunchBoardCardResponse(manager.TabId, manager.SessionId, manager.Cli, projectPath, card.Id, card.Key, manager.Selection),
                card, new BoardLaunchPrompt("", null, [], card.BoardId, board!.Name, Intent: intent), manager.Cli ?? "", manager.Selection, intent, Reused: true);
        }

        ReviewLaunchSnapshot? routingSnapshot = null;
        if (reviewRequest is not null)
        {
            if (intent != "code_review" || reviewRouting is null) throw new BoardValidationException("Reviewer routing requires Code review intent.");
            var saved = await store.GetReviewSettingsAsync(projectPath, card.Id, cancellationToken);
            routingSnapshot = await reviewRouting.ResolveAsync(projectPath, card.Key,
                reviewRequest.Routing ?? saved?.Routing ?? ReviewerRouting.SwitchDefault(), reviewRequest.Override, cancellationToken);
            selectionOverride = routingSnapshot.Resolution.Selected.Selection;
        }

        // The manager is a separate saved assignment. A delegated worker override never changes it.
        var savedSelection = isManager ? card.ManagerSelection : card.Assignee;
        var selectionText = string.IsNullOrWhiteSpace(selectionOverride) ? savedSelection : selectionOverride;
        if (string.IsNullOrWhiteSpace(selectionText))
            throw new BoardValidationException(isManager ? "Pick a manager agent for this card first." : "Assign an LLM to this card first, or pick one to start with.");
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

        if (isManager && (environment?.Purpose == "code_review" || environment?.ReviewerRouting?.Mode == "switch"))
            throw new BoardValidationException("Choose a work environment or base agent as manager; a code review environment is a worker.");

        string? routingWorkerPrompt = null;
        if (routingSnapshot is null && intent != "chat" && environment?.ReviewerRouting?.Mode == "switch")
        {
            if (reviewRouting is null) throw new BoardValidationException("Review routing is unavailable in this host.");
            routingWorkerPrompt = environment.CustomPrompt;
            routingSnapshot = await reviewRouting.ResolveAsync(projectPath, card.Key, environment.ReviewerRouting, null,
                cancellationToken, environment.Id, environment.CustomPrompt);
            BoardSelection.TryParse(routingSnapshot.Resolution.Selected.Selection, out parsed);
            environment = parsed!.EnvironmentId is int reviewerId ? await repository.GetEnvironmentByIdAsync(reviewerId, cancellationToken) : null;
            intent = "code_review";
        }

        if (intent != "chat" && environment?.Purpose == "code_review") intent = "code_review";
        var isReview = intent == "code_review";
        var assigneeLabel = environment is not null
            ? $"{environment.CustomName} ({parsed.Cli})"
            : parsed.Cli;
        // App and Board context follow the Nudge switch for this launch's kind: base or saved environment.
        var nudgeEnabled = ContextLevels.NudgeEnabled((launchSettings ?? Config.LoadFresh)(), environment is not null);
        var composed = await ComposePromptAsync(store, projectPath, card, assigneeLabel, routingWorkerPrompt is null ? environment?.CustomPrompt : routingWorkerPrompt + "\n" + environment?.CustomPrompt, intent, cancellationToken, task, isWorker, nudgeEnabled);
        if (intent == "chat") composed = composed with { Prompt = BoardPromptComposer.ComposeDiscussion(card, environment?.CustomPrompt, question, ContextLevels.Format(composed.ContextLevels)) };
        var prompt = composed.Prompt + (routingSnapshot is null ? "" : ReviewRoutingService.Prompt(routingSnapshot.Resolution));
        var title = $"{card.DisplayId} · {Truncate(card.Title, 60)}";
        var launchOptions = routingSnapshot is not null ? routingSnapshot.Resolution.Selected.Options
            : !parsed.IsEnvironment && string.Equals(parsed.Key, savedSelection, StringComparison.Ordinal) ? isManager ? card.ManagerBaseLlmOptions : card.BaseLlmOptions : null;
        var launchArgs = environment is null ? VibeRails.Services.LlmClis.BaseLlmOptionsBuilder.BuildArguments(parsed.Llm, launchOptions)
            : ShellArgSanitizer.ParseAndValidate(environment.CustomArgs).ToArray();
        if (intent == "chat" && launchArgs.Sum(arg => arg.Length + 1) > 4000)
            throw new BoardValidationException("For card discussion, use at most 4000 characters of environment arguments, or choose a base agent.");
        var isPlanning = card.Type == "research-spike" || launchArgs.Select((arg, index) =>
            arg is "--permission-mode=plan" or "--mode=plan" or "--agent=plan"
            || arg is "--permission-mode" or "--mode" or "--agent" && index + 1 < launchArgs.Length && launchArgs[index + 1] == "plan").Any(value => value);

        // The detail the prompt was composed from, rather than a second read of the same card.
        var detail = composed.Detail;
        var tabs = await tabHost.ListTabsAsync(cancellationToken);
        // Managers and workers have independent live-session gates. Only a working agent blocks
        // another worker launch. A lane Automation (and the CLI it spawned) is
        // linked to the card it came from but is not the card's agent, so a review still running
        // must not turn Start work into "already running" (VB-6Q8ZS-68 follow-up).
        IReadOnlyList<BoardSessionRecord> linkedSessions = detail?.Sessions ?? [];
        var automationIds = await store.GetAutomationSessionIdsAsync(projectPath,
            linkedSessions.Select(session => session.SessionId).ToList(), cancellationToken);
        var workingSessionIds = linkedSessions
            .Where(session => isWorker && session.Origin == "code_review" && !automationIds.Contains(session.SessionId)
                || !BoardService.IsAutomationSession(session.Origin, session.SessionId, automationIds))
            .Where(session => intent == "chat" ? session.Origin == "chat"
                : isManager ? session.Origin == "agent_manager"
                : isWorker ? session.Origin is not ("chat" or "agent_manager")
                : session.Origin is not ("chat" or "code_review"))
            .Where(session => intent != "chat" || session.Selection == parsed.Key)
            .Select(session => session.SessionId)
            .ToHashSet(StringComparer.Ordinal);
        var existingTab = tabs.FirstOrDefault(tab => tab.HasActiveSession && tab.SessionId is not null && workingSessionIds.Contains(tab.SessionId));
        if (intent == "chat" && existingTab is not null)
        {
            var existingSession = linkedSessions.First(s => s.SessionId == existingTab.SessionId);
            if (existingSession.Selection == parsed.Key && string.IsNullOrWhiteSpace(question))
                return new Launched(new LaunchBoardCardResponse(existingTab.TabId, existingTab.SessionId, existingSession.Cli, projectPath, card.Id, card.Key, existingSession.Selection),
                    card, composed, existingSession.Cli, existingSession.Selection, intent, Reused: true);
            // A different question/agent starts fresh; never paste generated input into a live TUI.
        }
        else if ((!isReview || isWorker) && existingTab is not null)
            throw new BoardConflictException(isManager ? "A manager is already running on this card. Open it from Sessions."
                : "A worker is already running on this card. Wait for its session to finish or open it from Sessions.", existingTab.SessionId);
        if (isWorker && !isReview && !isPlanning)
            await RequireManagedWorkerSlotAsync(projectPath, card, held, cancellationToken);
        if (tabs.Count >= tabHost.MaxTabs)
            throw new BoardConflictException($"All {tabHost.MaxTabs} terminal tabs are in use. Close one first.");

        BoardReviewRecord? review = isReview ? new("review_" + Guid.NewGuid().ToString("N"), card.Id, parsed.Cli, assigneeLabel, DateTime.UtcNow, Routing: routingSnapshot?.Resolution) : null;
        if (review is not null && !await store.SaveReviewAsync(projectPath, review, cancellationToken))
            throw new BoardValidationException("The review card is no longer available.");
        TerminalTabStatusResponse? tab = null;
        try
        {
            if (routingSnapshot is not null) await reviewRouting!.RequireLaunchAsync(routingSnapshot, cancellationToken);
            tab = await tabHost.CreateTabAsync(cancellationToken);
            var session = await tabHost.StartSessionAsync(
                tab.TabId,
                new StartTerminalRequest(
                    WorkingDirectory: projectPath,
                    Cli: parsed.Cli,
                    EnvironmentName: environment?.CustomName,
                    EnvironmentId: environment?.Id,
                    Title: title,
                    InitialPrompt: prompt,
                    BaseLlmOptions: launchOptions,
                    AuthorizeBoardTools: true, ContextBoardId: card.BoardId, ContextIncluded: true) { PreserveWorkingDirectory = routingSnapshot is not null },
                cancellationToken);

            if (!string.IsNullOrWhiteSpace(session.SessionId))
            {
                var origin = isManager ? BoardSessionRecord.ManagerOrigin
                    : isWorker ? isPlanning && !isReview ? BoardSessionRecord.PlanningWorkerOrigin : BoardSessionRecord.WorkerOrigin
                    : isReview ? "code_review" : intent == "chat" ? "chat" : isPlanning ? "planning" : BoardSessionRecord.LaunchOrigin;
                try
                {
                    var linked = await store.LinkSessionAsync(projectPath, card.Id, session.SessionId!, tab.TabId, parsed.Key, parsed.Cli,
                        title, origin, cancellationToken);
                    if (linked is null)
                        throw new BoardValidationException("The card was deleted while its agent was starting. The terminal has been closed.");
                }
                catch (BoardConflictException ex)
                {
                    if (!await store.ConfirmSessionLaunchOriginAsync(projectPath, card.Id, session.SessionId!, origin, cancellationToken))
                        throw;
                    Log.Warning("[Board] Session {SessionId} was already linked: {Message}", session.SessionId, ex.Message);
                }
            }
            else
            {
                Log.Warning("[Board] Tab {TabId} started for {Card} without a session id; the card will not show it", tab.TabId, card.Key);
            }

            if (review is not null)
            {
                review = review with { SessionId = session.SessionId, TabId = tab.TabId, ProcessStatus = "Running" };
                if (!await store.SaveReviewAsync(projectPath, review, CancellationToken.None))
                    throw new BoardValidationException("Could not link the review attempt to its session.");
            }
            return new Launched(new LaunchBoardCardResponse(tab.TabId, session.SessionId, session.Cli, session.WorkingDirectory, card.Id, card.Key, parsed.Key),
                card, composed, parsed.Cli, parsed.Key, intent);
        }
        catch (Exception failure)
        {
            if (review is not null)
            {
                try { await store.SaveReviewAsync(projectPath, review with { ProcessStatus = "Failed", Error = failure.Message }, CancellationToken.None); }
                catch (Exception recordFailure) { Log.Warning(recordFailure, "[Board] Could not record review launch failure {ReviewId}", review.Id); }
            }
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

    public async Task<LaunchBoardManagerResponse?> LaunchBoardManagerAsync(string projectPath, string boardId, CancellationToken cancellationToken = default)
    {
        var board = await store.GetBoardAsync(projectPath, boardId, cancellationToken);
        return board is null ? null : await LaunchBoardManagerReservedAsync(projectPath, board, cancellationToken);
    }

    public async Task<BoardManagerActiveSession?> FindLiveBoardManagerAsync(string projectPath, string boardId, CancellationToken cancellationToken = default) =>
        FindLiveBoardManager(await store.GetBoardManagerSessionsAsync(projectPath, boardId, 10, cancellationToken), await tabHost.ListTabsAsync(cancellationToken));

    private static BoardManagerActiveSession? FindLiveBoardManager(IReadOnlyList<BoardManagerSessionRecord> sessions, IReadOnlyList<TerminalTabStatusResponse> tabs)
    {
        foreach (var session in sessions)
        {
            var tab = tabs.FirstOrDefault(tab => tab.HasActiveSession && tab.SessionId == session.SessionId);
            if (tab is not null)
                return new BoardManagerActiveSession(session.SessionId, tab.TabId, session.Cli, session.Selection, session.StartedUtc);
        }
        return null;
    }

    public async Task<BoardManagerLiveState> GetBoardManagerLiveStateAsync(string projectPath, string boardId, CancellationToken cancellationToken = default)
    {
        var tabs = await tabHost.ListTabsAsync(cancellationToken);
        var active = FindLiveBoardManager(await store.GetBoardManagerSessionsAsync(projectPath, boardId, 10, cancellationToken), tabs);
        return new BoardManagerLiveState(active, await LiveImplementationWorkersAsync(projectPath, boardId, tabs, cancellationToken));
    }

    public async Task<StopBoardManagerResponse?> StopBoardManagerAsync(string projectPath, string boardId, CancellationToken cancellationToken = default)
    {
        if (await store.GetBoardAsync(projectPath, boardId, cancellationToken) is null) return null;
        var live = await FindLiveBoardManagerAsync(projectPath, boardId, cancellationToken);
        if (live is null) return new StopBoardManagerResponse(false, null);
        // Closing the tab is what closing it in the terminal does: the recording is kept, workers it
        // started keep running, and a restart recovers from the cards' handoffs.
        await tabHost.DeleteTabAsync(live.TabId, cancellationToken);
        Log.Information("[Board] Stopped board manager {SessionId} for board {Board}", live.SessionId, boardId);
        return new StopBoardManagerResponse(true, live.SessionId);
    }

    /// <summary>
    /// Live implementation workers on the board's board-managed cards: worker sessions with a live tab
    /// in this root, without reviews, planning workers and lane Automations, which never edit files on
    /// the manager's behalf. One entry per session, however many cards it is attached to.
    /// </summary>
    private async Task<IReadOnlyList<BoardManagedWorkerSession>> LiveImplementationWorkersAsync(string projectPath, string boardId,
        IReadOnlyList<TerminalTabStatusResponse> tabs, CancellationToken cancellationToken)
    {
        var live = tabs.Where(tab => tab.HasActiveSession && !string.IsNullOrWhiteSpace(tab.SessionId))
            .Select(tab => tab.SessionId!).ToHashSet(StringComparer.Ordinal);
        if (live.Count == 0) return [];
        var workers = (await store.GetManagedWorkerSessionsAsync(projectPath, boardId, cancellationToken))
            .Where(session => session.Origin == BoardSessionRecord.WorkerOrigin && live.Contains(session.SessionId))
            .GroupBy(session => session.SessionId, StringComparer.Ordinal).Select(group => group.First()).ToList();
        if (workers.Count == 0) return [];
        var ids = workers.Select(session => session.SessionId).ToList();
        var reviews = await store.GetReviewSessionIdsAsync(projectPath, ids, cancellationToken);
        var automations = await store.GetAutomationSessionIdsAsync(projectPath, ids, cancellationToken);
        return workers.Where(session => !reviews.Contains(session.SessionId) && !automations.Contains(session.SessionId)).ToList();
    }

    /// <summary>
    /// The board manager's worker limit (VIBE-204): implementation workers on the cards it manages
    /// usually share one checkout, so at most <see cref="BoardManagerSettings.MaxWorkers"/> of them
    /// run at once across the board, whoever asks. Cards with their own manager, reviews and
    /// planning workers are outside it, and so is everything while the AI board manager is off.
    /// The board's worker slot is reserved before counting and kept (in <paramref name="held"/>)
    /// until the caller has started and linked its session, so two launches on different cards can
    /// never both see a free slot; tabs are listed again under the reservation for the same reason.
    /// Root-local, like every live check here.
    /// </summary>
    private async Task RequireManagedWorkerSlotAsync(string projectPath, BoardCardRecord card, List<string> held, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(card.ManagerSelection) || string.IsNullOrEmpty(card.BoardId)) return;
        if (!await store.GetBoardManagerEnabledAsync(cancellationToken)) return;
        if ((await store.GetBoardManagerAsync(projectPath, card.BoardId, cancellationToken))?.Settings is not { IsConfigured: true } manager) return;
        var slot = "workers:" + card.BoardId;
        if (!LaunchingCards.TryAdd(slot, 0))
            throw new BoardConflictException("Another implementation worker is starting on this board. Wait for it with await_board_events, then try again.");
        held.Add(slot);
        var running = await LiveImplementationWorkersAsync(projectPath, card.BoardId, await tabHost.ListTabsAsync(cancellationToken), cancellationToken);
        if (running.Count < manager.MaxWorkers) return;
        var cards = string.Join(", ", running.Select(worker => worker.DisplayId ?? worker.CardKey).Distinct(StringComparer.Ordinal));
        throw new BoardConflictException(
            $"The board manager runs at most {manager.MaxWorkers} implementation worker{(manager.MaxWorkers == 1 ? "" : "s")} at a time on this board, "
            + $"and {running.Count} {(running.Count == 1 ? "is" : "are")} running ({cards}). Wait for one to finish with await_board_events, "
            + "or raise Workers at a time in Board settings > Manager.", running[0].SessionId);
    }

    /// <summary>One board manager starts at a time per board, whichever card or button asked for it.</summary>
    private async Task<LaunchBoardManagerResponse> LaunchBoardManagerReservedAsync(string projectPath, BoardRecord board, CancellationToken cancellationToken)
    {
        var reservation = "board:" + board.Id;
        if (!LaunchingCards.TryAdd(reservation, 0))
            throw new BoardConflictException("The board manager is already starting. Wait for it to finish starting.");
        try { return await LaunchBoardManagerCoreAsync(projectPath, board, cancellationToken); }
        finally { LaunchingCards.TryRemove(reservation, out _); }
    }

    /// <summary>
    /// The board manager launch (VIBE-52 follow-up, VIBE-204). It mirrors the card launch: the AI
    /// board manager switch and a writable board first, then resolve the saved selection, refuse a
    /// reviewer as manager, one live manager per board, grant only the Board tools, and record the
    /// session before anyone can poll it. App and Board context follow the launch's nudge switch and
    /// are composed into the prompt, so the tab child does not prepend them again. The new session is
    /// attached to every Agent card it manages with the manager origin, so each card's Go to manager
    /// reaches this tab.
    /// </summary>
    private async Task<LaunchBoardManagerResponse> LaunchBoardManagerCoreAsync(string projectPath, BoardRecord board, CancellationToken cancellationToken)
    {
        if (!await store.GetBoardManagerEnabledAsync(cancellationToken))
            throw new BoardConflictException(BoardManagerService.DisabledMessage);
        await BoardAccess.RequireWritableAsync(store, projectPath, board.Id, cancellationToken);
        var settings = (await store.GetBoardManagerAsync(projectPath, board.Id, cancellationToken))?.Settings;
        if (settings?.IsConfigured != true)
            throw new BoardValidationException("Set up this board's manager in Board settings > Manager first.");
        if (!BoardSelection.TryParse(settings.Selection, out var parsed) || parsed is null)
            throw new BoardValidationException("The board manager is not a launchable LLM selection.");
        LLM_Environment? environment = null;
        if (parsed.IsEnvironment)
        {
            var byId = await repository.GetEnvironmentByIdAsync(parsed.EnvironmentId!.Value, cancellationToken);
            environment = byId is not null && byId.LLM == parsed.Llm ? byId : null;
            if (environment is null)
                throw new BoardValidationException("The board manager's environment no longer exists (or changed CLI). Pick another manager in Board settings.");
            if (!ProjectPathComparer.IsVisibleIn(environment.ProjectPath, projectPath))
                throw new BoardValidationException("The board manager's environment belongs to another project.");
            if (environment.Purpose == "code_review" || environment.ReviewerRouting?.Mode == "switch")
                throw new BoardValidationException("Choose a work environment or base agent as the board manager; a code review environment is a worker.");
        }

        var tabs = await tabHost.ListTabsAsync(cancellationToken);
        var live = FindLiveBoardManager(await store.GetBoardManagerSessionsAsync(projectPath, board.Id, 10, cancellationToken), tabs);
        if (live is not null)
            return new LaunchBoardManagerResponse(live.TabId, live.SessionId, live.Cli, board.Id, live.Selection, Reused: true);
        if (tabs.Count >= tabHost.MaxTabs)
            throw new BoardConflictException($"All {tabHost.MaxTabs} terminal tabs are in use. Close one first.");

        var label = environment is not null ? $"{environment.CustomName} ({parsed.Cli})" : parsed.Cli;
        var nudgeEnabled = ContextLevels.NudgeEnabled((launchSettings ?? Config.LoadFresh)(), environment is not null);
        var liveWorkerCards = (await LiveImplementationWorkersAsync(projectPath, board.Id, tabs, cancellationToken))
            .Select(worker => worker.CardId).ToHashSet(StringComparer.Ordinal);
        var composed = await ComposeBoardManagerPromptAsync(store, projectPath, board, settings, label, environment?.CustomPrompt, nudgeEnabled, liveWorkerCards, cancellationToken);
        var title = $"{Truncate(board.Name, 40)} · Manager";
        TerminalTabStatusResponse? tab = null;
        try
        {
            tab = await tabHost.CreateTabAsync(cancellationToken);
            var session = await tabHost.StartSessionAsync(tab.TabId, new StartTerminalRequest(
                WorkingDirectory: projectPath,
                Cli: parsed.Cli,
                EnvironmentName: environment?.CustomName,
                EnvironmentId: environment?.Id,
                Title: title,
                InitialPrompt: composed.Prompt,
                BaseLlmOptions: environment is null ? settings.BaseLlmOptions : null,
                AuthorizeBoardTools: true, ContextBoardId: board.Id, ContextIncluded: true), cancellationToken);
            if (string.IsNullOrWhiteSpace(session.SessionId))
            {
                Log.Warning("[Board] Tab {TabId} started the manager for board {Board} without a session id; the board will not show it", tab.TabId, board.Id);
                return new LaunchBoardManagerResponse(tab.TabId, null, session.Cli, board.Id, parsed.Key, Reused: false);
            }
            if (await store.RecordBoardManagerSessionAsync(projectPath, board.Id, session.SessionId, tab.TabId, parsed.Key, parsed.Cli, cancellationToken) is null)
                throw new BoardValidationException("The board was deleted while its manager was starting. The terminal has been closed.");
            foreach (var managed in composed.Cards.Where(card => !card.OwnManager))
                await LinkManagerToCardAsync(projectPath, managed.Card, session.SessionId, tab.TabId, parsed.Key, parsed.Cli, cancellationToken);
            Log.Information("[Board] Started board manager {SessionId} ({Cli}) for board {Board} with {Cards} managed card(s)",
                session.SessionId, parsed.Cli, board.Id, composed.Cards.Count(card => !card.OwnManager));
            return new LaunchBoardManagerResponse(tab.TabId, session.SessionId, session.Cli, board.Id, parsed.Key, Reused: false);
        }
        catch (Exception)
        {
            if (tab is not null)
            {
                try { await tabHost.DeleteTabAsync(tab.TabId, CancellationToken.None); }
                catch (Exception ex) { Log.Debug(ex, "[Board] Failed to clean up tab {TabId} after a failed board manager launch", tab.TabId); }
            }
            throw;
        }
    }

    /// <summary>Attaches the board manager to a card it manages; an existing attachment is kept as it is.</summary>
    private async Task LinkManagerToCardAsync(string projectPath, BoardCardRecord card, string sessionId, string? tabId, string selection, string cli, CancellationToken cancellationToken)
    {
        try
        {
            await store.LinkSessionAsync(projectPath, card.Id, sessionId, tabId, selection, cli,
                $"{card.DisplayId ?? card.Key} · Manager", BoardSessionRecord.ManagerOrigin, cancellationToken);
        }
        catch (BoardConflictException)
        {
            // Already attached to this card: the manager was running when the card became an Agent card.
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Board] Could not attach the board manager {SessionId} to {Card}", sessionId, card.Key);
        }
    }

    /// <summary>A composed board manager prompt, the cards it lists and the user's context levels it carries.</summary>
    internal sealed record BoardManagerPrompt(string Prompt, IReadOnlyList<BoardManagedCardRecord> Cards, IReadOnlyList<ContextLevels.Level> ContextLevels);

    /// <summary>
    /// The board manager prompt: lanes and their Automations, the managed cards (those with a live
    /// worker marked), the manager's standing orders and worker limit, and App and Board context when
    /// <paramref name="nudgeEnabled"/>. Card and Card type context reach workers through their cards.
    /// </summary>
    internal static async Task<BoardManagerPrompt> ComposeBoardManagerPromptAsync(IBoardStore store, string projectPath, BoardRecord board,
        BoardManagerSettings settings, string? managerLabel, string? environmentPrompt, bool nudgeEnabled, IReadOnlySet<string>? liveWorkerCards,
        CancellationToken cancellationToken)
    {
        var columns = (await store.GetColumnsAsync(projectPath, cancellationToken, board.Id)).OrderBy(column => column.Position).ToList();
        var cards = await store.GetManagedCardsAsync(projectPath, board.Id, cancellationToken);
        var levels = nudgeEnabled ? await ContextLevels.ResolveNudgedForCardAsync(store, projectPath, board.Id, cancellationToken) : [];
        var laneAutomations = await BoardService.DescribeLaneAutomationsByLaneAsync(store, projectPath, columns.Select(column => column.Id).ToList(), cancellationToken);
        var context = new BoardPromptComposer.BoardManagerContext(board, columns, cards,
            columns.Select(column => (IReadOnlyList<string>)laneAutomations[column.Id].Select(automation => automation.Name).ToList()).ToList(),
            columns.SelectMany(column => laneAutomations[column.Id].Select(automation => $"{column.Name} / {automation.Name}: {automation.Summary}. Output and next action: {automation.Output}")).ToList(),
            settings, levels, liveWorkerCards);
        return new BoardManagerPrompt(BoardPromptComposer.ComposeBoardManager(context, managerLabel, environmentPrompt), cards, levels);
    }

    /// <summary>
    /// The prompt a launch composes for a card. Shared with <see cref="BoardContextEstimator"/> so
    /// the measurement the card editor shows is the prompt a launch would send, not an imitation.
    /// Lanes, linked commits and attachment names ride along so the agent does not spend its first
    /// minutes discovering which lane names exist or which change "the big refactor" was; lane
    /// Automations ride along too, so the agent can sequence its moves (VB-34). The user's context
    /// levels ride in one block: App and Board only when <paramref name="nudgeEnabled"/>, Card and
    /// Card type always (VIBE-229).
    /// </summary>
    internal static async Task<BoardLaunchPrompt> ComposePromptAsync(IBoardStore store, string projectPath, BoardCardRecord card,
        string? assigneeLabel, string? environmentPrompt, string intent, CancellationToken cancellationToken, string? task = null, bool delegatedWorker = false,
        bool nudgeEnabled = false)
    {
        var boardId = string.IsNullOrEmpty(card.BoardId) ? null : card.BoardId;
        var columns = await store.GetColumnsAsync(projectPath, cancellationToken, boardId);
        var column = columns.FirstOrDefault(c => c.Id == card.ColumnId);
        var boardName = boardId is null ? null : (await store.GetBoardAsync(projectPath, boardId, cancellationToken))?.Name;
        var boardContext = boardId is null ? null : await store.GetContextSettingsAsync(projectPath, boardId, cancellationToken);
        var nudged = nudgeEnabled ? await ContextLevels.ResolveNudgedForCardAsync(store, projectPath, boardId, cancellationToken) : null;
        var detail = await store.GetCardDetailAsync(projectPath, card.Id, cancellationToken);
        var orderedColumns = columns.OrderBy(c => c.Position).ToList();
        var laneAutomations = await BoardService.DescribeLaneAutomationsByLaneAsync(store, projectPath, orderedColumns.Select(c => c.Id).ToList(), cancellationToken);
        var context = new BoardPromptComposer.LaunchContext(
            orderedColumns.Select(c => c.Name).ToList(),
            detail?.Commits ?? [],
            detail?.Attachments ?? [],
            boardName,
            boardContext?.Context,
            orderedColumns.Select(c => (IReadOnlyList<string>)laneAutomations[c.Id].Select(a => a.Name).ToList()).ToList(),
            detail is null ? null : new BoardPromptComposer.CardActivity(detail.Comments.Count, detail.Notes.Count, detail.Sessions.Count),
            orderedColumns.SelectMany(c => laneAutomations[c.Id].Select(a => $"{c.Name} / {a.Name}: {a.Summary}. Output and next action: {a.Output}")).ToList(),
            nudged);
        var prompt = BoardPromptComposer.Compose(card, column?.Name ?? "(no lane)", assigneeLabel, environmentPrompt, context, intent, workerTask: task, delegatedWorker: delegatedWorker);
        return new BoardLaunchPrompt(prompt, environmentPrompt, context.LevelsFor(card.Type), boardId, boardName, detail, intent);
    }

    /// <summary>
    /// Runs after the terminal is up and linked. Nothing from here may reach the launch's cleanup
    /// path: the sample takes no request token (a cancelled HTTP request must not tear down a
    /// terminal that already started) and every failure, cancellation included, is only logged.
    /// </summary>
    private async Task RecordContextSampleAsync(string projectPath, BoardCardRecord card, BoardLaunchPrompt composed, string intent,
        string? sessionId, string cli, string selection)
    {
        try
        {
            await contextEstimator.RecordLaunchAsync(projectPath, card, composed, intent, sessionId, cli, selection, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Board] Could not record the launch context sample for {Card}", card.Key);
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max].TrimEnd() + "…";
}
