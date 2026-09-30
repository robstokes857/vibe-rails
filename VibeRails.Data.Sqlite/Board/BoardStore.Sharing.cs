using System.Data;
using Microsoft.Data.Sqlite;

namespace VibeRails.Services.Board;

public sealed partial class BoardStore
{
    private const string SharedOriginsSchemaSql = """
        CREATE TABLE IF NOT EXISTS BoardSharedOrigins (
            BoardId TEXT PRIMARY KEY REFERENCES Boards(Id) ON DELETE CASCADE,
            RemoteBoardId TEXT NOT NULL UNIQUE,
            DestinationKey TEXT NOT NULL,
            KeyPrefix TEXT NOT NULL
        );
        """;

    /// <inheritdoc />
    public async Task<BoardRecord> ImportSharedBoardAsync(string projectPath, BoardRemoteDescriptor remote,
        string destinationKey, CancellationToken cancellationToken = default)
    {
        ValidateRemoteLayout(remote);
        if (remote.IsOwner) throw new BoardValidationException("Choose a board shared with you.");
        if (string.IsNullOrWhiteSpace(destinationKey)) throw new BoardValidationException("Sign in before adding a shared board.");
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        await using (var find = connection.CreateCommand())
        {
            find.Transaction = transaction;
            find.CommandText = "SELECT b.Id, b.ProjectPath, o.DestinationKey FROM BoardSharedOrigins o JOIN Boards b ON b.Id=o.BoardId WHERE o.RemoteBoardId=$remote";
            find.Parameters.AddWithValue("$remote", remote.RemoteBoardId);
            await using var reader = await find.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                if (!string.Equals(reader.GetString(1), project, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                    || reader.GetString(2) != destinationKey)
                    throw new BoardConflictException("This shared board is already linked to another project or account on this machine.");
                var id = reader.GetString(0);
                await reader.DisposeAsync();
                return (await ReadBoardAsync(connection, transaction, project, id, cancellationToken))!;
            }
        }
        var now = DateTime.UtcNow;
        var boardId = NewId("brd");
        var position = (int)await ScalarLongAsync(connection, transaction,
            $"SELECT COUNT(*) FROM Boards WHERE ProjectPath=$project{ProjectPathCollation}", ("$project", project), cancellationToken);
        await ExecuteSharingAsync(connection, transaction, """
            INSERT INTO Boards (Id,ProjectPath,Name,Position,CreatedUTC,UpdatedUTC,DisplayPrefix)
            VALUES ($board,$project,$name,$position,$now,$now,$prefix);
            INSERT INTO BoardSharedOrigins (BoardId,RemoteBoardId,DestinationKey,KeyPrefix) VALUES ($board,$remote,$destination,$key);
            INSERT INTO BoardSyncLinks (BoardId,RemoteBoardId,Cursor,Enabled,CreatedUTC,UpdatedUTC,DestinationKey,ActivitySchema,LayoutHash)
            VALUES ($board,$remote,0,1,$now,$now,$destination,0,$layout);
            """, cancellationToken, ("$board", boardId), ("$project", project), ("$name", remote.Name), ("$position", position),
            ("$now", ToDb(now)), ("$prefix", remote.DisplayPrefix ?? remote.KeyPrefix), ("$remote", remote.RemoteBoardId),
            ("$destination", destinationKey), ("$key", remote.KeyPrefix),
            ("$layout", BoardLayoutHash.Compute(remote.Name, remote.KeyPrefix, remote.DisplayPrefix ?? remote.KeyPrefix, remote.Lanes)));
        foreach (var lane in remote.Lanes.OrderBy(l => l.Position))
        {
            if (await ColumnExistsAsync(connection, transaction, lane.Id, cancellationToken))
                throw new BoardConflictException("A lane identity already exists on this machine. This board cannot be imported twice.");
            await InsertColumnAsync(connection, transaction, lane.Id, project, boardId, lane.Name, lane.Position,
                lane.Color ?? "", now, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return new(boardId, project, remote.Name, position, now, now, remote.DisplayPrefix ?? remote.KeyPrefix);
    }

    /// <inheritdoc />
    public async Task<string?> ApplyRemoteLayoutAsync(string projectPath, string boardId, BoardRemoteDescriptor remote,
        BoardRecord expectedBoard, IReadOnlyList<BoardColumnRecord> expectedColumns, CancellationToken cancellationToken = default)
    {
        ValidateRemoteLayout(remote);
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var board = await ReadBoardAsync(connection, transaction, project, boardId, cancellationToken);
        if (board is null || board.Name != expectedBoard.Name || board.DisplayPrefix != expectedBoard.DisplayPrefix) return null;
        var columns = await ReadSharingColumnsAsync(connection, transaction, project, boardId, cancellationToken);
        static string Signature(IEnumerable<BoardColumnRecord> rows) => string.Join('\n', rows.OrderBy(c => c.Position)
            .Select(c => $"{c.Id}\t{c.Name}\t{c.Color}\t{c.Position}"));
        if (Signature(columns) != Signature(expectedColumns)) return null;
        var unchangedHash = BoardLayoutHash.Compute(board.Name, remote.KeyPrefix, board.EffectiveDisplayPrefix,
            columns.Select(c => new BoardRemoteLane(c.Id, c.Name, c.Color, c.Position)));
        if (unchangedHash == BoardLayoutHash.Compute(remote.Name, remote.KeyPrefix, remote.DisplayPrefix ?? remote.KeyPrefix, remote.Lanes))
            return unchangedHash;
        var now = DateTime.UtcNow;
        await ExecuteSharingAsync(connection, transaction,
            "UPDATE Boards SET Name=$name, DisplayPrefix=$prefix, UpdatedUTC=$now WHERE Id=$board;", cancellationToken,
            ("$name", remote.Name), ("$prefix", remote.DisplayPrefix ?? remote.KeyPrefix), ("$now", ToDb(now)), ("$board", boardId));
        foreach (var lane in remote.Lanes.OrderBy(l => l.Position))
        {
            if (columns.Any(c => c.Id == lane.Id))
                await ExecuteSharingAsync(connection, transaction,
                    "UPDATE BoardColumns SET Name=$name, Color=$color, Position=$position, UpdatedUTC=$now WHERE Id=$id AND BoardId=$board;",
                    cancellationToken, ("$name", lane.Name), ("$color", lane.Color ?? ""), ("$position", lane.Position), ("$now", ToDb(now)), ("$id", lane.Id), ("$board", boardId));
            else
            {
                if (await ColumnExistsAsync(connection, transaction, lane.Id, cancellationToken))
                    throw new BoardConflictException("The remote lane identity belongs to another local board.");
                await InsertColumnAsync(connection, transaction, lane.Id, project, boardId, lane.Name, lane.Position, lane.Color ?? "", now, cancellationToken);
            }
        }
        foreach (var removed in columns.Where(c => remote.Lanes.All(l => l.Id != c.Id)))
        {
            // A bounded pull may still owe us card moves. Keep that lane until its live cards
            // have moved, then retire it without deleting any retained card or discussion rows.
            if (await ScalarLongAsync(connection, transaction,
                "SELECT COUNT(*) FROM BoardCards WHERE ColumnId=$id AND DeletedUTC IS NULL", ("$id", removed.Id), cancellationToken) > 0) continue;
            await ExecuteSharingAsync(connection, transaction,
                "UPDATE BoardCards SET ColumnId=$fallback WHERE ColumnId=$id; DELETE FROM BoardColumns WHERE Id=$id AND BoardId=$board;",
                cancellationToken, ("$fallback", remote.Lanes[0].Id), ("$id", removed.Id), ("$board", boardId));
        }
        var finalColumns = await ReadSharingColumnsAsync(connection, transaction, project, boardId, cancellationToken);
        var hash = BoardLayoutHash.Compute(remote.Name, remote.KeyPrefix, remote.DisplayPrefix ?? remote.KeyPrefix,
            finalColumns.Select(c => new BoardRemoteLane(c.Id, c.Name, c.Color, c.Position)));
        await ExecuteSharingAsync(connection, transaction, "UPDATE BoardSyncLinks SET LayoutHash=$hash WHERE BoardId=$board;",
            cancellationToken, ("$hash", hash), ("$board", boardId));
        await transaction.CommitAsync(cancellationToken);
        return hash;
    }

    /// <inheritdoc />
    public async Task<bool> RestoreSharedCardAsync(string projectPath, string cardId, BoardAuthor author, BoardSyncStamp stamp,
        CancellationToken cancellationToken = default)
    {
        await using var db = await OpenAsync(cancellationToken);
        await using var tx = db.BeginTransaction(IsolationLevel.Serializable);
        var card = await ReadCardAsync(db, tx, NormalizeProjectPath(projectPath), cardId, cancellationToken, includeDeleted: true);
        if (card is null || stamp.BoardId != card.BoardId || await ScalarLongAsync(db, tx,
            "SELECT COUNT(*) FROM BoardSharedOrigins WHERE BoardId=$board", ("$board", card.BoardId), cancellationToken) != 1) return false;
        if (await HasSyncStampAsync(db, tx, stamp, cancellationToken)) return true;
        var later = await ScalarLongAsync(db, tx, """
            SELECT COUNT(*) FROM BoardSyncLog WHERE CardId=$card AND Kind IN ('deleted','restored')
                AND (RemoteSeq IS NULL OR RemoteSeq=-1 OR RemoteSeq>$seq)
                AND (SyncBoardId IS NULL OR SyncBoardId=$board);
            """, ("$card", cardId), cancellationToken, ("$seq", stamp.RemoteSeq), ("$board", card.BoardId));
        if (later == 0)
            await ExecuteSharingAsync(db, tx, "UPDATE BoardCards SET DeletedUTC=NULL, UpdatedUTC=$now WHERE Id=$card;",
                cancellationToken, ("$now", ToDb(stamp.CreatedUtc)), ("$card", cardId));
        await InsertLogEntryAsync(db, tx, cardId, author, "restored", stamp.Body ?? "Returned to board", stamp.Changes,
            stamp.CreatedUtc, cancellationToken, stamp);
        await tx.CommitAsync(cancellationToken);
        return true;
    }

    private static async Task<bool> ColumnExistsAsync(SqliteConnection db, SqliteTransaction tx, string id, CancellationToken ct)
        => await ScalarLongAsync(db, tx, "SELECT COUNT(*) FROM BoardColumns WHERE Id=$id", ("$id", id), ct) != 0;

    private static async Task<List<BoardColumnRecord>> ReadSharingColumnsAsync(SqliteConnection db, SqliteTransaction tx,
        string project, string board, CancellationToken ct)
    {
        await using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = "SELECT Id,Name,Position,Color,CreatedUTC,UpdatedUTC FROM BoardColumns WHERE BoardId=$board ORDER BY Position";
        command.Parameters.AddWithValue("$board", board);
        var rows = new List<BoardColumnRecord>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) rows.Add(new(reader.GetString(0), project, reader.GetString(1), reader.GetInt32(2), reader.GetString(3),
            ParseDb(reader.GetString(4)), ParseDb(reader.GetString(5)), board));
        return rows;
    }

    private static async Task ExecuteSharingAsync(SqliteConnection db, SqliteTransaction tx, string sql, CancellationToken ct,
        params (string Key, object? Value)[] args)
    {
        await using var command = db.CreateCommand(); command.Transaction = tx; command.CommandText = sql;
        foreach (var (key, value) in args) command.Parameters.AddWithValue(key, value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static void ValidateRemoteLayout(BoardRemoteDescriptor remote)
    {
        static bool Opaque(string? value) => value is { Length: > 0 and <= 64 } && char.IsAsciiLetterOrDigit(value[0]) && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
        if (!Guid.TryParse(remote.RemoteBoardId, out _) || string.IsNullOrWhiteSpace(remote.Name) || remote.Name.Length > 200
            || remote.Lanes is not { Count: > 0 and <= 100 } || remote.Lanes.Any(l => l is null || !Opaque(l.Id)
                || string.IsNullOrWhiteSpace(l.Name) || l.Name.Length > 80 || l.Color is { Length: > 32 })
            || remote.Lanes.Select(l => l.Id).Distinct(StringComparer.Ordinal).Count() != remote.Lanes.Count)
            throw new BoardValidationException("The server returned an invalid board layout.");
        BoardDisplayIds.NormalizePrefix(remote.KeyPrefix);
        if (remote.DisplayPrefix is not null) BoardDisplayIds.NormalizePrefix(remote.DisplayPrefix);
    }
}
