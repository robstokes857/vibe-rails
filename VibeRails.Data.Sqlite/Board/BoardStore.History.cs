using Microsoft.Data.Sqlite;

namespace VibeRails.Services.Board;

public sealed partial class BoardStore
{
    // Board/lane changes are separate from comments and notes. These additive triggers also
    // capture older binaries' layout writes, without rewriting any historical rows. A lane's
    // BoardId is NULL until ReconcileDerivedRows adopts it, so the lane triggers COALESCE it:
    // a trigger must never make an older binary's lane write fail on BoardHistory's NOT NULL.
    private const string BoardHistorySchemaSql = """
        CREATE TABLE IF NOT EXISTS BoardHistory (
            Id TEXT PRIMARY KEY,
            BoardId TEXT NOT NULL,
            ProjectPath TEXT NOT NULL,
            Kind TEXT NOT NULL,
            Body TEXT NOT NULL,
            CreatedUTC TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS IX_BoardHistory_BoardTime ON BoardHistory(BoardId, CreatedUTC);
        CREATE TRIGGER IF NOT EXISTS Boards_HistoryCreated AFTER INSERT ON Boards BEGIN
            INSERT INTO BoardHistory VALUES ('bh_' || lower(hex(randomblob(16))), NEW.Id, NEW.ProjectPath,
                'created', 'Created board: ' || NEW.Name, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
        END;
        CREATE TRIGGER IF NOT EXISTS Boards_HistoryName AFTER UPDATE OF Name ON Boards
        WHEN OLD.Name IS NOT NEW.Name BEGIN
            INSERT INTO BoardHistory VALUES ('bh_' || lower(hex(randomblob(16))), NEW.Id, NEW.ProjectPath,
                'change', 'Board name: ' || OLD.Name || ' → ' || NEW.Name, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
        END;
        CREATE TRIGGER IF NOT EXISTS BoardColumns_HistoryCreated AFTER INSERT ON BoardColumns BEGIN
            INSERT INTO BoardHistory VALUES ('bh_' || lower(hex(randomblob(16))), COALESCE(NEW.BoardId, ''), NEW.ProjectPath,
                'created', 'Created lane: ' || NEW.Name, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
        END;
        CREATE TRIGGER IF NOT EXISTS BoardColumns_HistoryChanged AFTER UPDATE OF Name, Color, Position ON BoardColumns
        WHEN OLD.Name IS NOT NEW.Name OR OLD.Color IS NOT NEW.Color OR OLD.Position IS NOT NEW.Position BEGIN
            INSERT INTO BoardHistory VALUES ('bh_' || lower(hex(randomblob(16))), COALESCE(NEW.BoardId, ''), NEW.ProjectPath,
                'change', 'Lane ' || OLD.Name || ': ' ||
                CASE WHEN OLD.Name IS NOT NEW.Name THEN 'name → ' || NEW.Name || '; ' ELSE '' END ||
                CASE WHEN OLD.Color IS NOT NEW.Color THEN 'colour → ' || NEW.Color || '; ' ELSE '' END ||
                CASE WHEN OLD.Position IS NOT NEW.Position THEN 'position → ' || NEW.Position ELSE '' END,
                strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
        END;
        CREATE TRIGGER IF NOT EXISTS BoardColumns_HistoryDeleted AFTER DELETE ON BoardColumns BEGIN
            INSERT INTO BoardHistory VALUES ('bh_' || lower(hex(randomblob(16))), COALESCE(OLD.BoardId, ''), OLD.ProjectPath,
                'deleted', 'Deleted lane: ' || OLD.Name, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
        END;
        """;

    // board/18: board/15 shipped these triggers without the COALESCE, so an existing database has
    // to drop them before BoardHistorySchemaSql's IF NOT EXISTS can recreate the corrected text.
    private const string BoardHistoryTriggerResetSql = """
        DROP TRIGGER IF EXISTS Boards_HistoryCreated;
        DROP TRIGGER IF EXISTS Boards_HistoryName;
        DROP TRIGGER IF EXISTS BoardColumns_HistoryCreated;
        DROP TRIGGER IF EXISTS BoardColumns_HistoryChanged;
        DROP TRIGGER IF EXISTS BoardColumns_HistoryDeleted;
        """;

    public async Task<IReadOnlyList<BoardCommentRecord>> GetCardHistoryAsync(string projectPath, string cardId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var card = await ReadCardAsync(connection, null, NormalizeProjectPath(projectPath), cardId, cancellationToken, includeDeleted: true);
        return card is null ? [] : await ReadHistoryAsync(connection, card.Id, cancellationToken);
    }

    public async Task<IReadOnlyList<BoardHistoryRecord>?> GetHistoryAsync(string projectPath, string boardId, string? cardId,
        int offset, CancellationToken cancellationToken = default)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        if (await ReadBoardAsync(connection, null, project, boardId, cancellationToken) is null) return null;
        if (cardId is not null)
        {
            var card = await ReadCardAsync(connection, null, project, cardId, cancellationToken, includeDeleted: true);
            if (card is null || card.BoardId != boardId) return null;
            cardId = card.Id;
        }
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT Id, CardKey, Kind, Author, Body, CreatedUTC, Changes, AuthorKind, SessionId FROM (
                SELECT h.Id, NULL AS CardKey, h.Kind, 'VibeRails' AS Author, h.Body, h.CreatedUTC, NULL AS Changes,
                    NULL AS AuthorKind, NULL AS SessionId,
                    1 AS LocalEntry, 0 AS RemoteSeq, h.rowid AS LocalOrder
                FROM BoardHistory h
                WHERE h.BoardId = $board AND h.ProjectPath = $project{ProjectPathCollation} AND $card IS NULL
                UNION ALL
                SELECT m.Id, {CardKeySql}, m.Kind, m.AuthorLabel AS Author, m.Body, m.CreatedUTC, m.Changes,
                    m.AuthorKind, m.SessionId,
                    CASE WHEN m.RemoteSeq > 0 THEN 0 ELSE 1 END, COALESCE(m.RemoteSeq, 0), m.rowid
                FROM BoardSyncLog m JOIN BoardCards c ON c.Id = m.CardId
                JOIN BoardColumns k ON k.Id = c.ColumnId {CardPrefixJoinSql}
                WHERE k.BoardId = $board AND c.ProjectPath = $project{ProjectPathCollation}
                    AND ($card IS NULL OR c.Id = $card) AND m.Kind IN ('created', 'change', 'deleted')
            ) ORDER BY LocalEntry DESC,
                CASE WHEN LocalEntry = 0 THEN RemoteSeq END DESC,
                CASE WHEN LocalEntry = 1 THEN julianday(CreatedUTC) END DESC,
                LocalOrder DESC, Id DESC LIMIT 101 OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$board", boardId);
        command.Parameters.AddWithValue("$project", project);
        command.Parameters.AddWithValue("$card", (object?)cardId ?? DBNull.Value);
        command.Parameters.AddWithValue("$offset", Math.Clamp(offset, 0, 1_000_000));
        var result = new List<BoardHistoryRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), ParseDb(reader.GetString(5)), reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8)));
        return result;
    }
}
