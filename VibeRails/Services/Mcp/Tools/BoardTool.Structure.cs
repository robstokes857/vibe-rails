using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using Serilog;
using VibeRails.DTOs;
using VibeRails.Services.Board;

namespace VibeRails.Services.Mcp.Tools;

/// <summary>
/// Lane and board structure (VIBE-342). The dashboard creates, renames, recolours and reorders
/// lanes and creates and renames boards; these tools call the same <see cref="IBoardService"/>
/// methods, so validation, the shared-copy refusal and the default-lane rule are the dashboard's.
/// Nothing here deletes, and a lane's on-entry Agents and Actions stay a dashboard setting.
/// The four tools are deliberately outside <c>BoardMcpAuthorization.ToolNames</c>: a card agent is
/// not pre-approved to restructure the board it works on. The service records no author for
/// these changes, so each one is written to the file log with the caller's name.
/// </summary>
public sealed partial class BoardTool
{
    /// <summary>In the descriptions, so an agent knows before it calls. The replies say what one change did.</summary>
    private const string LaneMeaningNote =
        "Lane names and order carry meaning: a lane whose name contains done, complete, ship or closed is treated as finished, "
        + "and cards that settle in the board's last lane get the completion check for landed code.";

    /// <summary>
    /// The Board's finished-lane convention: <c>BoardStore.IsCompletedLane</c> and the dashboard page
    /// lanes and total open work by ship, done and complete; the desktop-activity triggers add closed.
    /// </summary>
    internal static bool IsFinishedLaneName(string? name) =>
        name is not null && (name.Contains("ship", StringComparison.OrdinalIgnoreCase)
            || name.Contains("done", StringComparison.OrdinalIgnoreCase)
            || name.Contains("complete", StringComparison.OrdinalIgnoreCase)
            || name.Contains("closed", StringComparison.OrdinalIgnoreCase));

    [McpServerTool, Description("Create a lane on a local board, as the dashboard's + Lane does. Without position the lane is added after the board's last lane and becomes the last lane. Create a lane only when the user or the board's workflow asks for one. A lane's on-entry Agents and Actions are set in the dashboard. " + LaneMeaningNote)]
    public Task<string> CreateBoardColumn(
        [Description("Lane name (required).")] string name,
        [Description("Hex colour such as #3b82f6. Optional: grey when omitted.")] string? color = null,
        [Description(BoardArgumentHelp)] string? board = null,
        [Description("true: cards created without a lane land here from now on, replacing the board's current choice.")] bool defaultForNewCards = false,
        [Description("Place among the board's lanes, counted from 1 on the left. Optional: after the last lane.")] int? position = null,
        CancellationToken cancellationToken = default,
        McpServer? server = null) => StructureChangeAsync("create the lane", async () =>
    {
        if (string.IsNullOrWhiteSpace(name)) return "FAIL: a lane needs a name.";
        if (position is < 1) return "FAIL: position counts from 1.";
        var project = await projects.ResolveAsync(cancellationToken);
        var target = await ResolveBoardAsync(project, board, cancellationToken);
        if (target.Error is not null) return target.Error;
        if (await ResolveAuthorAsync(server, cancellationToken) is not { } author) return UnnamedClientHint;

        var lastBefore = await LastLaneAsync(target.Project, target.BoardId, cancellationToken);
        var lane = await service.CreateColumnAsync(target.Project,
            new CreateBoardColumnRequest(name, color, target.BoardId, defaultForNewCards), cancellationToken);
        Log.Information("[Board] MCP {Author} created lane {LaneId} {Lane} on board {BoardId} in {Project}", author.Label, lane.Id, lane.Name, lane.BoardId, target.Project);

        var reply = new StringBuilder("Created lane \"").Append(BoardPromptComposer.SanitizeLine(lane.Name, 120)).Append("\" (id ").Append(lane.Id).Append(')');
        if (target.BoardName is not null) reply.Append(" on board ").Append(BoardPromptComposer.SanitizeLine(target.BoardName, 120));
        if (position is int wanted)
        {
            var (place, count) = await PlaceLaneAsync(target.Project, lane.BoardId, lane.Id, wanted, cancellationToken);
            reply.Append(" at place ").Append(place).Append(" of ").Append(count);
        }
        else
            reply.Append(" as lane ").Append(lane.Position + 1);
        reply.Append(lane.IsDefaultForNewCards ? "; new cards land here." : ".");
        AppendNewLaneNotes(reply, lane, color);
        await AppendLastLaneChangeAsync(reply, target.Project, lane.BoardId, lastBefore, cancellationToken);
        return reply.ToString();
    });

    [McpServerTool, Description("Rename, recolour or move one lane, or make it the lane new cards land in, as the dashboard's lane settings and lane drag do. Pass only what should change. Cards stay in the lane. A lane's on-entry Agents and Actions are not changed here. " + LaneMeaningNote + " The reply says when a change alters either.")]
    public Task<string> UpdateBoardColumn(
        [Description("The lane: its id from list_board_columns, or its name on the board.")] string column,
        [Description("New lane name. Omitted keeps the name.")] string? name = null,
        [Description("New hex colour such as #a855f7. Omitted keeps the colour.")] string? color = null,
        [Description("true: new cards land in this lane; false: clear that choice from this lane. Omitted keeps it.")] bool? defaultForNewCards = null,
        [Description("New place among the board's lanes, counted from 1 on the left. Omitted keeps the order.")] int? position = null,
        [Description(BoardArgumentHelp)] string? board = null,
        CancellationToken cancellationToken = default,
        McpServer? server = null) => StructureChangeAsync("update the lane", async () =>
    {
        var fields = new UpdateBoardColumnRequest(name, color, defaultForNewCards);
        if (LaneUpdateError(column, fields, position) is { } invalid) return invalid;
        var project = await projects.ResolveAsync(cancellationToken);
        var target = await ResolveBoardAsync(project, board, cancellationToken);
        if (target.Error is not null) return target.Error;
        var lane = await service.FindColumnAsync(target.Project, column.Trim(), cancellationToken, target.BoardId);
        if (lane is null) return $"FAIL: lane not found: {column}. Use list_board_columns to see the lanes.";
        if (await ResolveAuthorAsync(server, cancellationToken) is not { } author) return UnnamedClientHint;

        var lastBefore = await LastLaneAsync(target.Project, lane.BoardId, cancellationToken);
        var reply = new StringBuilder();
        var laneName = lane.Name;
        if (fields != NoLaneFields)
        {
            var updated = await service.UpdateColumnAsync(target.Project, lane.Id, fields, cancellationToken);
            if (updated is null) return $"FAIL: lane not found: {column}.";
            Log.Information("[Board] MCP {Author} updated lane {LaneId} {Before} to {Lane} in {Project}", author.Label, lane.Id, lane.Name, updated.Name, target.Project);
            laneName = updated.Name;
            AppendLaneUpdate(reply, lane.Name, updated, fields);
        }
        if (position is int wanted)
        {
            var (place, count) = await PlaceLaneAsync(target.Project, lane.BoardId, lane.Id, wanted, cancellationToken);
            Log.Information("[Board] MCP {Author} moved lane {LaneId} {Lane} to place {Place} in {Project}", author.Label, lane.Id, laneName, place, target.Project);
            if (reply.Length > 0) reply.Append('\n');
            reply.Append("Moved lane \"").Append(BoardPromptComposer.SanitizeLine(laneName, 120)).Append("\" to place ").Append(place).Append(" of ").Append(count).Append('.');
        }
        await AppendLastLaneChangeAsync(reply, target.Project, lane.BoardId, lastBefore, cancellationToken);
        return reply.ToString();
    });

    [McpServerTool, Description("Create a board in the current project, as the dashboard's Create board does: it starts with the default lanes and stays local (not synced). Create one only when the user asks for a new board; cards belong on the existing board otherwise.")]
    public Task<string> CreateBoard(
        [Description("Board name (required).")] string name,
        [Description("Prefix for new cards' display IDs, such as SHOP. Optional: the repository's default.")] string? displayPrefix = null,
        CancellationToken cancellationToken = default,
        McpServer? server = null) => StructureChangeAsync("create the board", async () =>
    {
        if (string.IsNullOrWhiteSpace(name)) return "FAIL: a board needs a name.";
        var project = await projects.ResolveAsync(cancellationToken);
        if (await ResolveAuthorAsync(server, cancellationToken) is not { } author) return UnnamedClientHint;
        // First-use setup, as the dashboard's board list does: the project's own board stays its first board.
        await service.GetBoardsAsync(project, cancellationToken);
        var created = await service.CreateBoardAsync(project, new CreateBoardRequest(name, displayPrefix), cancellationToken);
        Log.Information("[Board] MCP {Author} created board {BoardId} {Board} in {Project}", author.Label, created.Id, created.Name, project);
        return $"Created board \"{BoardPromptComposer.SanitizeLine(created.Name, 120)}\" (id {created.Id}) in {project}"
            + $" with lanes: {string.Join(" → ", created.Columns.OrderBy(c => c.Position).Select(c => BoardPromptComposer.SanitizeLine(c.Name, 80)))}."
            + (created.DisplayPrefix is null ? "" : $" New cards are numbered {created.DisplayPrefix}-n.")
            + $"\nPass board={created.Id} to the other Board tools to work on it.";
    });

    [McpServerTool, Description("Rename a board or change the prefix of its new cards' display IDs, as Board settings does. Existing card IDs stay as they are. Pass only what should change.")]
    public Task<string> UpdateBoard(
        [Description("The board: its id or unambiguous name from list_boards.")] string board,
        [Description("New board name. Omitted keeps the name.")] string? name = null,
        [Description("New display ID prefix for cards created from now on. An empty string goes back to the repository's default. Omitted keeps it.")] string? displayPrefix = null,
        CancellationToken cancellationToken = default,
        McpServer? server = null) => StructureChangeAsync("update the board", async () =>
    {
        if (string.IsNullOrWhiteSpace(board)) return "FAIL: name the board to change (its id or name from list_boards).";
        if (name is null && displayPrefix is null) return "FAIL: nothing to change. Pass name or displayPrefix.";
        var project = await projects.ResolveAsync(cancellationToken);
        var target = await ResolveBoardAsync(project, board, cancellationToken);
        if (target.Error is not null) return target.Error;
        if (await ResolveAuthorAsync(server, cancellationToken) is not { } author) return UnnamedClientHint;
        var updated = await service.UpdateBoardAsync(target.Project, target.BoardId!, new UpdateBoardRequest(name, displayPrefix), cancellationToken);
        if (updated is null) return $"FAIL: board not found: {board}. Use list_boards to see the boards.";
        Log.Information("[Board] MCP {Author} updated board {BoardId} {Before} to {Board} in {Project}", author.Label, updated.Id, target.BoardName, updated.Name, target.Project);
        var renamed = name is not null && !string.Equals(target.BoardName, updated.Name, StringComparison.Ordinal);
        return $"Updated board \"{BoardPromptComposer.SanitizeLine(updated.Name, 120)}\" (id {updated.Id})"
            + (renamed ? $": renamed from \"{BoardPromptComposer.SanitizeLine(target.BoardName, 120)}\"" : "")
            + PrefixNote(displayPrefix, updated.DisplayPrefix);
    });

    /// <summary>The four tools fail one way: a refusal reads as the service worded it, anything else goes through <see cref="Fail"/>.</summary>
    private static async Task<string> StructureChangeAsync(string action, Func<Task<string>> change)
    {
        try { return await change(); }
        catch (Exception ex) when (ex is BoardValidationException or BoardConflictException) { return "FAIL: " + ex.Message; }
        catch (Exception ex) when (ex is not OperationCanceledException) { return Fail(action, ex); }
    }

    /// <summary>An update that names no field: record equality tells it from one that does.</summary>
    private static readonly UpdateBoardColumnRequest NoLaneFields = new();

    private static string? LaneUpdateError(string column, UpdateBoardColumnRequest fields, int? position)
    {
        if (string.IsNullOrWhiteSpace(column)) return "FAIL: name the lane to change (its id or name from list_board_columns).";
        if (fields == NoLaneFields && position is null) return "FAIL: nothing to change. Pass name, color, defaultForNewCards or position.";
        return position is < 1 ? "FAIL: position counts from 1." : null;
    }

    /// <summary>What a lane's saved fields became, and whether a rename changed the lane's kind.</summary>
    private static void AppendLaneUpdate(StringBuilder reply, string nameBefore, BoardColumnResponse updated, UpdateBoardColumnRequest asked)
    {
        reply.Append("Updated lane \"").Append(BoardPromptComposer.SanitizeLine(updated.Name, 120)).Append("\" (id ").Append(updated.Id).Append(')');
        var renamed = !string.Equals(nameBefore, updated.Name, StringComparison.Ordinal);
        if (renamed) reply.Append(": renamed from \"").Append(BoardPromptComposer.SanitizeLine(nameBefore, 120)).Append('"');
        if (asked.Color is not null) reply.Append("; colour ").Append(updated.Color);
        if (asked.IsDefaultForNewCards is not null) reply.Append(updated.IsDefaultForNewCards ? "; new cards land here" : "; new cards no longer land here");
        reply.Append('.');
        if (!renamed || IsFinishedLaneName(nameBefore) == IsFinishedLaneName(updated.Name)) return;
        reply.Append(IsFinishedLaneName(updated.Name)
            ? "\nThe new name makes it a finished lane: its cards leave the open totals."
            : "\nThe new name no longer makes it a finished lane: its cards count as open work again.");
    }

    private static void AppendNewLaneNotes(StringBuilder reply, BoardColumnResponse lane, string? askedColor)
    {
        // The dashboard's create falls back to grey for a colour it cannot read; an agent is told.
        if (!string.IsNullOrWhiteSpace(askedColor) && !string.Equals(askedColor.Trim(), lane.Color, StringComparison.OrdinalIgnoreCase))
            reply.Append("\nThe colour was not a hex colour like #3b82f6, so the lane is ").Append(lane.Color).Append('.');
        if (IsFinishedLaneName(lane.Name)) reply.Append("\nIts name makes it a finished lane: cards in it leave the open totals.");
    }

    /// <summary>The summary carries the prefix in effect, so a cleared one reads as the repository's default.</summary>
    private static string PrefixNote(string? asked, string? inEffect)
    {
        if (asked is null) return ".";
        if (inEffect is null) return "; new cards use the repository's default prefix.";
        return asked.Trim().Length == 0
            ? $"; new cards are numbered {inEffect}-n, the repository's default."
            : $"; new cards are numbered {inEffect}-n.";
    }

    /// <summary>
    /// The dashboard saves a board's whole lane order after a drag; so does this, with one lane moved.
    /// A place past the end means last. Returns the lane's 1-based place and the lane count.
    /// </summary>
    private async Task<(int Place, int Count)> PlaceLaneAsync(string project, string boardId, string laneId, int wanted, CancellationToken cancellationToken)
    {
        var lanes = (await service.GetColumnsAsync(project, cancellationToken, boardId)).Columns.OrderBy(c => c.Position).ToList();
        var ids = lanes.Select(c => c.Id).Where(id => id != laneId).ToList();
        var index = Math.Clamp(wanted, 1, lanes.Count) - 1;
        ids.Insert(index, laneId);
        await service.ReorderColumnsAsync(project, ids, cancellationToken, boardId);
        return (index + 1, lanes.Count);
    }

    private async Task<BoardColumnResponse?> LastLaneAsync(string project, string? boardId, CancellationToken cancellationToken) =>
        (await service.GetColumnsAsync(project, cancellationToken, boardId)).Columns.OrderBy(c => c.Position).LastOrDefault();

    /// <summary>The completion check follows the board's last lane, so a change of last lane is said, never implied.</summary>
    private async Task AppendLastLaneChangeAsync(StringBuilder reply, string project, string boardId, BoardColumnResponse? before, CancellationToken cancellationToken)
    {
        var after = await LastLaneAsync(project, boardId, cancellationToken);
        if (after is null || before is null || after.Id == before.Id) return;
        reply.Append("\nThe board's last lane is now \"").Append(BoardPromptComposer.SanitizeLine(after.Name, 120))
            .Append("\" (it was \"").Append(BoardPromptComposer.SanitizeLine(before.Name, 120))
            .Append("\"): cards that settle there get the completion check for landed code.");
    }
}
