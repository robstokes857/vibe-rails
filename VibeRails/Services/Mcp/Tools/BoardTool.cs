using System.ComponentModel;
using System.Globalization;
using System.Text;
using ModelContextProtocol.Server;
using Serilog;
using VibeRails.DTOs;
using VibeRails.Services.AgentTools;
using VibeRails.Services.Board;

namespace VibeRails.Services.Mcp.Tools;

/// <summary>
/// Kanban board tools for an LLM working a card. Registered on BOTH transports (HTTP /mcp in the
/// root backend, and the stdio <c>vb mcp</c> host) and backed by the board store directly, so they
/// work from any terminal — no VibeRails tab needs to be involved.
///
/// Read / append / move / link only: there is deliberately no delete tool, and the only process
/// these tools spawn is <c>git</c> with a validated sha (see BoardCommitService). Failures come
/// back as readable <c>FAIL:</c> sentences (house style); exception detail goes to the file log.
///
/// Card arguments accept a key (<c>VB-12</c>) or an id. When omitted, the card this terminal was
/// launched for is used (the session id VibeRails stamps into the environment).
/// </summary>
[McpServerToolType]
public sealed class BoardTool(
    IBoardService board,
    IBoardProjectResolver projects,
    IBoardStore store)
{
    private const string NoCardHint =
        "FAIL: no card given and this terminal is not linked to one. Pass the card key, e.g. card=\"VB-12\" (see list_board_cards).";

    [McpServerTool, Description("List the lanes (columns) of this project's VibeRails kanban board with their WIP limits and card counts.")]
    public async Task<string> ListBoardColumns(CancellationToken cancellationToken = default)
    {
        try
        {
            var project = await projects.ResolveAsync(cancellationToken);
            var columns = await board.GetColumnsAsync(project, cancellationToken);
            var cards = await board.GetCardsAsync(project, cancellationToken);
            var builder = new StringBuilder();
            builder.Append("Board lanes for ").Append(project).Append(":\n");
            foreach (var column in columns.Columns.OrderBy(c => c.Position))
            {
                var count = cards.Cards.Count(c => c.ColumnId == column.Id);
                builder.Append("- ").Append(column.Name)
                    .Append(" (id ").Append(column.Id).Append(", ").Append(count).Append(" card").Append(count == 1 ? "" : "s");
                if (column.WipLimit is int wip)
                    builder.Append(", WIP limit ").Append(wip);
                builder.Append(")\n");
            }
            return builder.ToString().TrimEnd();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail("list the board lanes", ex);
        }
    }

    [McpServerTool, Description("List the cards on this project's VibeRails kanban board: key, lane, priority, title, assignee, comment count and whether a terminal session is open on it. Optional filters by lane name and assignee key.")]
    public async Task<string> ListBoardCards(
        [Description("Only cards in this lane (name or id). Optional.")] string? column = null,
        [Description("Only cards assigned to this LLM picker key, e.g. base:claude or env:7:codex. Optional.")] string? assignee = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var project = await projects.ResolveAsync(cancellationToken);
            var columns = (await board.GetColumnsAsync(project, cancellationToken)).Columns.ToDictionary(c => c.Id, c => c);
            var cards = (await board.GetCardsAsync(project, cancellationToken)).Cards.AsEnumerable();

            if (!string.IsNullOrWhiteSpace(column))
            {
                var lane = await board.FindColumnAsync(project, column, cancellationToken);
                if (lane is null)
                    return $"FAIL: lane not found: {column}. Use list_board_columns to see the lanes.";
                cards = cards.Where(c => c.ColumnId == lane.Id);
            }
            if (!string.IsNullOrWhiteSpace(assignee))
                cards = cards.Where(c => string.Equals(c.Assignee, assignee.Trim(), StringComparison.OrdinalIgnoreCase));

            var rows = cards.OrderBy(c => columns.TryGetValue(c.ColumnId, out var lane) ? lane.Position : int.MaxValue).ThenBy(c => c.Position).ToList();
            if (rows.Count == 0)
                return "No cards match.";

            var builder = new StringBuilder();
            foreach (var card in rows)
            {
                builder.Append(card.Key).Append(" [").Append(columns.TryGetValue(card.ColumnId, out var lane) ? lane.Name : card.ColumnId).Append("] (")
                    .Append(card.Priority).Append(") ").Append(card.Title);
                if (!string.IsNullOrWhiteSpace(card.Assignee)) builder.Append(" — assignee ").Append(card.Assignee);
                if (card.Blocked) builder.Append(" — BLOCKED");
                if (card.CommentCount > 0) builder.Append(" — ").Append(card.CommentCount).Append(" comment").Append(card.CommentCount == 1 ? "" : "s");
                if (!string.IsNullOrWhiteSpace(card.ActiveTabId)) builder.Append(" — session open");
                builder.Append('\n');
            }
            return builder.ToString().TrimEnd();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail("list the board cards", ex);
        }
    }

    [McpServerTool, Description("Read one kanban card in full: fields, description, comments, linked commits, linked terminal sessions and attachment names. Omit the card to read the card this terminal was launched for.")]
    public async Task<string> GetBoardCard(
        [Description("Card key like VB-12 (or the card id). Optional when this terminal was launched for a card.")] string? card = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var target = await ResolveCardAsync(card, cancellationToken);
            if (target.Error is not null)
                return target.Error;
            var detail = await board.GetCardAsync(target.Project, target.CardId!, cancellationToken);
            if (detail is null)
                return $"FAIL: card not found: {card}";
            var lane = await board.FindColumnAsync(target.Project, detail.ColumnId, cancellationToken);
            return FormatCard(detail, lane?.Name ?? detail.ColumnId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail("read the card", ex);
        }
    }

    [McpServerTool, Description("Create a new kanban card on this project's board. Returns the new card's key.")]
    public async Task<string> CreateBoardCard(
        [Description("Card title (required).")] string title,
        [Description("Longer description of the work. Optional.")] string? description = null,
        [Description("Lane name or id to create the card in. Defaults to the left-most lane.")] string? column = null,
        [Description("critical | high | medium | low. Defaults to medium.")] string? priority = null,
        [Description("Comma-separated tags. Optional.")] string? tags = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var project = await projects.ResolveAsync(cancellationToken);
            string? columnId = null;
            if (!string.IsNullOrWhiteSpace(column))
            {
                var lane = await board.FindColumnAsync(project, column, cancellationToken);
                if (lane is null)
                    return $"FAIL: lane not found: {column}. Use list_board_columns to see the lanes.";
                columnId = lane.Id;
            }
            var created = await board.CreateCardAsync(project, new CreateBoardCardRequest(
                Title: title,
                ColumnId: columnId,
                Description: description,
                Priority: priority,
                Tags: SplitTags(tags)), cancellationToken);
            await AutoLinkSessionAsync(project, created.Id, cancellationToken);
            return $"Created {created.Key}: {created.Title}";
        }
        catch (BoardValidationException ex) { return "FAIL: " + ex.Message; }
        catch (BoardConflictException ex) { return "FAIL: " + ex.Message; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail("create the card", ex);
        }
    }

    [McpServerTool, Description("Update fields on a kanban card. Only the arguments you pass change; the rest stay as they are.")]
    public async Task<string> UpdateBoardCard(
        [Description("Card key like VB-12 (or the card id).")] string card,
        [Description("New title.")] string? title = null,
        [Description("New description (replaces the whole description).")] string? description = null,
        [Description("critical | high | medium | low.")] string? priority = null,
        [Description("Story points: 1, 2, 3, 5, 8 or 13. Pass 0 to clear.")] int? points = null,
        [Description("Comma-separated tags (replaces all tags). Pass an empty string to clear.")] string? tags = null,
        [Description("Mark the card blocked (true) or unblocked (false).")] bool? blocked = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var target = await ResolveCardAsync(card, cancellationToken);
            if (target.Error is not null)
                return target.Error;
            var request = new UpdateBoardCardRequest(
                Title: title,
                Description: description,
                Priority: priority,
                Points: points is null ? default : PointsElement(points.Value),
                Tags: tags is null ? null : SplitTags(tags) ?? [],
                Blocked: blocked);
            var updated = await board.UpdateCardAsync(target.Project, target.CardId!, request, cancellationToken);
            if (updated is null)
                return $"FAIL: card not found: {card}";
            await AutoLinkSessionAsync(target.Project, updated.Id, cancellationToken);
            return $"Updated {updated.Key}: {updated.Title} ({updated.Priority}{(updated.Blocked ? ", blocked" : "")})";
        }
        catch (BoardValidationException ex) { return "FAIL: " + ex.Message; }
        catch (BoardConflictException ex) { return "FAIL: " + ex.Message; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail("update the card", ex);
        }
    }

    [McpServerTool, Description("Move a kanban card to another lane (by lane name or id), optionally at a position within it. Use this when the card changes state, e.g. to Review when the work is ready for eyes.")]
    public async Task<string> MoveBoardCard(
        [Description("Card key like VB-12 (or the card id).")] string card,
        [Description("Target lane name or id, e.g. Review.")] string column,
        [Description("0-based position within the lane. Defaults to the end.")] int? position = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var target = await ResolveCardAsync(card, cancellationToken);
            if (target.Error is not null)
                return target.Error;
            var moved = await board.MoveCardAsync(target.Project, target.CardId!, column, position, cancellationToken);
            if (moved is null)
                return $"FAIL: card not found: {card}";
            var lane = await board.FindColumnAsync(target.Project, moved.ColumnId, cancellationToken);
            await AutoLinkSessionAsync(target.Project, moved.Id, cancellationToken);
            return $"Moved {moved.Key} to {lane?.Name ?? moved.ColumnId} (position {moved.Position}).";
        }
        catch (BoardValidationException ex) { return "FAIL: " + ex.Message; }
        catch (BoardConflictException ex) { return "FAIL: " + ex.Message; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail("move the card", ex);
        }
    }

    [McpServerTool, Description("Add a comment to a kanban card. Use it to record progress, decisions, blockers and hand-off notes so the next session can resume. Omit the card to comment on the card this terminal was launched for.")]
    public async Task<string> AddBoardComment(
        [Description("Comment text.")] string body,
        [Description("Card key like VB-12 (or the card id). Optional when this terminal was launched for a card.")] string? card = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var target = await ResolveCardAsync(card, cancellationToken);
            if (target.Error is not null)
                return target.Error;
            var author = await ResolveAuthorAsync(cancellationToken);
            var comment = await board.AddCommentAsync(target.Project, target.CardId!, author, body, cancellationToken);
            if (comment is null)
                return $"FAIL: card not found: {card}";
            await AutoLinkSessionAsync(target.Project, target.CardId!, cancellationToken);
            return $"Comment added to {target.CardKey} as {author.Label} at {comment.CreatedAt:HH:mm:ss}Z.";
        }
        catch (BoardValidationException ex) { return "FAIL: " + ex.Message; }
        catch (BoardConflictException ex) { return "FAIL: " + ex.Message; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail("add the comment", ex);
        }
    }

    [McpServerTool, Description("Save a git commit's changed-code snapshot from this terminal's checkout on a kanban card. The snapshot remains viewable after the checkout is deleted. Call it after you commit; capture must succeed before the commit is linked. Omit the card to link to the card this terminal was launched for.")]
    public async Task<string> LinkBoardCommit(
        [Description("Commit sha (7-40 hex characters) from this terminal's checkout.")] string sha,
        [Description("Card key like VB-12 (or the card id). Optional when this terminal was launched for a card.")] string? card = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var target = await ResolveCardAsync(card, cancellationToken);
            if (target.Error is not null)
                return target.Error;
            var commit = await board.LinkCommitAsync(target.Project, target.CardId!, sha, cancellationToken,
                gitWorkingDirectory: projects.GitWorkingDirectory);
            if (commit is null)
                return $"FAIL: card not found: {card}";
            await AutoLinkSessionAsync(target.Project, target.CardId!, cancellationToken);
            return $"Linked {commit.ShortSha} \"{commit.Message}\" to {target.CardKey}.";
        }
        catch (BoardValidationException ex) { return "FAIL: " + ex.Message; }
        catch (BoardConflictException ex) { return "FAIL: " + ex.Message; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail("link the commit", ex);
        }
    }

    // ------------------------------------------------------------------ helpers

    private sealed record CardTarget(string Project, string? CardId, string? CardKey, string? Error);

    /// <summary>Explicit card argument first; otherwise the card this session was launched for.</summary>
    private async Task<CardTarget> ResolveCardAsync(string? card, CancellationToken cancellationToken)
    {
        var project = await projects.ResolveAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(card))
        {
            var found = await board.FindCardAsync(project, card, cancellationToken);
            return found is null
                ? new CardTarget(project, null, null, $"FAIL: card not found on this project's board: {card}. Use list_board_cards to see the keys.")
                : new CardTarget(project, found.Id, found.Key, null);
        }

        if (projects.CurrentSessionId is { } sessionId)
        {
            var link = await store.FindSessionLinkAsync(sessionId, cancellationToken);
            if (link is not null)
            {
                var linked = await board.FindCardAsync(link.ProjectPath, link.CardId, cancellationToken);
                if (linked is not null)
                    return new CardTarget(link.ProjectPath, linked.Id, linked.Key, null);
            }
        }
        return new CardTarget(project, null, null, NoCardHint);
    }

    /// <summary>Agent comments are attributed to the launching session when there is one, else to a generic agent.</summary>
    private async Task<BoardAuthor> ResolveAuthorAsync(CancellationToken cancellationToken)
    {
        var sessionId = projects.CurrentSessionId;
        if (sessionId is not null)
        {
            var author = await store.FindSessionAuthorAsync(sessionId, cancellationToken);
            if (author is not null) return author;
        }
        return BoardAuthor.Agent("Agent", null, sessionId);
    }

    /// <summary>
    /// A VibeRails-launched session that touches a card it is not yet linked to gets linked (origin
    /// "mcp"), so "pick up VB-12" from any VibeRails tab shows in the card's Sessions rail.
    /// </summary>
    private async Task AutoLinkSessionAsync(string project, string cardId, CancellationToken cancellationToken)
    {
        var sessionId = projects.CurrentSessionId;
        if (sessionId is null)
            return;
        try
        {
            var existing = await store.FindSessionLinkAsync(sessionId, cancellationToken);
            if (existing is not null)
                return;
            var tabId = Environment.GetEnvironmentVariable(LocalToolApiContext.CurrentTabIdVariable);
            var author = await store.FindSessionAuthorAsync(sessionId, cancellationToken);
            await store.LinkSessionAsync(project, cardId, sessionId, string.IsNullOrWhiteSpace(tabId) ? null : tabId.Trim(),
                selection: string.Empty, cli: author?.Cli ?? string.Empty, displayName: author?.Label ?? "Agent session",
                BoardSessionRecord.McpOrigin, cancellationToken);
        }
        catch (BoardConflictException)
        {
            // Linked by a concurrent call — nothing to do.
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Debug(ex, "[Board] Auto-link of session {SessionId} to card {CardId} failed", sessionId, cardId);
        }
    }

    internal static string FormatCard(BoardCardResponse card, string laneName)
    {
        var builder = new StringBuilder();
        builder.Append(card.Key).Append(": ").Append(card.Title).Append('\n');
        builder.Append("Lane: ").Append(laneName)
            .Append(" · Priority: ").Append(card.Priority)
            .Append(" · Assignee: ").Append(string.IsNullOrWhiteSpace(card.Assignee) ? "unassigned" : card.Assignee);
        if (card.Points is int points) builder.Append(" · Points: ").Append(points);
        if (card.Blocked) builder.Append(" · BLOCKED");
        if (card.Tags.Count > 0) builder.Append(" · Tags: ").Append(string.Join(", ", card.Tags));
        builder.Append('\n');
        builder.Append("Created ").Append(card.CreatedAt.ToString("u", CultureInfo.InvariantCulture))
            .Append(" · Updated ").Append(card.UpdatedAt.ToString("u", CultureInfo.InvariantCulture)).Append("\n\n");

        builder.Append("Description:\n").Append(string.IsNullOrWhiteSpace(card.Description) ? "(none)" : card.Description).Append("\n\n");

        builder.Append("Comments (").Append(card.Comments.Count).Append("):\n");
        if (card.Comments.Count == 0) builder.Append("(none)\n");
        foreach (var comment in card.Comments)
        {
            builder.Append("- [").Append(comment.CreatedAt.ToString("u", CultureInfo.InvariantCulture)).Append("] ")
                .Append(comment.Author.Label).Append(": ").Append(comment.Body).Append('\n');
        }

        builder.Append("\nLinked commits (").Append(card.Commits.Count).Append("):\n");
        if (card.Commits.Count == 0) builder.Append("(none)\n");
        foreach (var commit in card.Commits)
            builder.Append("- ").Append(commit.ShortSha).Append(' ').Append(commit.Message).Append(" (").Append(commit.Author).Append(")\n");

        builder.Append("\nSessions (").Append(card.Sessions.Count).Append("):\n");
        if (card.Sessions.Count == 0) builder.Append("(none)\n");
        foreach (var session in card.Sessions)
        {
            builder.Append("- ").Append(session.DisplayName).Append(" · ").Append(session.CreatedAt.ToString("u", CultureInfo.InvariantCulture))
                .Append(session.Active ? " · OPEN" : " · ended").Append('\n');
        }

        if (card.Attachments.Count > 0)
        {
            builder.Append("\nAttachments (").Append(card.Attachments.Count).Append("): ")
                .Append(string.Join(", ", card.Attachments.Select(a => a.Name))).Append('\n');
        }
        return builder.ToString().TrimEnd();
    }

    // "0" clears; the request shape carries points as a JsonElement so a PUT can tell "clear" from "leave".
    private static System.Text.Json.JsonElement PointsElement(int points)
    {
        using var document = System.Text.Json.JsonDocument.Parse(points <= 0 ? "null" : points.ToString(CultureInfo.InvariantCulture));
        return document.RootElement.Clone();
    }

    private static List<string>? SplitTags(string? tags) =>
        tags is null ? null : tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static string Fail(string action, Exception ex)
    {
        // The model gets a readable sentence; the detail (paths, SQL) stays in the file log.
        Log.Warning(ex, "[Board] MCP tool failed to {Action}", action);
        return $"FAIL: could not {action}. See the VibeRails log for details.";
    }
}
