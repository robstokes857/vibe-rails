using System.Data;
using System.Text.Json.Nodes;

namespace VibeRails.Services.Board;

public sealed partial class BoardStore
{
    /// <inheritdoc />
    public async Task<BoardJiraConnectionRecord> EnsureDedicatedJiraBoardAsync(
        string projectPath, string connectionId, CancellationToken cancellationToken = default)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var db = await OpenAsync(cancellationToken);
        await using var transaction = db.BeginTransaction(IsolationLevel.Serializable);
        BoardJiraConnectionRecord current;
        await using (var read = db.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = JiraConnectionSelect + $" WHERE Id = $id AND ProjectPath = $project{ProjectPathCollation};";
            read.Parameters.AddWithValue("$id", connectionId);
            read.Parameters.AddWithValue("$project", project);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new BoardValidationException("The Jira connection changed. Reopen Board settings and retry.");
            current = ReadJiraConnection(reader);
        }
        if (current.DedicatedBoard) return current;

        var lanes = await ReadColumnsAsync(db, transaction, project, current.BoardId, cancellationToken);
        if (lanes.Count == 0) throw new BoardValidationException("The Jira source board no longer exists.");
        var name = "Jira · " + (current.JiraBoardName ?? current.JiraBoardId ?? new Uri(current.SiteUrl).Host.Split('.')[0]);
        if (name.Length > 60) name = name[..60];
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
        var columnMap = current.ColumnMap;
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
                  AND EXISTS (SELECT 1 FROM BoardJiraLinks j WHERE j.CardId = c.Id AND j.SiteId = $id)
                ORDER BY c.Flagged DESC, c.Position, c.Number;
                """;
            read.Parameters.AddWithValue("$project", project);
            read.Parameters.AddWithValue("$board", current.BoardId);
            read.Parameters.AddWithValue("$id", current.Id);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) cards.Add((reader.GetString(0), reader.GetString(1)));
        }
        foreach (var card in cards)
            await MoveCardInTransactionAsync(db, transaction, project, card.Id, laneMap[card.Column], int.MaxValue,
                skipLaneAutomations: true, cancellationToken, BoardAuthor.System("Jira"));

        var overflow = current.OverflowColumnId is { } old && laneMap.TryGetValue(old, out var mapped) ? mapped : null;
        await using (var move = db.CreateCommand())
        {
            move.Transaction = transaction;
            move.CommandText = """
                UPDATE BoardJiraConnections SET BoardId = $board, DedicatedBoard = 1,
                    ColumnMap = $map, OverflowColumnId = $overflow WHERE Id = $id;
                """;
            move.Parameters.AddWithValue("$board", destination.Id);
            move.Parameters.AddWithValue("$map", (object?)columnMap ?? DBNull.Value);
            move.Parameters.AddWithValue("$overflow", (object?)overflow ?? DBNull.Value);
            move.Parameters.AddWithValue("$id", current.Id);
            await move.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return current with { BoardId = destination.Id, DedicatedBoard = true, ColumnMap = columnMap, OverflowColumnId = overflow };
    }
}
