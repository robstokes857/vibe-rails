using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace VibeRails.Services.Board;

public sealed partial class BoardStore
{
    private const int MaxDedicatedBoardNameLength = 60;

    /// <summary>What <see cref="CloneToDedicatedBoardAsync"/> made: the new board, and the
    /// connection's lane choices and overflow lane remapped onto its cloned lanes.</summary>
    private sealed record DedicatedBoardClone(string BoardId, string? ColumnMap, string? OverflowColumnId);

    /// <summary>
    /// The shared core of a tracker's dedicated board (Jira, Azure DevOps), inside the caller's
    /// transaction. Creates an unpublished board named <paramref name="name"/> after the project's
    /// other boards, clones the source board's lanes without Automations, remaps explicit lane
    /// choices in <paramref name="columnMap"/> (automatic ones keep matching the cloned names) and
    /// the overflow lane, and moves the live cards on the source board that
    /// <paramref name="importedCardSql"/> selects through the ordinary log-transfer path, without
    /// lane Automations. <paramref name="importedCardSql"/> is a constant condition on card
    /// <c>c</c> that may use <c>$connection</c>.
    /// </summary>
    private static async Task<DedicatedBoardClone> CloneToDedicatedBoardAsync(
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
        return new DedicatedBoardClone(destination.Id, columnMap, overflow);
    }
}
