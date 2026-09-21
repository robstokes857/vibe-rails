using System.Data;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VibeRails.DTOs;
using VibeRails.Data.Sqlite;

namespace VibeRails.Services.Board;



/// <summary>
/// SQLite persistence for the kanban board. Singleton with its own connection string and its own
/// schema owner (the <see cref="VibeRails.DB.JobStore"/> pattern): connection-per-operation, busy
/// timeout on every open, and no dependency on <c>Repository.EnsureInitialized()</c> so the stdio
/// MCP host can construct it without running the dashboard's migration pass.
///
/// Every row is scoped by <c>ProjectPath</c> (the git root the board belongs to). Callers never
/// pass a project path they got from a request — see <see cref="IBoardProjectResolver"/>.
///
/// A project holds one or more <em>boards</em> (board/4): each lane belongs to exactly one board
/// and a card belongs to a board through its lane. Card numbers stay per project, so a key names
/// the same card on every board. An optional <c>boardId</c> of null means the project's default
/// board (the first by position), which is what every pre-board caller was talking to.
/// </summary>
public sealed partial class BoardStore : IBoardStore
{
    // Independent of state.db from the split onward; historical board/8 stamps generation 3.
    internal const int Generation = 3;
    private readonly string _connectionString;
    private readonly string _stateConnectionString;

    /// <param name="connectionString">board.db: every Board-owned table.</param>
    /// <param name="stateConnectionString">
    /// state.db: Jobs and Sessions lookups only. Required rather than defaulted so a host that
    /// forgets it fails to compile instead of failing at runtime with "no such table: Jobs".
    /// A test that models the pre-split single-file layout passes the same string twice.
    /// </param>
    public BoardStore(string connectionString, string stateConnectionString)
    {
        _connectionString = connectionString;
        _stateConnectionString = stateConnectionString;
        EnsureSchema();
    }

    // ------------------------------------------------------------------ boards

    /// <summary>What the one board every project starts with is called.</summary>
    public const string DefaultBoardName = "Main";

    public async Task<IReadOnlyList<BoardRecord>> GetBoardsAsync(string projectPath, CancellationToken cancellationToken = default)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        return await ReadBoardsAsync(connection, null, project, cancellationToken);
    }

    public async Task<BoardRecord?> GetBoardAsync(string projectPath, string boardId, CancellationToken cancellationToken = default)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        return await ReadBoardAsync(connection, null, project, boardId, cancellationToken);
    }

    public async Task<BoardRecord> CreateBoardAsync(string projectPath, string name, CancellationToken cancellationToken = default)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var position = (int)await ScalarLongAsync(connection, transaction,
            $"SELECT COUNT(*) FROM Boards WHERE ProjectPath = $project{ProjectPathCollation}",
            ("$project", project), cancellationToken);
        var board = await InsertBoardWithDefaultLanesAsync(connection, transaction, project, name, position, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return board;
    }

    public async Task<BoardRecord?> RenameBoardAsync(string projectPath, string boardId, string name, CancellationToken cancellationToken = default)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var existing = await ReadBoardAsync(connection, transaction, project, boardId, cancellationToken);
        if (existing is null)
            return null;
        var updated = existing with { Name = name, UpdatedUtc = DateTime.UtcNow };
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "UPDATE Boards SET Name = $name, UpdatedUTC = $updated WHERE Id = $id;";
            command.Parameters.AddWithValue("$name", updated.Name);
            command.Parameters.AddWithValue("$updated", ToDb(updated.UpdatedUtc));
            command.Parameters.AddWithValue("$id", updated.Id);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return updated;
    }

    public async Task<BoardDeleteResult?> DeleteBoardAsync(string projectPath, string boardId, CancellationToken cancellationToken = default)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var boards = await ReadBoardsAsync(connection, transaction, project, cancellationToken);
        var target = boards.FirstOrDefault(b => string.Equals(b.Id, boardId.Trim(), StringComparison.Ordinal));
        if (target is null)
            return null;
        if (boards.Count <= 1)
            throw new BoardConflictException("The project needs at least one board.");

        // Cards go with their lanes; the rails cascade off BoardCards like a single delete does.
        int cards, columns;
        await using (var deleteCards = connection.CreateCommand())
        {
            deleteCards.Transaction = transaction;
            deleteCards.CommandText = "DELETE FROM BoardCards WHERE ColumnId IN (SELECT Id FROM BoardColumns WHERE BoardId = $board);";
            deleteCards.Parameters.AddWithValue("$board", target.Id);
            cards = await deleteCards.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var deleteColumns = connection.CreateCommand())
        {
            deleteColumns.Transaction = transaction;
            deleteColumns.CommandText = "DELETE FROM BoardColumns WHERE BoardId = $board;";
            deleteColumns.Parameters.AddWithValue("$board", target.Id);
            columns = await deleteColumns.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var deleteBoard = connection.CreateCommand())
        {
            deleteBoard.Transaction = transaction;
            deleteBoard.CommandText = "DELETE FROM Boards WHERE Id = $board;";
            deleteBoard.Parameters.AddWithValue("$board", target.Id);
            await deleteBoard.ExecuteNonQueryAsync(cancellationToken);
        }
        var remaining = boards.Where(b => b.Id != target.Id).OrderBy(b => b.Position).ThenBy(b => b.CreatedUtc).Select(b => b.Id).ToList();
        await WriteBoardPositionsAsync(connection, transaction, remaining, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new BoardDeleteResult(target.Id, columns, cards);
    }

    // ------------------------------------------------------------------ columns

    public async Task<bool> EnsureDefaultColumnsAsync(string projectPath, CancellationToken cancellationToken = default)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var count = await ScalarLongAsync(connection, transaction,
            $"SELECT COUNT(*) FROM Boards WHERE ProjectPath = $project{ProjectPathCollation}",
            ("$project", project), cancellationToken);
        if (count > 0)
            return false;

        await InsertBoardWithDefaultLanesAsync(connection, transaction, project, DefaultBoardName, 0, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<BoardColumnRecord>> GetColumnsAsync(string projectPath, CancellationToken cancellationToken = default, string? boardId = null)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        var board = await ResolveBoardIdAsync(connection, null, project, boardId, cancellationToken);
        return board is null ? [] : await ReadColumnsAsync(connection, null, project, board, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, int>> CountCardsByBoardAsync(string projectPath, CancellationToken cancellationToken = default)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT k.BoardId, COUNT(*) FROM BoardCards c JOIN BoardColumns k ON k.Id = c.ColumnId
            WHERE c.ProjectPath = $project{ProjectPathCollation} AND k.BoardId IS NOT NULL
            GROUP BY k.BoardId;
            """;
        command.Parameters.AddWithValue("$project", project);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            counts[reader.GetString(0)] = reader.GetInt32(1);
        return counts;
    }

    public async Task<IReadOnlyList<BoardColumnRecord>> GetAllColumnsAsync(string projectPath, CancellationToken cancellationToken = default)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = ColumnSelectSql + $"""
             WHERE c.ProjectPath = $project{ProjectPathCollation}
             ORDER BY (SELECT b.Position FROM Boards b WHERE b.Id = c.BoardId), c.BoardId, c.Position, c.CreatedUTC;
            """;
        command.Parameters.AddWithValue("$project", project);
        var columns = new List<BoardColumnRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            columns.Add(ReadColumn(reader));
        return columns;
    }

    public async Task<BoardColumnRecord?> GetColumnAsync(string projectPath, string columnId, CancellationToken cancellationToken = default)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        return await ReadColumnAsync(connection, null, project, columnId, cancellationToken);
    }

    public async Task<BoardColumnRecord> CreateColumnAsync(string projectPath, string name, string color, CancellationToken cancellationToken = default, string? boardId = null)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var board = await ResolveBoardIdAsync(connection, transaction, project, boardId, cancellationToken)
            ?? throw new BoardValidationException("No board available. Create a board first.");
        var position = (int)await ScalarLongAsync(connection, transaction,
            "SELECT COUNT(*) FROM BoardColumns WHERE BoardId = $board",
            ("$board", board), cancellationToken);
        var id = NewId("col");
        var now = DateTime.UtcNow;
        await InsertColumnAsync(connection, transaction, id, project, board, name, position, color, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new BoardColumnRecord(id, project, name, position, color, now, now, board);
    }

    public async Task<BoardColumnRecord?> UpdateColumnAsync(string projectPath, string columnId, string? name, string? color, CancellationToken cancellationToken = default)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var existing = await ReadColumnAsync(connection, transaction, project, columnId, cancellationToken);
        if (existing is null)
            return null;

        var updated = existing with
        {
            Name = name ?? existing.Name,
            Color = color ?? existing.Color,
            UpdatedUtc = DateTime.UtcNow
        };

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE BoardColumns SET Name = $name, Color = $color, UpdatedUTC = $updated
                WHERE Id = $id;
                """;
            command.Parameters.AddWithValue("$name", updated.Name);
            command.Parameters.AddWithValue("$color", updated.Color);
            command.Parameters.AddWithValue("$updated", ToDb(updated.UpdatedUtc));
            command.Parameters.AddWithValue("$id", existing.Id);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return updated;
    }

    public async Task<BoardColumnDeleteResult?> DeleteColumnAsync(string projectPath, string columnId, CancellationToken cancellationToken = default)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var target = await ReadColumnAsync(connection, transaction, project, columnId, cancellationToken);
        if (target is null)
            return null;
        var columns = await ReadColumnsAsync(connection, transaction, project, target.BoardId, cancellationToken);
        if (columns.Count <= 1)
            throw new BoardConflictException("The board needs at least one lane.");

        // Cards fall to the left-most remaining lane, appended after its existing cards.
        var destination = columns.Where(c => c.Id != target.Id).OrderBy(c => c.Position).First();
        var destinationCards = await ReadColumnCardIdsAsync(connection, transaction, destination.Id, cancellationToken);
        var movingCards = await ReadColumnCardIdsAsync(connection, transaction, target.Id, cancellationToken);
        var now = ToDb(DateTime.UtcNow);
        var position = destinationCards.Count;
        foreach (var cardId in movingCards)
        {
            await using var move = connection.CreateCommand();
            move.Transaction = transaction;
            move.CommandText = "UPDATE BoardCards SET ColumnId = $column, Position = $position, UpdatedUTC = $updated WHERE Id = $id;";
            move.Parameters.AddWithValue("$column", destination.Id);
            move.Parameters.AddWithValue("$position", position++);
            move.Parameters.AddWithValue("$updated", now);
            move.Parameters.AddWithValue("$id", cardId);
            await move.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM BoardColumns WHERE Id = $id;";
            delete.Parameters.AddWithValue("$id", target.Id);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        var remaining = columns.Where(c => c.Id != target.Id).OrderBy(c => c.Position).Select(c => c.Id).ToList();
        await WriteColumnPositionsAsync(connection, transaction, remaining, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new BoardColumnDeleteResult(target.Id, destination.Id, movingCards.Count);
    }

    public async Task<IReadOnlyList<BoardColumnRecord>> ReorderColumnsAsync(string projectPath, IReadOnlyList<string> orderedIds, CancellationToken cancellationToken = default, string? boardId = null)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var requested = orderedIds.Select(id => id?.Trim() ?? string.Empty).ToList();
        // Without an explicit board, the order describes the board its first lane sits on.
        string? board = null;
        if (boardId is null && requested.Count > 0)
            board = (await ReadColumnAsync(connection, transaction, project, requested[0], cancellationToken))?.BoardId;
        board ??= await ResolveBoardIdAsync(connection, transaction, project, boardId, cancellationToken)
            ?? throw new BoardValidationException("No board available. Create a board first.");
        var columns = await ReadColumnsAsync(connection, transaction, project, board, cancellationToken);
        var known = columns.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        if (requested.Count != known.Count
            || requested.Distinct(StringComparer.Ordinal).Count() != requested.Count
            || requested.Any(id => !known.Contains(id)))
        {
            throw new BoardValidationException("The lane order must list every lane on this board exactly once.");
        }

        await WriteColumnPositionsAsync(connection, transaction, requested, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await ReadColumnsAsync(connection, null, project, board, cancellationToken);
    }

    // ------------------------------------------------------------------ cards

    public async Task<IReadOnlyList<BoardCardRecord>> GetCardsAsync(string projectPath, CancellationToken cancellationToken = default, string? boardId = null)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        var board = await ResolveBoardIdAsync(connection, null, project, boardId, cancellationToken);
        if (board is null)
            return [];
        await using var command = connection.CreateCommand();
        command.CommandText = CardSelectSql + $"""
             WHERE c.ProjectPath = $project{ProjectPathCollation}
               AND c.ColumnId IN (SELECT k.Id FROM BoardColumns k WHERE k.BoardId = $board)
             ORDER BY c.ColumnId, c.Position, c.Number;
            """;
        command.Parameters.AddWithValue("$project", project);
        command.Parameters.AddWithValue("$board", board);
        var cards = new List<BoardCardRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            cards.Add(ReadCard(reader));
        return cards;
    }

    public async Task<BoardCardRecord?> FindCardAsync(string projectPath, string idOrKey, CancellationToken cancellationToken = default)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        return await ReadCardAsync(connection, null, project, idOrKey, cancellationToken);
    }

    public async Task<BoardCardDetailRecord?> GetCardDetailAsync(string projectPath, string idOrKey, CancellationToken cancellationToken = default)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        var card = await ReadCardAsync(connection, null, project, idOrKey, cancellationToken);
        if (card is null)
            return null;

        return new BoardCardDetailRecord(
            card,
            await ReadCommentsAsync(connection, card.Id, BoardCommentKinds.Comment, cancellationToken),
            await ReadSessionsAsync(connection, card.Id, cancellationToken),
            await ReadAttachmentsAsync(connection, card.Id, cancellationToken),
            await ReadCommitsAsync(connection, card.Id, cancellationToken),
            await ReadCommentsAsync(connection, card.Id, BoardCommentKinds.Note, cancellationToken))
        {
            LinkedCards = await ReadLinkedCardsAsync(connection, project, card.Id, cancellationToken)
        };
    }

    public async Task<BoardCardRecord> CreateCardAsync(string projectPath, NewBoardCard card, CancellationToken cancellationToken = default)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);

        BoardColumnRecord column;
        if (string.IsNullOrWhiteSpace(card.ColumnId))
        {
            var board = await ResolveBoardIdAsync(connection, transaction, project, card.BoardId, cancellationToken);
            var columns = board is null ? [] : await ReadColumnsAsync(connection, transaction, project, board, cancellationToken);
            if (columns.Count == 0)
                throw new BoardValidationException("No lane available. Create a lane first.");
            column = columns.OrderBy(c => c.Position).First();
        }
        else
        {
            // A lane id is unique across the project's boards, so the lane alone places the card.
            column = await ReadColumnAsync(connection, transaction, project, card.ColumnId, cancellationToken)
                ?? throw new BoardValidationException($"Lane not found: {card.ColumnId}");
        }

        // Keep the high-water mark independently of card rows, so deleting even
        // the last card cannot make an old key available again. Allocation and
        // insertion share the transaction across dashboard and MCP processes.
        var number = checked((int)await ScalarLongAsync(connection, transaction,
            """
            INSERT INTO BoardCardSequences (ProjectPath, LastNumber) VALUES ($project, 1)
            ON CONFLICT(ProjectPath) DO UPDATE SET LastNumber = LastNumber + 1
            RETURNING LastNumber;
            """,
            ("$project", project), cancellationToken));
        var position = (int)await ScalarLongAsync(connection, transaction,
            "SELECT COUNT(*) FROM BoardCards WHERE ColumnId = $column",
            ("$column", column.Id), cancellationToken);
        var id = NewId("card");
        var now = DateTime.UtcNow;

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO BoardCards
                    (Id, ProjectPath, Number, ColumnId, Position, Title, Description, Assignee, Priority, Type, Points, Tags, Blocked, Flagged, CreatedUTC, UpdatedUTC)
                VALUES
                    ($id, $project, $number, $column, $position, $title, $description, $assignee, $priority, $type, $points, $tags, $blocked, $flagged, $created, $updated);
                """;
            insert.Parameters.AddWithValue("$id", id);
            insert.Parameters.AddWithValue("$project", project);
            insert.Parameters.AddWithValue("$number", number);
            insert.Parameters.AddWithValue("$column", column.Id);
            insert.Parameters.AddWithValue("$position", position);
            insert.Parameters.AddWithValue("$title", card.Title);
            insert.Parameters.AddWithValue("$description", card.Description);
            insert.Parameters.AddWithValue("$assignee", (object?)card.Assignee ?? DBNull.Value);
            insert.Parameters.AddWithValue("$priority", card.Priority);
            insert.Parameters.AddWithValue("$type", card.Type);
            insert.Parameters.AddWithValue("$points", card.Points is int p ? p : DBNull.Value);
            insert.Parameters.AddWithValue("$tags", SerializeTags(card.Tags));
            insert.Parameters.AddWithValue("$blocked", card.Blocked ? 1 : 0);
            insert.Parameters.AddWithValue("$flagged", card.Flagged ? 1 : 0);
            insert.Parameters.AddWithValue("$created", ToDb(now));
            insert.Parameters.AddWithValue("$updated", ToDb(now));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await WriteBaseLlmOptionsAsync(connection, transaction, id, card.BaseLlmOptions, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new BoardCardRecord(id, project, number, column.Id, position, card.Title, card.Description,
            card.Assignee, card.Priority, card.Points, card.Tags, card.Blocked, 0, now, now, card.BaseLlmOptions, Type: card.Type, BoardId: column.BoardId, Flagged: card.Flagged);
    }

    public async Task<BoardCardRecord?> UpdateCardAsync(string projectPath, string cardId, BoardCardPatch patch, CancellationToken cancellationToken = default)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var existing = await ReadCardAsync(connection, transaction, project, cardId, cancellationToken);
        if (existing is null)
            return null;

        var moving = !string.IsNullOrWhiteSpace(patch.ColumnId)
            && !string.Equals(patch.ColumnId.Trim(), existing.ColumnId, StringComparison.Ordinal);
        var columnId = existing.ColumnId;
        var boardId = existing.BoardId;
        var position = existing.Position;
        if (moving)
        {
            var column = await ReadColumnAsync(connection, transaction, project, patch.ColumnId!.Trim(), cancellationToken)
                ?? throw new BoardValidationException($"Lane not found: {patch.ColumnId}");
            columnId = column.Id;
            boardId = column.BoardId;
            position = (int)await ScalarLongAsync(connection, transaction,
                "SELECT COUNT(*) FROM BoardCards WHERE ColumnId = $column AND Id <> $id",
                ("$column", columnId), cancellationToken, ("$id", (object)existing.Id));
        }

        // Append reads the current text while holding the same write transaction as the update.
        // Replacements deliberately accept the last write; neither operation retains old states.
        var description = patch.Description ?? existing.Description;
        if (patch.DescriptionAppend is not null)
        {
            if (patch.Description is not null)
                throw new BoardValidationException("Pass either description or descriptionAppend, not both.");
            description = existing.Description.Length == 0 ? patch.DescriptionAppend
                : existing.Description + "\n\n" + patch.DescriptionAppend;
        }
        if (description.Length > BoardCardLimits.MaxDescriptionLength)
            throw new BoardValidationException($"Description is too long (max {BoardCardLimits.MaxDescriptionLength} characters).");

        var updated = existing with
        {
            ColumnId = columnId,
            BoardId = boardId,
            Position = position,
            Title = patch.Title ?? existing.Title,
            Description = description,
            Assignee = patch.ClearAssignee ? null : (patch.Assignee ?? existing.Assignee),
            Priority = patch.Priority ?? existing.Priority,
            Type = patch.Type ?? existing.Type,
            Points = patch.ClearPoints ? null : (patch.Points ?? existing.Points),
            Tags = patch.Tags ?? existing.Tags,
            Blocked = patch.Blocked ?? existing.Blocked,
            Flagged = patch.Flagged ?? existing.Flagged,
            UpdatedUtc = DateTime.UtcNow,
            BaseLlmOptions = patch.ClearBaseLlmOptions ? null : patch.BaseLlmOptions ?? existing.BaseLlmOptions
        };

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE BoardCards SET ColumnId = $column, Position = $position, Title = $title, Description = $description,
                    Assignee = $assignee, Priority = $priority, Type = $type, Points = $points, Tags = $tags, Blocked = $blocked, Flagged = $flagged, UpdatedUTC = $updated
                WHERE Id = $id;
                """;
            command.Parameters.AddWithValue("$column", updated.ColumnId);
            command.Parameters.AddWithValue("$position", updated.Position);
            command.Parameters.AddWithValue("$title", updated.Title);
            command.Parameters.AddWithValue("$description", updated.Description);
            command.Parameters.AddWithValue("$assignee", (object?)updated.Assignee ?? DBNull.Value);
            command.Parameters.AddWithValue("$priority", updated.Priority);
            command.Parameters.AddWithValue("$type", updated.Type);
            command.Parameters.AddWithValue("$points", updated.Points is int p ? p : DBNull.Value);
            command.Parameters.AddWithValue("$tags", SerializeTags(updated.Tags));
            command.Parameters.AddWithValue("$blocked", updated.Blocked ? 1 : 0);
            command.Parameters.AddWithValue("$flagged", updated.Flagged ? 1 : 0);
            command.Parameters.AddWithValue("$updated", ToDb(updated.UpdatedUtc));
            command.Parameters.AddWithValue("$id", updated.Id);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        if (patch.ClearBaseLlmOptions || patch.BaseLlmOptions is not null)
            await WriteBaseLlmOptionsAsync(connection, transaction, updated.Id, updated.BaseLlmOptions, cancellationToken);
        if (moving)
        {
            await RenumberColumnAsync(connection, transaction, existing.ColumnId, cancellationToken);
            await RenumberColumnAsync(connection, transaction, columnId, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return updated;
    }

    public async Task<bool> DeleteCardAsync(string projectPath, string cardId, CancellationToken cancellationToken = default)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var existing = await ReadCardAsync(connection, transaction, project, cardId, cancellationToken);
        if (existing is null)
            return false;

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM BoardCards WHERE Id = $id;";
            delete.Parameters.AddWithValue("$id", existing.Id);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }
        await RenumberColumnAsync(connection, transaction, existing.ColumnId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<BoardCardRecord?> MoveCardAsync(string projectPath, string cardId, string columnId, int? position, CancellationToken cancellationToken = default)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var existing = await ReadCardAsync(connection, transaction, project, cardId, cancellationToken);
        if (existing is null)
            return null;
        var column = await ReadColumnAsync(connection, transaction, project, columnId, cancellationToken)
            ?? throw new BoardValidationException($"Lane not found: {columnId}");

        var sourceIds = (await ReadColumnCardIdsAsync(connection, transaction, existing.ColumnId, cancellationToken))
            .Where(id => id != existing.Id).ToList();
        var sameColumn = string.Equals(column.Id, existing.ColumnId, StringComparison.Ordinal);
        var targetIds = sameColumn
            ? sourceIds
            : (await ReadColumnCardIdsAsync(connection, transaction, column.Id, cancellationToken)).Where(id => id != existing.Id).ToList();
        var index = Math.Clamp(position ?? targetIds.Count, 0, targetIds.Count);
        targetIds.Insert(index, existing.Id);

        var now = ToDb(DateTime.UtcNow);
        await using (var move = connection.CreateCommand())
        {
            move.Transaction = transaction;
            move.CommandText = "UPDATE BoardCards SET ColumnId = $column, UpdatedUTC = $updated WHERE Id = $id;";
            move.Parameters.AddWithValue("$column", column.Id);
            move.Parameters.AddWithValue("$updated", now);
            move.Parameters.AddWithValue("$id", existing.Id);
            await move.ExecuteNonQueryAsync(cancellationToken);
        }
        await WriteCardPositionsAsync(connection, transaction, targetIds, cancellationToken);
        if (!sameColumn)
            await WriteCardPositionsAsync(connection, transaction, sourceIds, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await ReadCardAsync(connection, null, project, existing.Id, cancellationToken);
    }

    // ------------------------------------------------------------------ comments

    /// <summary>Reads the agent identity without constructing the dashboard's Repository in stdio hosts.</summary>
    public async Task<BoardAuthor?> FindSessionAuthorAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenStateAsync(cancellationToken);
        // Terminal history stays in state.db; a fresh stdio host may not yet have Sessions.
        var hasSessions = await ScalarLongAsync(connection, null,
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $table",
            ("$table", "Sessions"), cancellationToken) > 0;
        if (hasSessions)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT Cli, EnvironmentName FROM Sessions WHERE Id = $session LIMIT 1;";
            command.Parameters.AddWithValue("$session", sessionId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var cli = reader.IsDBNull(0) ? null : reader.GetString(0);
                var environment = reader.IsDBNull(1) ? null : reader.GetString(1);
                var cliName = LlmParser.ToWireName(LlmParser.ParseValue(cli));
                if (string.IsNullOrWhiteSpace(cliName)) cliName = cli?.Trim();
                var label = string.IsNullOrWhiteSpace(environment) ? cliName : environment.Trim();
                if (!string.IsNullOrWhiteSpace(label))
                    return BoardAuthor.Agent(label, cliName, sessionId);
            }
        }

        // Retain attribution even if old terminal history has been pruned.
        await using var boardConnection = await OpenAsync(cancellationToken);
        await using var linked = boardConnection.CreateCommand();
        linked.CommandText = "SELECT DisplayName, Cli FROM BoardCardSessions WHERE SessionId = $session LIMIT 1;";
        linked.Parameters.AddWithValue("$session", sessionId);
        await using var linkedReader = await linked.ExecuteReaderAsync(cancellationToken);
        if (await linkedReader.ReadAsync(cancellationToken))
        {
            var label = linkedReader.GetString(0);
            var cli = linkedReader.GetString(1);
            if (BoardAuthor.IsGenericAgentLabel(label))
                label = LlmParser.ToWireName(LlmParser.ParseValue(cli));
            if (!string.IsNullOrWhiteSpace(label))
                return BoardAuthor.Agent(label, cli, sessionId);
        }
        return null;
    }

    public Task<BoardCommentRecord?> AddCommentAsync(string projectPath, string cardId, BoardAuthor author, string body, CancellationToken cancellationToken = default)
        => InsertCommentRowAsync(projectPath, cardId, author, body, BoardCommentKinds.Comment, cancellationToken);

    public Task<BoardCommentRecord?> AddNoteAsync(string projectPath, string cardId, BoardAuthor author, string body, CancellationToken cancellationToken = default)
        => InsertCommentRowAsync(projectPath, cardId, author, body, BoardCommentKinds.Note, cancellationToken);

    private async Task<BoardCommentRecord?> InsertCommentRowAsync(string projectPath, string cardId, BoardAuthor author, string body, string kind, CancellationToken cancellationToken)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var card = await ReadCardAsync(connection, transaction, project, cardId, cancellationToken);
        if (card is null)
            return null;

        // Notes use their own id prefix so an agent can tell the two apart in tool output.
        var comment = new BoardCommentRecord(NewId(kind == BoardCommentKinds.Note ? "note" : "cm"), card.Id, author, body, DateTime.UtcNow, kind);
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO BoardComments (Id, CardId, AuthorKind, AuthorLabel, AuthorCli, SessionId, Body, CreatedUTC, Kind)
                VALUES ($id, $card, $kind, $label, $cli, $session, $body, $created, $rowKind);
                """;
            insert.Parameters.AddWithValue("$id", comment.Id);
            insert.Parameters.AddWithValue("$card", card.Id);
            insert.Parameters.AddWithValue("$kind", author.Kind);
            insert.Parameters.AddWithValue("$label", author.Label);
            insert.Parameters.AddWithValue("$cli", (object?)author.Cli ?? DBNull.Value);
            insert.Parameters.AddWithValue("$session", (object?)author.SessionId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$body", body);
            insert.Parameters.AddWithValue("$created", ToDb(comment.CreatedUtc));
            insert.Parameters.AddWithValue("$rowKind", kind);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await TouchCardAsync(connection, transaction, card.Id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return comment;
    }

    public async Task<IReadOnlyList<BoardCommentRecord>> GetNotesAsync(string projectPath, string idOrKey, CancellationToken cancellationToken = default)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        var card = await ReadCardAsync(connection, null, project, idOrKey, cancellationToken);
        return card is null ? [] : await ReadCommentsAsync(connection, card.Id, BoardCommentKinds.Note, cancellationToken);
    }

    /// <summary>
    /// Same "the table may not exist in this host" discipline as <see cref="FindSessionAuthorAsync"/>:
    /// a board-only database (fresh stdio host) has neither Sessions nor ChatSummary.
    /// </summary>
    public async Task<BoardSessionOutcomeRecord?> FindSessionOutcomeAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenStateAsync(cancellationToken);
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var probe = connection.CreateCommand())
        {
            probe.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name IN ('Sessions', 'ChatSummary');";
            await using var reader = await probe.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                tables.Add(reader.GetString(0));
        }
        // Fixtures (and very old files) carry a Sessions table without these columns.
        if (!tables.Contains("Sessions")
            || !SqliteSchema.HasColumn(connection, null, "Sessions", "EndedUTC")
            || !SqliteSchema.HasColumn(connection, null, "Sessions", "ExitCode"))
            return null;

        DateTime? ended = null;
        int? exitCode = null;
        await using (var session = connection.CreateCommand())
        {
            session.CommandText = "SELECT EndedUTC, ExitCode FROM Sessions WHERE Id = $session LIMIT 1;";
            session.Parameters.AddWithValue("$session", sessionId);
            await using var reader = await session.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return null;
            if (!reader.IsDBNull(0) && DateTime.TryParse(reader.GetString(0), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var endedAt))
                ended = endedAt.ToUniversalTime();
            if (!reader.IsDBNull(1))
                exitCode = reader.GetInt32(1);
        }

        string? summary = null;
        if (tables.Contains("ChatSummary"))
        {
            await using var chat = connection.CreateCommand();
            chat.CommandText = "SELECT SummaryText FROM ChatSummary WHERE SessionId = $session LIMIT 1;";
            chat.Parameters.AddWithValue("$session", sessionId);
            summary = await chat.ExecuteScalarAsync(cancellationToken) as string;
            if (string.IsNullOrWhiteSpace(summary)) summary = null;
        }
        return new BoardSessionOutcomeRecord(sessionId, ended, exitCode, summary);
    }

    // ------------------------------------------------------------------ sessions

    public async Task<BoardSessionRecord?> LinkSessionAsync(string projectPath, string cardId, string sessionId, string? tabId, string selection, string cli, string displayName, string origin, CancellationToken cancellationToken = default)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var card = await ReadCardAsync(connection, transaction, project, cardId, cancellationToken);
        if (card is null)
            return null;

        var existingCard = await ScalarStringAsync(connection, transaction,
            "SELECT CardId FROM BoardCardSessions WHERE SessionId = $session", ("$session", sessionId), cancellationToken);
        if (existingCard is not null)
        {
            throw new BoardConflictException(existingCard == card.Id
                ? "That session is already on this card."
                : "That session is already linked to another card.");
        }

        var record = new BoardSessionRecord(sessionId, card.Id, tabId, selection, cli, displayName, origin, DateTime.UtcNow);
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO BoardCardSessions (SessionId, CardId, TabId, Selection, Cli, DisplayName, Origin, CreatedUTC)
                VALUES ($session, $card, $tab, $selection, $cli, $name, $origin, $created);
                """;
            insert.Parameters.AddWithValue("$session", record.SessionId);
            insert.Parameters.AddWithValue("$card", record.CardId);
            insert.Parameters.AddWithValue("$tab", (object?)record.TabId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$selection", record.Selection);
            insert.Parameters.AddWithValue("$cli", record.Cli);
            insert.Parameters.AddWithValue("$name", record.DisplayName);
            insert.Parameters.AddWithValue("$origin", record.Origin);
            insert.Parameters.AddWithValue("$created", ToDb(record.CreatedUtc));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await TouchCardAsync(connection, transaction, card.Id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return record;
    }

    public async Task<BoardSessionRecord?> RenameSessionAsync(string projectPath, string cardId, string sessionId, string displayName, CancellationToken cancellationToken = default)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var card = await ReadCardAsync(connection, transaction, project, cardId, cancellationToken);
        if (card is null)
            return null;
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE BoardCardSessions SET DisplayName = $name WHERE SessionId = $session AND CardId = $card;";
            update.Parameters.AddWithValue("$name", displayName);
            update.Parameters.AddWithValue("$session", sessionId);
            update.Parameters.AddWithValue("$card", card.Id);
            if (await update.ExecuteNonQueryAsync(cancellationToken) == 0)
                return null;
        }
        await transaction.CommitAsync(cancellationToken);
        return (await ReadSessionsAsync(connection, card.Id, cancellationToken)).FirstOrDefault(s => s.SessionId == sessionId);
    }

    public async Task<bool> UnlinkSessionAsync(string projectPath, string cardId, string sessionId, CancellationToken cancellationToken = default)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var card = await ReadCardAsync(connection, transaction, project, cardId, cancellationToken);
        if (card is null)
            return false;
        bool removed;
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM BoardCardSessions WHERE SessionId = $session AND CardId = $card;";
            delete.Parameters.AddWithValue("$session", sessionId);
            delete.Parameters.AddWithValue("$card", card.Id);
            removed = await delete.ExecuteNonQueryAsync(cancellationToken) > 0;
        }
        if (removed)
            await TouchCardAsync(connection, transaction, card.Id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return removed;
    }

    public async Task<BoardSessionLink?> FindSessionLinkAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.SessionId, s.CardId, c.ProjectPath
            FROM BoardCardSessions s JOIN BoardCards c ON c.Id = s.CardId
            WHERE s.SessionId = $session LIMIT 1;
            """;
        command.Parameters.AddWithValue("$session", sessionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;
        return new BoardSessionLink(reader.GetString(0), reader.GetString(1), reader.GetString(2));
    }

    public async Task<IReadOnlyList<BoardSessionRecord>> GetSessionsForProjectAsync(string projectPath, CancellationToken cancellationToken = default)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = SessionSelectSql + $" JOIN BoardCards c ON c.Id = s.CardId WHERE c.ProjectPath = $project{ProjectPathCollation} ORDER BY s.CreatedUTC;";
        command.Parameters.AddWithValue("$project", project);
        var sessions = new List<BoardSessionRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            sessions.Add(ReadSession(reader));
        return sessions;
    }

    // ------------------------------------------------------------------ attachments

    public async Task<bool> DeleteAttachmentAsync(string projectPath, string cardId, string attachmentId, CancellationToken cancellationToken = default)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var card = await ReadCardAsync(connection, transaction, project, cardId, cancellationToken);
        if (card is null)
            return false;
        bool removed;
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM BoardAttachments WHERE Id = $id AND CardId = $card;";
            delete.Parameters.AddWithValue("$id", attachmentId);
            delete.Parameters.AddWithValue("$card", card.Id);
            removed = await delete.ExecuteNonQueryAsync(cancellationToken) > 0;
        }
        if (removed)
        {
            await TouchCardAsync(connection, transaction, card.Id, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return removed;
    }

    // ------------------------------------------------------------------ commits

    public async Task<IReadOnlyList<BoardCommitRecord>> GetCommitsAsync(string projectPath, string cardId, CancellationToken cancellationToken = default)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        var card = await ReadCardAsync(connection, null, project, cardId, cancellationToken);
        return card is null ? [] : await ReadCommitsAsync(connection, card.Id, cancellationToken);
    }

    public async Task<BoardCommitRecord?> AddCommitAsync(string projectPath, string cardId, string sha, string author, string message, DateTime committedUtc, SandboxDiffResponse snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var snapshotJson = JsonSerializer.Serialize(snapshot, StorageJsonSerializerContext.Default.SandboxDiffResponse);
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var card = await ReadCardAsync(connection, transaction, project, cardId, cancellationToken);
        if (card is null)
            return null;

        var duplicate = await ScalarLongAsync(connection, transaction,
            "SELECT COUNT(*) FROM BoardCommits WHERE CardId = $card AND Sha = $sha",
            ("$card", card.Id), cancellationToken, ("$sha", (object)sha));
        if (duplicate > 0)
            throw new BoardConflictException($"{sha[..Math.Min(7, sha.Length)]} is already on this card.");

        var record = new BoardCommitRecord(card.Id, sha, author, message, committedUtc, DateTime.UtcNow);
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO BoardCommits (CardId, Sha, Author, Message, CommittedUTC, LinkedUTC)
                VALUES ($card, $sha, $author, $message, $committed, $linked);
                """;
            insert.Parameters.AddWithValue("$card", record.CardId);
            insert.Parameters.AddWithValue("$sha", record.Sha);
            insert.Parameters.AddWithValue("$author", record.Author);
            insert.Parameters.AddWithValue("$message", record.Message);
            insert.Parameters.AddWithValue("$committed", ToDb(record.CommittedUtc));
            insert.Parameters.AddWithValue("$linked", ToDb(record.LinkedUtc));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var capture = connection.CreateCommand())
        {
            capture.Transaction = transaction;
            capture.CommandText = "INSERT INTO BoardCommitSnapshots (CardId, Sha, SnapshotJson) VALUES ($card, $sha, $snapshot);";
            capture.Parameters.AddWithValue("$card", record.CardId);
            capture.Parameters.AddWithValue("$sha", record.Sha);
            capture.Parameters.AddWithValue("$snapshot", snapshotJson);
            await capture.ExecuteNonQueryAsync(cancellationToken);
        }
        await TouchCardAsync(connection, transaction, card.Id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return record;
    }

    public async Task<SandboxDiffResponse?> GetCommitSnapshotAsync(string projectPath, string cardId, string sha, CancellationToken cancellationToken = default)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        var card = await ReadCardAsync(connection, null, project, cardId, cancellationToken);
        if (card is null)
            return null;
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT SnapshotJson FROM BoardCommitSnapshots WHERE CardId = $card AND Sha = $sha;";
        command.Parameters.AddWithValue("$card", card.Id);
        command.Parameters.AddWithValue("$sha", sha);
        var json = await command.ExecuteScalarAsync(cancellationToken) as string;
        return json is null ? null : JsonSerializer.Deserialize(json, StorageJsonSerializerContext.Default.SandboxDiffResponse);
    }

    public async Task<bool> RemoveCommitAsync(string projectPath, string cardId, string sha, CancellationToken cancellationToken = default)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var card = await ReadCardAsync(connection, transaction, project, cardId, cancellationToken);
        if (card is null)
            return false;
        bool removed;
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            // Callers resolve a short sha to exactly one full sha before deleting.
            delete.CommandText = "DELETE FROM BoardCommits WHERE CardId = $card AND Sha = $sha;";
            delete.Parameters.AddWithValue("$card", card.Id);
            delete.Parameters.AddWithValue("$sha", sha);
            removed = await delete.ExecuteNonQueryAsync(cancellationToken) > 0;
        }
        if (removed)
            await TouchCardAsync(connection, transaction, card.Id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return removed;
    }

    // ------------------------------------------------------------------ readers

    private const string ColumnSelectSql =
        "SELECT c.Id, c.ProjectPath, c.Name, c.Position, c.Color, c.CreatedUTC, c.UpdatedUTC, c.BoardId FROM BoardColumns c";

    private const string BoardSelectSql =
        "SELECT Id, ProjectPath, Name, Position, CreatedUTC, UpdatedUTC FROM Boards";

    private const string CardSelectSql = """
        SELECT c.Id, c.ProjectPath, c.Number, c.ColumnId, c.Position, c.Title, c.Description, c.Assignee, c.Priority,
               c.Points, c.Tags, c.Blocked, c.CreatedUTC, c.UpdatedUTC,
               (SELECT COUNT(*) FROM BoardComments m WHERE m.CardId = c.Id AND m.Kind = 'comment') AS CommentCount,
               (SELECT o.OptionsJson FROM BoardCardOptions o WHERE o.CardId = c.Id),
               c.Type,
               (SELECT k.BoardId FROM BoardColumns k WHERE k.Id = c.ColumnId), c.Flagged
        FROM BoardCards c
        """;

    private const string SessionSelectSql =
        "SELECT s.SessionId, s.CardId, s.TabId, s.Selection, s.Cli, s.DisplayName, s.Origin, s.CreatedUTC FROM BoardCardSessions s";

    private static async Task<IReadOnlyList<BoardColumnRecord>> ReadColumnsAsync(SqliteConnection connection, SqliteTransaction? transaction, string project, string boardId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = ColumnSelectSql + $" WHERE c.ProjectPath = $project{ProjectPathCollation} AND c.BoardId = $board ORDER BY c.Position, c.CreatedUTC;";
        command.Parameters.AddWithValue("$project", project);
        command.Parameters.AddWithValue("$board", boardId);
        var columns = new List<BoardColumnRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            columns.Add(ReadColumn(reader));
        return columns;
    }

    private static async Task<BoardColumnRecord?> ReadColumnAsync(SqliteConnection connection, SqliteTransaction? transaction, string project, string columnId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = ColumnSelectSql + $" WHERE c.ProjectPath = $project{ProjectPathCollation} AND c.Id = $id LIMIT 1;";
        command.Parameters.AddWithValue("$project", project);
        command.Parameters.AddWithValue("$id", columnId.Trim());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadColumn(reader) : null;
    }

    private static BoardColumnRecord ReadColumn(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetInt32(3),
        reader.GetString(4),
        ParseDb(reader.GetString(5)),
        ParseDb(reader.GetString(6)),
        reader.IsDBNull(7) ? string.Empty : reader.GetString(7));

    private static async Task<IReadOnlyList<BoardRecord>> ReadBoardsAsync(SqliteConnection connection, SqliteTransaction? transaction, string project, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = BoardSelectSql + $" WHERE ProjectPath = $project{ProjectPathCollation} ORDER BY Position, CreatedUTC;";
        command.Parameters.AddWithValue("$project", project);
        var boards = new List<BoardRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            boards.Add(ReadBoard(reader));
        return boards;
    }

    private static async Task<BoardRecord?> ReadBoardAsync(SqliteConnection connection, SqliteTransaction? transaction, string project, string boardId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = BoardSelectSql + $" WHERE ProjectPath = $project{ProjectPathCollation} AND Id = $id LIMIT 1;";
        command.Parameters.AddWithValue("$project", project);
        command.Parameters.AddWithValue("$id", boardId.Trim());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadBoard(reader) : null;
    }

    private static BoardRecord ReadBoard(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetInt32(3),
        ParseDb(reader.GetString(4)),
        ParseDb(reader.GetString(5)));

    /// <summary>
    /// The board a caller means: the named one (which must belong to the project), else the
    /// project's default — first by position. Null only when the project has no board yet.
    /// </summary>
    private static async Task<string?> ResolveBoardIdAsync(SqliteConnection connection, SqliteTransaction? transaction, string project, string? boardId, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(boardId))
        {
            var board = await ReadBoardAsync(connection, transaction, project, boardId, cancellationToken)
                ?? throw new BoardValidationException($"Board not found: {boardId.Trim()}");
            return board.Id;
        }
        return await ScalarStringAsync(connection, transaction,
            $"SELECT Id FROM Boards WHERE ProjectPath = $project{ProjectPathCollation} ORDER BY Position, CreatedUTC LIMIT 1",
            ("$project", project), cancellationToken);
    }

    private static async Task<BoardCardRecord?> ReadCardAsync(SqliteConnection connection, SqliteTransaction? transaction, string project, string idOrKey, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        if (BoardKeys.TryParse(idOrKey, out var number))
        {
            command.CommandText = CardSelectSql + $" WHERE c.ProjectPath = $project{ProjectPathCollation} AND c.Number = $number LIMIT 1;";
            command.Parameters.AddWithValue("$number", number);
        }
        else
        {
            command.CommandText = CardSelectSql + $" WHERE c.ProjectPath = $project{ProjectPathCollation} AND c.Id = $id LIMIT 1;";
            command.Parameters.AddWithValue("$id", idOrKey.Trim());
        }
        command.Parameters.AddWithValue("$project", project);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadCard(reader) : null;
    }

    private static BoardCardRecord ReadCard(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetInt32(2),
        reader.GetString(3),
        reader.GetInt32(4),
        reader.GetString(5),
        reader.GetString(6),
        reader.IsDBNull(7) ? null : reader.GetString(7),
        reader.GetString(8),
        reader.IsDBNull(9) ? null : reader.GetInt32(9),
        DeserializeTags(reader.GetString(10)),
        reader.GetInt32(11) != 0,
        reader.GetInt32(14),
        ParseDb(reader.GetString(12)),
        ParseDb(reader.GetString(13)),
        reader.IsDBNull(15) ? null : JsonSerializer.Deserialize(reader.GetString(15), StorageJsonSerializerContext.Default.BaseLlmOptions),
        Type: reader.GetString(16),
        BoardId: reader.IsDBNull(17) ? string.Empty : reader.GetString(17),
        Flagged: reader.GetInt32(18) != 0);

    private static async Task<IReadOnlyList<BoardCommentRecord>> ReadCommentsAsync(SqliteConnection connection, string cardId, string kind, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, CardId, AuthorKind, AuthorLabel, AuthorCli, SessionId, Body, CreatedUTC, Kind
            FROM BoardComments WHERE CardId = $card AND Kind = $kind ORDER BY CreatedUTC, Id;
            """;
        command.Parameters.AddWithValue("$card", cardId);
        command.Parameters.AddWithValue("$kind", kind);
        var comments = new List<BoardCommentRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            comments.Add(new BoardCommentRecord(
                reader.GetString(0),
                reader.GetString(1),
                new BoardAuthor(
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5)),
                reader.GetString(6),
                ParseDb(reader.GetString(7)),
                reader.GetString(8)));
        }
        return comments;
    }

    private static async Task<IReadOnlyList<BoardSessionRecord>> ReadSessionsAsync(SqliteConnection connection, string cardId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = SessionSelectSql + " WHERE s.CardId = $card ORDER BY s.CreatedUTC;";
        command.Parameters.AddWithValue("$card", cardId);
        var sessions = new List<BoardSessionRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            sessions.Add(ReadSession(reader));
        return sessions;
    }

    private static BoardSessionRecord ReadSession(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.IsDBNull(2) ? null : reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetString(5),
        reader.GetString(6),
        ParseDb(reader.GetString(7)));

    private static async Task<IReadOnlyList<BoardAttachmentRecord>> ReadAttachmentsAsync(SqliteConnection connection, string cardId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, CardId, Name, MimeType, Bytes, DataUrl, CreatedUTC FROM BoardAttachments WHERE CardId = $card ORDER BY CreatedUTC, Id;";
        command.Parameters.AddWithValue("$card", cardId);
        var attachments = new List<BoardAttachmentRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            attachments.Add(new BoardAttachmentRecord(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetInt64(4), reader.GetString(5), ParseDb(reader.GetString(6))));
        }
        return attachments;
    }

    private static async Task<IReadOnlyList<BoardCommitRecord>> ReadCommitsAsync(SqliteConnection connection, string cardId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CardId, Sha, Author, Message, CommittedUTC, LinkedUTC FROM BoardCommits WHERE CardId = $card ORDER BY CommittedUTC DESC, Sha;";
        command.Parameters.AddWithValue("$card", cardId);
        var commits = new List<BoardCommitRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            commits.Add(new BoardCommitRecord(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                ParseDb(reader.GetString(4)), ParseDb(reader.GetString(5))));
        }
        return commits;
    }

    // ------------------------------------------------------------------ writers / helpers

    private static async Task<BoardRecord> InsertBoardWithDefaultLanesAsync(SqliteConnection connection, SqliteTransaction transaction, string project, string name, int position, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var board = new BoardRecord(NewId("brd"), project, name, position, now, now);
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO Boards (Id, ProjectPath, Name, Position, CreatedUTC, UpdatedUTC)
                VALUES ($id, $project, $name, $position, $created, $updated);
                """;
            insert.Parameters.AddWithValue("$id", board.Id);
            insert.Parameters.AddWithValue("$project", project);
            insert.Parameters.AddWithValue("$name", name);
            insert.Parameters.AddWithValue("$position", position);
            insert.Parameters.AddWithValue("$created", ToDb(now));
            insert.Parameters.AddWithValue("$updated", ToDb(now));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        var lanePosition = 0;
        foreach (var (laneName, color) in DefaultLanes)
            await InsertColumnAsync(connection, transaction, NewId("col"), project, board.Id, laneName, lanePosition++, color, now, cancellationToken);
        return board;
    }

    private static async Task WriteBoardPositionsAsync(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<string> orderedIds, CancellationToken cancellationToken)
    {
        var now = ToDb(DateTime.UtcNow);
        for (var index = 0; index < orderedIds.Count; index++)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE Boards SET Position = $position, UpdatedUTC = $updated WHERE Id = $id AND Position <> $position;";
            command.Parameters.AddWithValue("$position", index);
            command.Parameters.AddWithValue("$updated", now);
            command.Parameters.AddWithValue("$id", orderedIds[index]);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task InsertColumnAsync(SqliteConnection connection, SqliteTransaction transaction, string id, string project, string boardId, string name, int position, string color, DateTime now, CancellationToken cancellationToken)
    {
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO BoardColumns (Id, ProjectPath, BoardId, Name, Position, Color, CreatedUTC, UpdatedUTC)
            VALUES ($id, $project, $board, $name, $position, $color, $created, $updated);
            """;
        insert.Parameters.AddWithValue("$id", id);
        insert.Parameters.AddWithValue("$project", project);
        insert.Parameters.AddWithValue("$board", boardId);
        insert.Parameters.AddWithValue("$name", name);
        insert.Parameters.AddWithValue("$position", position);
        insert.Parameters.AddWithValue("$color", color);
        insert.Parameters.AddWithValue("$created", ToDb(now));
        insert.Parameters.AddWithValue("$updated", ToDb(now));
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<List<string>> ReadColumnCardIdsAsync(SqliteConnection connection, SqliteTransaction transaction, string columnId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT Id FROM BoardCards WHERE ColumnId = $column ORDER BY Position, Number;";
        command.Parameters.AddWithValue("$column", columnId);
        var ids = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            ids.Add(reader.GetString(0));
        return ids;
    }

    private static async Task RenumberColumnAsync(SqliteConnection connection, SqliteTransaction transaction, string columnId, CancellationToken cancellationToken)
    {
        var ids = await ReadColumnCardIdsAsync(connection, transaction, columnId, cancellationToken);
        await WriteCardPositionsAsync(connection, transaction, ids, cancellationToken);
    }

    private static async Task WriteCardPositionsAsync(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<string> orderedIds, CancellationToken cancellationToken)
    {
        for (var index = 0; index < orderedIds.Count; index++)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE BoardCards SET Position = $position WHERE Id = $id AND Position <> $position;";
            command.Parameters.AddWithValue("$position", index);
            command.Parameters.AddWithValue("$id", orderedIds[index]);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task WriteColumnPositionsAsync(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<string> orderedIds, CancellationToken cancellationToken)
    {
        var now = ToDb(DateTime.UtcNow);
        for (var index = 0; index < orderedIds.Count; index++)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE BoardColumns SET Position = $position, UpdatedUTC = $updated WHERE Id = $id AND Position <> $position;";
            command.Parameters.AddWithValue("$position", index);
            command.Parameters.AddWithValue("$updated", now);
            command.Parameters.AddWithValue("$id", orderedIds[index]);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task TouchCardAsync(SqliteConnection connection, SqliteTransaction transaction, string cardId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE BoardCards SET UpdatedUTC = $updated WHERE Id = $id;";
        command.Parameters.AddWithValue("$updated", ToDb(DateTime.UtcNow));
        command.Parameters.AddWithValue("$id", cardId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<long> ScalarLongAsync(SqliteConnection connection, SqliteTransaction? transaction, string sql, (string Name, object Value) parameter, CancellationToken cancellationToken, params (string Name, object Value)[] more)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        foreach (var (name, value) in more)
            command.Parameters.AddWithValue(name, value);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null || result is DBNull ? 0 : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private static async Task<string?> ScalarStringAsync(SqliteConnection connection, SqliteTransaction? transaction, string sql, (string Name, object Value) parameter, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is string text ? text : null;
    }

    private Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
        => SqliteConnectionFactory.OpenAsync(_connectionString, cancellationToken);

    // Only external references (terminal history and Automation definitions) use state.db.
    // Every Board-owned read/write, including session links and pending events, uses OpenAsync.
    private Task<SqliteConnection> OpenStateAsync(CancellationToken cancellationToken)
        => SqliteConnectionFactory.OpenAsync(_stateConnectionString, cancellationToken);

    private void EnsureSchema()
    {
        using var connection = SqliteConnectionFactory.Open(_connectionString);
        SqliteConnectionFactory.EnsureWalMode(connection);
        SqliteMigrationRunner.RequireGenerationAtMost(connection, Generation, "board.db");
        SqliteMigrationRunner.Apply(connection, "board", 1, MigrationKind.Additive, (db, transaction) =>
        {
            SqliteSchema.Execute(db, transaction, SchemaSql);
            EnsureAttachmentSchema(db, transaction);
            EnsureDescriptionSchema(db, transaction);
        });
        // board/2: BoardComments.Kind separates agent scratchpad notes from the comment stream.
        // A fresh file already has the column from SchemaSql; adoption is guarded either way.
        SqliteMigrationRunner.Apply(connection, "board", 2, MigrationKind.Additive, (db, transaction) =>
            SqliteSchema.AdoptStatement(db, transaction, CommentKindColumnSql));
        // board/3: classify cards. Existing rows are deliberately neutral rather than guessed.
        SqliteMigrationRunner.Apply(connection, "board", 3, MigrationKind.Additive, (db, transaction) =>
            SqliteSchema.AdoptStatement(db, transaction, CardTypeColumnSql));
        // board/4: several boards per project. Lanes gain a nullable BoardId; the rows are adopted
        // into a default board by ReconcileDerivedRows, which also catches lanes an older binary
        // inserts later without one.
        SqliteMigrationRunner.Apply(connection, "board", 4, MigrationKind.Additive, (db, transaction) =>
        {
            SqliteSchema.Execute(db, transaction, BoardsTableSql);
            SqliteSchema.AdoptStatement(db, transaction, ColumnBoardIdSql);
            SqliteSchema.Execute(db, transaction, ColumnBoardIndexSql);
        });
        SqliteMigrationRunner.Apply(connection, "board", 5, MigrationKind.Additive, (db, transaction) =>
            SqliteSchema.Execute(db, transaction, CardLinksSchemaSql));
        SqliteMigrationRunner.Apply(connection, "board", 6, MigrationKind.Additive, (db, transaction) =>
        {
            SqliteSchema.Execute(db, transaction, ContextSettingsSchemaSql);
            SqliteSchema.Execute(db, transaction, LaneAutomationSchemaSql);
        });
        SqliteMigrationRunner.Apply(connection, "board", 7, MigrationKind.Additive, (db, transaction) =>
            SqliteSchema.Execute(db, transaction, AdditionalLaneAutomationSchemaSql));
        SqliteMigrationRunner.Apply(connection, "board", 8, MigrationKind.Breaking, (db, transaction) =>
        {
            SqliteSchema.Execute(db, transaction, """
                DROP TABLE IF EXISTS BoardDescriptionRevisionAttachments;
                DROP TABLE IF EXISTS BoardDescriptionSessionEvents;
                DROP TABLE IF EXISTS BoardDescriptionRevisions;
                PRAGMA user_version=3;
                """);
            if (SqliteSchema.HasColumn(db, transaction, "BoardAttachments", "DeletedUTC"))
                SqliteSchema.Execute(db, transaction, "DELETE FROM BoardAttachments WHERE DeletedUTC IS NOT NULL; ALTER TABLE BoardAttachments DROP COLUMN DeletedUTC;");
            if (SqliteSchema.HasColumn(db, transaction, "BoardColumns", "WipLimit"))
                SqliteSchema.Execute(db, transaction, "ALTER TABLE BoardColumns DROP COLUMN WipLimit;");
        });
        SqliteMigrationRunner.Apply(connection, "board", 9, MigrationKind.Additive, (db, transaction) =>
            SqliteSchema.AdoptStatement(db, transaction, "ALTER TABLE BoardCards ADD COLUMN Flagged INTEGER NOT NULL DEFAULT 0"));
        ReconcileDerivedRows(connection);
    }

    internal const string ColumnBoardIdSql =
        "ALTER TABLE BoardColumns ADD COLUMN BoardId TEXT NULL";

    internal const string ColumnBoardIndexSql =
        "CREATE INDEX IF NOT EXISTS IX_BoardColumns_Board ON BoardColumns(BoardId, Position)";

    internal const string CommentKindColumnSql =
        "ALTER TABLE BoardComments ADD COLUMN Kind TEXT NOT NULL DEFAULT 'comment'";

    internal const string CardTypeColumnSql =
        "ALTER TABLE BoardCards ADD COLUMN Type TEXT NOT NULL DEFAULT 'task'";

    /// <summary>
    /// Re-derives the rows that are a function of BoardCards rather than schema: the
    /// BoardCardSequences high-water mark and board ownership of orphaned lanes.
    /// These ran on every startup before the schema became a one-shot migration, and they have to
    /// keep doing so -- a state.db written by an older build, or restored from a partial backup,
    /// can hold cards with no sequence row at all, and the next CreateCardAsync then reissues a
    /// number and fails UNIQUE(ProjectPath, Number) with a raw SqliteException.
    ///
    /// Read-probed first so the ordinary startup (nothing to repair) never takes the write lock.
    /// </summary>
    private static void ReconcileDerivedRows(SqliteConnection connection)
    {
        using (var probe = connection.CreateCommand())
        {
            probe.CommandText = $"""
                SELECT 1 WHERE EXISTS (
                    SELECT 1 FROM BoardCards c
                    LEFT JOIN BoardCardSequences s ON s.ProjectPath = c.ProjectPath{ProjectPathCollation}
                    WHERE s.ProjectPath IS NULL OR s.LastNumber < c.Number
                ) OR EXISTS (
                    SELECT 1 FROM BoardColumns k WHERE k.BoardId IS NULL
                );
                """;
            if (probe.ExecuteScalar() is null)
                return;
        }

        using var transaction = connection.BeginTransaction(deferred: false);
        using (var repair = connection.CreateCommand())
        {
            repair.Transaction = transaction;
            repair.CommandText = CardSequenceReseedSql + OrphanLaneAdoptionSql;
            repair.Parameters.AddWithValue("$now", ToDb(DateTime.UtcNow));
            repair.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    /// <summary>Idempotent: raises each project's high-water mark to its highest live card number.</summary>
    // Properties, not static readonly fields: both interpolate ProjectPathCollation, which is
    // declared further down and would still be null while an earlier field initialised.
    internal static string CardSequenceReseedSql => $"""
        INSERT INTO BoardCardSequences (ProjectPath, LastNumber)
            SELECT ProjectPath, MAX(Number) FROM BoardCards
            GROUP BY ProjectPath{ProjectPathCollation}
        ON CONFLICT(ProjectPath) DO UPDATE SET LastNumber = MAX(LastNumber, excluded.LastNumber);
        """;

    /// <summary>
    /// Idempotent: lanes without a board (pre-board/4 rows, or rows an older binary added) join
    /// their project's default board, which is created — named <see cref="DefaultBoardName"/> —
    /// when the project has none. Board ids follow the same <c>brd_</c> + 12 hex shape as NewId.
    /// </summary>
    internal static string OrphanLaneAdoptionSql => $"""
        INSERT INTO Boards (Id, ProjectPath, Name, Position, CreatedUTC, UpdatedUTC)
            SELECT 'brd_' || lower(hex(randomblob(6))), MIN(k.ProjectPath), '{DefaultBoardName}', 0, $now, $now
            FROM BoardColumns k
            WHERE k.BoardId IS NULL
              AND NOT EXISTS (SELECT 1 FROM Boards b WHERE b.ProjectPath = k.ProjectPath{ProjectPathCollation})
            GROUP BY k.ProjectPath{ProjectPathCollation};
        UPDATE BoardColumns SET BoardId = (
            SELECT b.Id FROM Boards b WHERE b.ProjectPath = BoardColumns.ProjectPath{ProjectPathCollation}
            ORDER BY b.Position, b.CreatedUTC LIMIT 1)
        WHERE BoardId IS NULL;
        """;

    static partial void EnsureAttachmentSchema(SqliteConnection connection, SqliteTransaction transaction);

    private static string NewId(string prefix) => prefix + "_" + Guid.NewGuid().ToString("N")[..12];

    private static string SerializeTags(IReadOnlyList<string> tags) =>
        JsonSerializer.Serialize(tags.ToList(), StorageJsonSerializerContext.Default.ListString);

    private static IReadOnlyList<string> DeserializeTags(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            return JsonSerializer.Deserialize(json, StorageJsonSerializerContext.Default.ListString) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    // Same platform rule as ProjectPathComparer: Windows and macOS fold case, Linux does not.
    private static readonly string ProjectPathCollation =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? " COLLATE NOCASE" : string.Empty;

    public static string NormalizeProjectPath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));

    private static string ToDb(DateTime value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTime ParseDb(string value) => DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();

    /// <summary>The lanes a new project's board starts with (the placeholder's seed, minus its sample cards).</summary>
    public static readonly IReadOnlyList<(string Name, string Color)> DefaultLanes =
    [
        ("Backlog", "#64748b"),
        ("Ready", "#3b82f6"),
        ("Build", "#06b6d4"),
        ("Review", "#f59e0b"),
        ("Done", "#10b981")
    ];

    internal const string BoardsTableSql = """
        CREATE TABLE IF NOT EXISTS Boards (
            Id TEXT PRIMARY KEY,
            ProjectPath TEXT NOT NULL,
            Name TEXT NOT NULL,
            Position INTEGER NOT NULL,
            CreatedUTC TEXT NOT NULL,
            UpdatedUTC TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS IX_Boards_Project ON Boards(ProjectPath, Position);
        """;

    private static readonly string SchemaSql = $"""
        {BoardsTableSql}

        CREATE TABLE IF NOT EXISTS BoardColumns (
            Id TEXT PRIMARY KEY,
            ProjectPath TEXT NOT NULL,
            Name TEXT NOT NULL,
            WipLimit INTEGER NULL,
            Position INTEGER NOT NULL,
            Color TEXT NOT NULL,
            CreatedUTC TEXT NOT NULL,
            UpdatedUTC TEXT NOT NULL,
            BoardId TEXT NULL
        );
        CREATE INDEX IF NOT EXISTS IX_BoardColumns_Project ON BoardColumns(ProjectPath, Position);

        CREATE TABLE IF NOT EXISTS BoardCards (
            Id TEXT PRIMARY KEY,
            ProjectPath TEXT NOT NULL,
            Number INTEGER NOT NULL,
            ColumnId TEXT NOT NULL REFERENCES BoardColumns(Id),
            Position INTEGER NOT NULL,
            Title TEXT NOT NULL,
            Description TEXT NOT NULL DEFAULT '',
            Assignee TEXT NULL,
            Priority TEXT NOT NULL DEFAULT 'medium',
            Type TEXT NOT NULL DEFAULT 'task',
            Points INTEGER NULL,
            Tags TEXT NOT NULL DEFAULT '[]',
            Blocked INTEGER NOT NULL DEFAULT 0,
            CreatedUTC TEXT NOT NULL,
            UpdatedUTC TEXT NOT NULL,
            UNIQUE(ProjectPath, Number)
        );
        CREATE INDEX IF NOT EXISTS IX_BoardCards_Column ON BoardCards(ColumnId, Position);

        CREATE TABLE IF NOT EXISTS BoardCardSequences (
            ProjectPath TEXT PRIMARY KEY{ProjectPathCollation},
            LastNumber INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS BoardComments (
            Id TEXT PRIMARY KEY,
            CardId TEXT NOT NULL REFERENCES BoardCards(Id) ON DELETE CASCADE,
            AuthorKind TEXT NOT NULL,
            AuthorLabel TEXT NOT NULL,
            AuthorCli TEXT NULL,
            SessionId TEXT NULL,
            Body TEXT NOT NULL,
            CreatedUTC TEXT NOT NULL,
            Kind TEXT NOT NULL DEFAULT 'comment'
        );
        CREATE INDEX IF NOT EXISTS IX_BoardComments_Card ON BoardComments(CardId, CreatedUTC);

        CREATE TABLE IF NOT EXISTS BoardCardSessions (
            SessionId TEXT PRIMARY KEY,
            CardId TEXT NOT NULL REFERENCES BoardCards(Id) ON DELETE CASCADE,
            TabId TEXT NULL,
            Selection TEXT NOT NULL,
            Cli TEXT NOT NULL,
            DisplayName TEXT NOT NULL,
            Origin TEXT NOT NULL,
            CreatedUTC TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS IX_BoardCardSessions_Card ON BoardCardSessions(CardId);

        CREATE TABLE IF NOT EXISTS BoardAttachments (
            Id TEXT PRIMARY KEY,
            CardId TEXT NOT NULL REFERENCES BoardCards(Id) ON DELETE CASCADE,
            Name TEXT NOT NULL,
            MimeType TEXT NOT NULL,
            Bytes INTEGER NOT NULL,
            DataUrl TEXT NOT NULL,
            CreatedUTC TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS IX_BoardAttachments_Card ON BoardAttachments(CardId);

        CREATE TABLE IF NOT EXISTS BoardCommits (
            CardId TEXT NOT NULL REFERENCES BoardCards(Id) ON DELETE CASCADE,
            Sha TEXT NOT NULL,
            Author TEXT NOT NULL,
            Message TEXT NOT NULL,
            CommittedUTC TEXT NOT NULL,
            LinkedUTC TEXT NOT NULL,
            PRIMARY KEY (CardId, Sha)
        );

        CREATE TABLE IF NOT EXISTS BoardCommitSnapshots (
            CardId TEXT NOT NULL,
            Sha TEXT NOT NULL,
            SnapshotJson TEXT NOT NULL,
            PRIMARY KEY (CardId, Sha),
            FOREIGN KEY (CardId, Sha) REFERENCES BoardCommits(CardId, Sha) ON DELETE CASCADE
        );
        """;
}
