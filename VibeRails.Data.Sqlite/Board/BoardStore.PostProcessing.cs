using System.Data;
using Microsoft.Data.Sqlite;

namespace VibeRails.Services.Board;

/// <summary>
/// Card post-processing (board-post-processing/1, VIBE-185): one pass per card once it settles in
/// its board's last lane. <c>BoardCards.PostProcessed</c> defaults to 1, so cards that existed before
/// this migration, and cards an older binary inserts, are never processed; <see cref="InsertCardAsync"/>
/// writes 0. The pass's outcome (today only <c>GhostReason</c>) and <c>PostProcessed = 1</c> commit
/// together, and only while the card is exactly as the pass read it, so concurrent roots apply it once.
/// </summary>
public sealed partial class BoardStore
{
    private const string PostProcessingSchemaSql = """
        CREATE INDEX IF NOT EXISTS IX_BoardCards_PostProcessPending ON BoardCards(ColumnId) WHERE PostProcessed = 0;
        """;

    private static readonly BoardAuthor PostProcessingAuthor = BoardAuthor.System("VibeRails post-processing");

    // Lane k is its board's last lane (the one ReadColumnsAsync lists last: Position, then CreatedUTC)
    // and the board has another lane. A board with one lane has no "finished" lane.
    private const string LastOfSeveralLanesSql = """
        EXISTS (SELECT 1 FROM BoardColumns o WHERE o.BoardId = k.BoardId AND o.Id <> k.Id)
        AND NOT EXISTS (SELECT 1 FROM BoardColumns o WHERE o.BoardId = k.BoardId AND o.Id <> k.Id
            AND (o.Position > k.Position OR (o.Position = k.Position AND o.CreatedUTC > k.CreatedUTC)))
        """;

    /// <inheritdoc />
    public async Task<IReadOnlyList<BoardPostProcessingCandidate>> GetPostProcessingCandidatesAsync(DateTime settledBeforeUtc, int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0) return [];
        await using var connection = await OpenAsync(cancellationToken);
        var rows = new List<BoardPostProcessingCandidate>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                SELECT c.Id, c.ProjectPath, {CardKeySql}, c.Type, k.BoardId, b.Name, c.ColumnId, k.Name, c.UpdatedUTC,
                       (SELECT COUNT(*) FROM {AllSessionsSql} s WHERE s.CardId = c.Id),
                       (SELECT COUNT(*) FROM BoardCodeLinks l WHERE l.CardId = c.Id)
                FROM BoardCards c
                {CardPrefixJoinSql}
                JOIN BoardColumns k ON k.Id = c.ColumnId
                JOIN Boards b ON b.Id = k.BoardId
                WHERE c.PostProcessed = 0 AND c.DeletedUTC IS NULL AND c.UpdatedUTC <= $settled
                  AND NOT EXISTS (SELECT 1 FROM BoardSharedOrigins origin WHERE origin.BoardId = k.BoardId)
                  AND {LastOfSeveralLanesSql}
                ORDER BY c.UpdatedUTC, c.Id
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$settled", ToDb(settledBeforeUtc));
            command.Parameters.AddWithValue("$limit", limit);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new BoardPostProcessingCandidate(
                    CardId: reader.GetString(0),
                    ProjectPath: reader.GetString(1),
                    Key: reader.GetString(2),
                    Type: reader.GetString(3),
                    BoardId: reader.GetString(4),
                    BoardName: reader.GetString(5),
                    ColumnId: reader.GetString(6),
                    LaneName: reader.GetString(7),
                    UpdatedUtc: ParseDb(reader.GetString(8)),
                    SessionCount: reader.GetInt32(9),
                    CodeLinkCount: reader.GetInt32(10),
                    CommitShas: []));
            }
        }

        for (var i = 0; i < rows.Count; i++)
        {
            await using var commits = connection.CreateCommand();
            commits.CommandText = "SELECT Sha FROM BoardCommits WHERE CardId = $card ORDER BY LinkedUTC, Sha;";
            commits.Parameters.AddWithValue("$card", rows[i].CardId);
            var shas = new List<string>();
            await using var reader = await commits.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                shas.Add(reader.GetString(0));
            rows[i] = rows[i] with { CommitShas = shas,
                CodeLinks = await ReadCodeLinksAsync(connection, null, rows[i].CardId, cancellationToken) };
        }
        return rows;
    }

    /// <inheritdoc />
    public async Task<bool> CompletePostProcessingAsync(BoardPostProcessingCandidate candidate, BoardPostProcessingResult result,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        string? previousGhost;
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            // Lane and board names and the lane order change without touching the card, and the
            // decision rests on them too (a lane renamed Canceled, a new lane after this one).
            read.CommandText = $"""
                SELECT c.UpdatedUTC, c.GhostReason FROM BoardCards c
                JOIN BoardColumns k ON k.Id = c.ColumnId
                JOIN Boards b ON b.Id = k.BoardId
                WHERE c.Id = $id AND c.PostProcessed = 0 AND c.DeletedUTC IS NULL AND c.ColumnId = $column
                  AND k.Name = $lane AND b.Id = $board AND b.Name = $boardName
                  AND {LastOfSeveralLanesSql};
                """;
            read.Parameters.AddWithValue("$id", candidate.CardId);
            read.Parameters.AddWithValue("$column", candidate.ColumnId);
            read.Parameters.AddWithValue("$lane", candidate.LaneName);
            read.Parameters.AddWithValue("$board", candidate.BoardId);
            read.Parameters.AddWithValue("$boardName", candidate.BoardName);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            // Processed by another root, deleted, moved, touched, or its lane renamed or no longer
            // last since it was read: the decision may be stale. A card that still qualifies is read
            // afresh by a later pass.
            if (!await reader.ReadAsync(cancellationToken) || ParseDb(reader.GetString(0)) > candidate.UpdatedUtc)
                return false;
            previousGhost = reader.IsDBNull(1) ? null : reader.GetString(1);
        }

        var ghost = result.GhostReason ?? previousGhost;
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            // Neither UpdatedUTC nor the card's position changes: this is an annotation, not activity.
            update.CommandText = "UPDATE BoardCards SET PostProcessed = 1, GhostReason = $ghost WHERE Id = $id;";
            update.Parameters.AddWithValue("$ghost", (object?)ghost ?? DBNull.Value);
            update.Parameters.AddWithValue("$id", candidate.CardId);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await LogGhostChangedAsync(connection, transaction, candidate.CardId, previousGhost, ghost, PostProcessingAuthor,
            DateTime.UtcNow, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <inheritdoc />
    public async Task<BoardGhostCheck?> GetGhostCheckAsync(string projectPath, string cardId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: true);
        var card = await ReadCardAsync(connection, transaction, NormalizeProjectPath(projectPath), cardId, cancellationToken);
        if (card is null) return null;
        var lanes = await ReadColumnsAsync(connection, transaction, card.ProjectPath, card.BoardId, cancellationToken);
        var lane = lanes.Single(c => c.Id == card.ColumnId);
        var boards = await ReadBoardsAsync(connection, transaction, card.ProjectPath, cancellationToken);
        var board = boards.Single(b => b.Id == card.BoardId);
        var commits = await ReadCommitsAsync(connection, card.Id, cancellationToken, transaction);
        var links = await ReadCodeLinksAsync(connection, transaction, card.Id, cancellationToken);
        var sessions = await ReadSessionsAsync(connection, card.Id, cancellationToken, transaction: transaction);
        var snapshot = new BoardPostProcessingCandidate(card.Id, card.ProjectPath, card.Key, card.Type,
            card.BoardId, board.Name, card.ColumnId, lane.Name, card.UpdatedUtc, sessions.Count, links.Count,
            commits.Select(c => c.Sha).ToArray()) { CodeLinks = links };
        return new(snapshot, card.GhostReason, lanes.Count > 1 && lanes[^1].Id == card.ColumnId);
    }

    /// <inheritdoc />
    public async Task<bool> ApplyGhostCheckAsync(BoardGhostCheck check, string? ghostReason, CancellationToken cancellationToken = default)
    {
        var card = check.Card;
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        await RequireWritableCardAsync(connection, transaction, card.CardId, cancellationToken);
        // Compared as instants, like CompletePostProcessingAsync: the stored text need not be in
        // the exact round-trip spelling ToDb produces for the snapshot to still be current.
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT UpdatedUTC FROM BoardCards WHERE Id = $id;";
            read.Parameters.AddWithValue("$id", card.CardId);
            if (await read.ExecuteScalarAsync(cancellationToken) is not string stored || ParseDb(stored) != card.UpdatedUtc)
                return false;
        }
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        // Explicit dismissal, new evidence, lane edits and concurrent checks invalidate this result.
        update.CommandText = $"""
            UPDATE BoardCards SET GhostReason = $ghost
            WHERE Id = $id AND ProjectPath = $project{ProjectPathCollation} AND DeletedUTC IS NULL
              AND GhostReason IS $before AND ColumnId = $column AND Type = $type
              AND EXISTS (SELECT 1 FROM BoardColumns k JOIN Boards b ON b.Id = k.BoardId
                WHERE k.Id = $column AND k.Name = $lane AND b.Id = $board AND b.Name = $boardName
                  AND ({LastOfSeveralLanesSql}) = $finished);
            """;
        update.Parameters.AddWithValue("$ghost", (object?)ghostReason ?? DBNull.Value);
        update.Parameters.AddWithValue("$before", (object?)check.GhostReason ?? DBNull.Value);
        update.Parameters.AddWithValue("$id", card.CardId);
        update.Parameters.AddWithValue("$project", card.ProjectPath);
        update.Parameters.AddWithValue("$column", card.ColumnId);
        update.Parameters.AddWithValue("$type", card.Type);
        update.Parameters.AddWithValue("$lane", card.LaneName);
        update.Parameters.AddWithValue("$board", card.BoardId);
        update.Parameters.AddWithValue("$boardName", card.BoardName);
        update.Parameters.AddWithValue("$finished", check.IsFinished);
        if (await update.ExecuteNonQueryAsync(cancellationToken) == 0) return false;
        await LogGhostChangedAsync(connection, transaction, card.CardId, check.GhostReason, ghostReason,
            PostProcessingAuthor, DateTime.UtcNow, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// A local-only History entry when the ghost appears or is cleared. Never synced: the ghost is
    /// this machine's reading of its own sessions and Git checkout, not a shared card field.
    /// </summary>
    private static Task LogGhostChangedAsync(SqliteConnection connection, SqliteTransaction transaction, string cardId,
        string? before, string? after, BoardAuthor author, DateTime createdUtc, CancellationToken cancellationToken)
    {
        var log = new CardLogChanges();
        log.Ghost(before, after);
        return log.IsEmpty ? Task.CompletedTask
            : InsertLogEntryAsync(connection, transaction, cardId, author, BoardCommentKinds.Change,
                log.Summary, log.ToJson(), createdUtc, cancellationToken, localOnly: true);
    }
}
