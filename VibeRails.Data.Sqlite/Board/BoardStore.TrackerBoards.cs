using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace VibeRails.Services.Board;

public sealed partial class BoardStore
{
    private const int MaxDedicatedBoardNameLength = 60;

    /// <summary>The connection's dedicated board, and its lane choices and overflow lane on that
    /// board's lanes: the source board's own when it was taken over, remapped when cloned.</summary>
    private sealed record DedicatedBoardTarget(string BoardId, string? ColumnMap, string? OverflowColumnId);

    /// <summary>
    /// The shared core of a tracker's dedicated board (Jira, Azure DevOps), inside the caller's
    /// transaction. A source board that <see cref="CanTakeOverBoardAsync"/> allows becomes the
    /// dedicated board itself, keeping its name and lanes, so a board made only to connect a
    /// tracker is not left behind empty. Otherwise, or with <paramref name="newBoard"/>, the
    /// source board is cloned (<see cref="CloneToDedicatedBoardAsync"/>).
    /// </summary>
    private static async Task<DedicatedBoardTarget> DedicateBoardAsync(
        SqliteConnection db, SqliteTransaction transaction, string project, string sourceBoardId, string name,
        string? columnMap, string? overflowColumnId, string importedCardSql, string connectionId,
        BoardAuthor author, string missingSourceMessage, bool newBoard, CancellationToken cancellationToken)
    {
        await RequireWritableBoardAsync(db, transaction, sourceBoardId, cancellationToken);
        if (!newBoard && await CanTakeOverBoardAsync(db, transaction, project, sourceBoardId, importedCardSql, connectionId, cancellationToken))
            return new DedicatedBoardTarget(sourceBoardId, columnMap, overflowColumnId);
        return await CloneToDedicatedBoardAsync(db, transaction, project, sourceBoardId, name, columnMap,
            overflowColumnId, importedCardSql, connectionId, author, missingSourceMessage, cancellationToken);
    }

    /// <summary>
    /// Whether the connection can take over its source board: nothing there would be lost or
    /// start running. The board holds no live card but the connection's own imports, selects no
    /// lane Automation (every import would trigger it), has no other tracker connection, has never
    /// been published, shared or opted in to viberails.ai sync, and another local board remains in
    /// the project.
    /// </summary>
    private static async Task<bool> CanTakeOverBoardAsync(
        SqliteConnection db, SqliteTransaction transaction, string project, string boardId,
        string importedCardSql, string connectionId, CancellationToken cancellationToken)
    {
        await using var check = db.CreateCommand();
        check.Transaction = transaction;
        check.CommandText = $"""
            SELECT 1 FROM Boards b
            WHERE b.Id = $board AND b.ProjectPath = $project{ProjectPathCollation} AND b.SyncEnabled = 0
              AND NOT EXISTS (SELECT 1 FROM BoardCards c JOIN BoardColumns lane ON lane.Id = c.ColumnId
                  WHERE lane.BoardId = b.Id AND c.DeletedUTC IS NULL AND NOT ({importedCardSql}))
              AND NOT EXISTS (SELECT 1 FROM BoardLaneAutomations a JOIN BoardColumns lane ON lane.Id = a.ColumnId
                  WHERE lane.BoardId = b.Id AND a.JobId IS NOT NULL)
              AND NOT EXISTS (SELECT 1 FROM BoardLaneAdditionalAutomations a JOIN BoardColumns lane ON lane.Id = a.ColumnId
                  WHERE lane.BoardId = b.Id)
              AND NOT EXISTS (SELECT 1 FROM BoardJiraConnections j WHERE j.BoardId = b.Id AND j.Id <> $connection)
              AND NOT EXISTS (SELECT 1 FROM BoardAdoConnections o WHERE o.BoardId = b.Id AND o.Id <> $connection)
              AND NOT EXISTS (SELECT 1 FROM BoardSyncLinks s WHERE s.BoardId = b.Id)
              AND NOT EXISTS (SELECT 1 FROM BoardSharedOrigins s WHERE s.BoardId = b.Id)
              AND EXISTS (SELECT 1 FROM Boards other
                  WHERE other.ProjectPath = $project{ProjectPathCollation} AND other.Id <> b.Id
                    AND NOT EXISTS (SELECT 1 FROM BoardJiraConnections j WHERE j.BoardId = other.Id AND j.DedicatedBoard = 1)
                    AND NOT EXISTS (SELECT 1 FROM BoardAdoConnections o WHERE o.BoardId = other.Id AND o.DedicatedBoard = 1));
            """;
        check.Parameters.AddWithValue("$board", boardId);
        check.Parameters.AddWithValue("$project", project);
        check.Parameters.AddWithValue("$connection", connectionId);
        return await check.ExecuteScalarAsync(cancellationToken) is not null;
    }

    /// <summary>
    /// Creates an unpublished board named <paramref name="name"/> after the project's other
    /// boards, clones the source board's lanes without Automations, remaps explicit lane choices
    /// in <paramref name="columnMap"/> (automatic ones keep matching the cloned names) and the
    /// overflow lane, and moves the live cards on the source board that
    /// <paramref name="importedCardSql"/> selects through the ordinary log-transfer path, without
    /// lane Automations. <paramref name="importedCardSql"/> is a constant condition on card
    /// <c>c</c> that may use <c>$connection</c>.
    /// </summary>
    private static async Task<DedicatedBoardTarget> CloneToDedicatedBoardAsync(
        SqliteConnection db, SqliteTransaction transaction, string project, string sourceBoardId, string name,
        string? columnMap, string? overflowColumnId, string importedCardSql, string connectionId,
        BoardAuthor author, string missingSourceMessage, CancellationToken cancellationToken)
    {
        var lanes = await ReadColumnsAsync(db, transaction, project, sourceBoardId, cancellationToken);
        if (lanes.Count == 0) throw new BoardValidationException(missingSourceMessage);
        if (name.Length > MaxDedicatedBoardNameLength) name = name[..MaxDedicatedBoardNameLength];
        int position;
        await using (var count = db.CreateCommand())
        {
            count.Transaction = transaction;
            count.CommandText = $"SELECT COALESCE(MAX(Position), -1) + 1 FROM Boards WHERE ProjectPath = $project{ProjectPathCollation};";
            count.Parameters.AddWithValue("$project", project);
            position = Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken));
        }
        var destination = await InsertBoardWithDefaultLanesAsync(db, transaction, project, name, position,
            cancellationToken, createDefaultLanes: false);
        var laneMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var lane in lanes)
        {
            var id = NewId("col");
            laneMap[lane.Id] = id;
            await InsertColumnAsync(db, transaction, id, project, destination.Id, lane.Name, lane.Position,
                lane.Color, DateTime.UtcNow, cancellationToken);
        }

        // Preserve explicit lane choices. Automatic choices keep matching the cloned lane names.
        if (columnMap is not null && JsonNode.Parse(columnMap) is JsonArray columns)
        {
            foreach (var item in columns.OfType<JsonObject>())
                if (item["lane"] is JsonValue value && value.TryGetValue<string>(out var oldLane)
                    && laneMap.TryGetValue(oldLane, out var newLane))
                    item["lane"] = JsonValue.Create(newLane);
            columnMap = columns.ToJsonString();
        }

        var cards = new List<(string Id, string Column)>();
        await using (var read = db.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = $"""
                SELECT c.Id, c.ColumnId FROM BoardCards c
                JOIN BoardColumns lane ON lane.Id = c.ColumnId
                WHERE c.ProjectPath = $project{ProjectPathCollation} AND c.DeletedUTC IS NULL
                  AND lane.BoardId = $board
                  AND {importedCardSql}
                ORDER BY c.Flagged DESC, c.Position, c.Number;
                """;
            read.Parameters.AddWithValue("$project", project);
            read.Parameters.AddWithValue("$board", sourceBoardId);
            read.Parameters.AddWithValue("$connection", connectionId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) cards.Add((reader.GetString(0), reader.GetString(1)));
        }
        foreach (var card in cards)
            await MoveCardInTransactionAsync(db, transaction, project, card.Id, laneMap[card.Column], int.MaxValue,
                skipLaneAutomations: true, cancellationToken, author);

        var overflow = overflowColumnId is { } old && laneMap.TryGetValue(old, out var mapped) ? mapped : null;
        return new DedicatedBoardTarget(destination.Id, columnMap, overflow);
    }
}
