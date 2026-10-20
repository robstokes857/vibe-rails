using System.Data;

namespace VibeRails.Services.Board;

public sealed partial class BoardStore
{
    /// <inheritdoc />
    public async Task<BoardAdoConnectionRecord> EnsureDedicatedAdoBoardAsync(
        string projectPath, string connectionId, CancellationToken cancellationToken = default, bool newBoard = false)
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
        // A team board is named for its team, a saved query (no team) for the query.
        var name = DedicatedAdoBoardName(current);
        if (current.DedicatedBoard)
        {
            // Save creates the board before Connect discovers the query's name. Replace only
            // that provisional project label, in the same transaction as the name check, so a
            // concurrent user rename wins. No startup backfill or custom-name rewrite.
            var provisional = DedicatedAdoBoardName(current with { AdoBoardName = null });
            if (current.QueryId is not null && !string.IsNullOrWhiteSpace(current.AdoBoardName) && name != provisional)
            {
                await using var rename = db.CreateCommand();
                rename.Transaction = transaction;
                rename.CommandText = $"""
                    UPDATE Boards SET Name = $name, UpdatedUTC = $now
                    WHERE Id = $board AND ProjectPath = $project{ProjectPathCollation} AND Name = $provisional;
                    """;
                rename.Parameters.AddWithValue("$name", name);
                rename.Parameters.AddWithValue("$now", ToDb(DateTime.UtcNow));
                rename.Parameters.AddWithValue("$board", current.BoardId);
                rename.Parameters.AddWithValue("$project", project);
                rename.Parameters.AddWithValue("$provisional", provisional);
                await rename.ExecuteNonQueryAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            return current;
        }

        var target = await DedicateBoardAsync(db, transaction, project, current.BoardId, name,
            current.ColumnMap, current.OverflowColumnId,
            "EXISTS (SELECT 1 FROM BoardAdoLinks a WHERE a.CardId = c.Id AND a.ConnectionId = $connection)", current.Id,
            BoardAuthor.System(AdoAuthorLabel), "The Azure DevOps source board no longer exists.", newBoard, cancellationToken);

        await using (var move = db.CreateCommand())
        {
            move.Transaction = transaction;
            move.CommandText = """
                UPDATE BoardAdoConnections SET BoardId = $board, DedicatedBoard = 1,
                    ColumnMap = $map, OverflowColumnId = $overflow WHERE Id = $id;
                """;
            move.Parameters.AddWithValue("$board", target.BoardId);
            move.Parameters.AddWithValue("$map", (object?)target.ColumnMap ?? DBNull.Value);
            move.Parameters.AddWithValue("$overflow", (object?)target.OverflowColumnId ?? DBNull.Value);
            move.Parameters.AddWithValue("$id", current.Id);
            await move.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return current with
        {
            BoardId = target.BoardId, DedicatedBoard = true, ColumnMap = target.ColumnMap, OverflowColumnId = target.OverflowColumnId
        };
    }

    private static string DedicatedAdoBoardName(BoardAdoConnectionRecord connection)
    {
        var name = "Azure DevOps · " + (connection.Team ?? connection.AdoBoardName ?? connection.Project);
        return name.Length <= MaxDedicatedBoardNameLength ? name : name[..MaxDedicatedBoardNameLength];
    }
}
