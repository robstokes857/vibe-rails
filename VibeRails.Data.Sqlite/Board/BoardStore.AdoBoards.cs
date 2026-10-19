using System.Data;

namespace VibeRails.Services.Board;

public sealed partial class BoardStore
{
    /// <inheritdoc />
    public async Task<BoardAdoConnectionRecord> EnsureDedicatedAdoBoardAsync(
        string projectPath, string connectionId, CancellationToken cancellationToken = default)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var db = await OpenAsync(cancellationToken);
        await using var transaction = db.BeginTransaction(IsolationLevel.Serializable);
        BoardAdoConnectionRecord current;
        await using (var read = db.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = AdoConnectionSelect + $" WHERE Id = $id AND ProjectPath = $project{ProjectPathCollation};";
            read.Parameters.AddWithValue("$id", connectionId);
            read.Parameters.AddWithValue("$project", project);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new BoardValidationException("The Azure DevOps connection changed. Reopen Board settings and retry.");
            current = ReadAdoConnection(reader);
        }
        if (current.DedicatedBoard) return current;

        // A team board is named for its team, a saved query (no team) for the query.
        var name = "Azure DevOps · " + (current.Team ?? current.AdoBoardName ?? current.Project);
        var clone = await CloneToDedicatedBoardAsync(db, transaction, project, current.BoardId, name,
            current.ColumnMap, current.OverflowColumnId,
            "EXISTS (SELECT 1 FROM BoardAdoLinks a WHERE a.CardId = c.Id AND a.ConnectionId = $connection)", current.Id,
            BoardAuthor.System(AdoAuthorLabel), "The Azure DevOps source board no longer exists.", cancellationToken);

        await using (var move = db.CreateCommand())
        {
            move.Transaction = transaction;
            move.CommandText = """
                UPDATE BoardAdoConnections SET BoardId = $board, DedicatedBoard = 1,
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
