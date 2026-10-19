using System.Data;

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

        var name = "Jira · " + (current.JiraBoardName ?? current.JiraBoardId ?? new Uri(current.SiteUrl).Host.Split('.')[0]);
        var clone = await CloneToDedicatedBoardAsync(db, transaction, project, current.BoardId, name,
            current.ColumnMap, current.OverflowColumnId,
            "EXISTS (SELECT 1 FROM BoardJiraLinks j WHERE j.CardId = c.Id AND j.SiteId = $connection)", current.Id,
            BoardAuthor.System("Jira"), "The Jira source board no longer exists.", cancellationToken);

        await using (var move = db.CreateCommand())
        {
            move.Transaction = transaction;
            move.CommandText = """
                UPDATE BoardJiraConnections SET BoardId = $board, DedicatedBoard = 1,
                    ColumnMap = $map, OverflowColumnId = $overflow WHERE Id = $id;
                """;
            move.Parameters.AddWithValue("$board", clone.BoardId);
            move.Parameters.AddWithValue("$map", (object?)clone.ColumnMap ?? DBNull.Value);
            move.Parameters.AddWithValue("$overflow", (object?)clone.OverflowColumnId ?? DBNull.Value);
            move.Parameters.AddWithValue("$id", current.Id);
            await move.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return current with
        {
            BoardId = clone.BoardId, DedicatedBoard = true, ColumnMap = clone.ColumnMap, OverflowColumnId = clone.OverflowColumnId
        };
    }
}
