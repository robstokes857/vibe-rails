using System.Data;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VibeRails.DTOs;

namespace VibeRails.Services.Board;

public interface IBoardStore
{
    Task<bool> EnsureDefaultColumnsAsync(string projectPath, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BoardColumnRecord>> GetColumnsAsync(string projectPath, CancellationToken cancellationToken = default);
    Task<BoardColumnRecord?> GetColumnAsync(string projectPath, string columnId, CancellationToken cancellationToken = default);
    Task<BoardColumnRecord> CreateColumnAsync(string projectPath, string name, int? wipLimit, string color, CancellationToken cancellationToken = default);
    Task<BoardColumnRecord?> UpdateColumnAsync(string projectPath, string columnId, string? name, int? wipLimit, bool clearWipLimit, string? color, CancellationToken cancellationToken = default);
    Task<BoardColumnDeleteResult?> DeleteColumnAsync(string projectPath, string columnId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BoardColumnRecord>> ReorderColumnsAsync(string projectPath, IReadOnlyList<string> orderedIds, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BoardCardRecord>> GetCardsAsync(string projectPath, CancellationToken cancellationToken = default);
    Task<BoardCardRecord?> FindCardAsync(string projectPath, string idOrKey, CancellationToken cancellationToken = default);
    Task<BoardCardDetailRecord?> GetCardDetailAsync(string projectPath, string idOrKey, CancellationToken cancellationToken = default);
    Task<BoardCardRecord> CreateCardAsync(string projectPath, NewBoardCard card, CancellationToken cancellationToken = default);
    Task<BoardCardRecord?> UpdateCardAsync(string projectPath, string cardId, BoardCardPatch patch, CancellationToken cancellationToken = default);
    Task<bool> DeleteCardAsync(string projectPath, string cardId, CancellationToken cancellationToken = default);
    Task<BoardCardRecord?> MoveCardAsync(string projectPath, string cardId, string columnId, int? position, CancellationToken cancellationToken = default);

    Task<BoardCommentRecord?> AddCommentAsync(string projectPath, string cardId, BoardAuthor author, string body, CancellationToken cancellationToken = default);

    Task<BoardSessionRecord?> LinkSessionAsync(string projectPath, string cardId, string sessionId, string? tabId, string selection, string cli, string displayName, string origin, CancellationToken cancellationToken = default);
    Task<BoardSessionRecord?> RenameSessionAsync(string projectPath, string cardId, string sessionId, string displayName, CancellationToken cancellationToken = default);
    Task<bool> UnlinkSessionAsync(string projectPath, string cardId, string sessionId, CancellationToken cancellationToken = default);
    Task<BoardSessionLink?> FindSessionLinkAsync(string sessionId, CancellationToken cancellationToken = default);
    Task<BoardAuthor?> FindSessionAuthorAsync(string sessionId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BoardSessionRecord>> GetSessionsForProjectAsync(string projectPath, CancellationToken cancellationToken = default);

    Task<BoardAttachmentRecord?> AddAttachmentAsync(string projectPath, string cardId, string name, string mimeType, long bytes, string dataUrl, CancellationToken cancellationToken = default);
    Task<bool> DeleteAttachmentAsync(string projectPath, string cardId, string attachmentId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BoardCommitRecord>> GetCommitsAsync(string projectPath, string cardId, CancellationToken cancellationToken = default);
    Task<BoardCommitRecord?> AddCommitAsync(string projectPath, string cardId, string sha, string author, string message, DateTime committedUtc, SandboxDiffResponse snapshot, CancellationToken cancellationToken = default);
    Task<SandboxDiffResponse?> GetCommitSnapshotAsync(string projectPath, string cardId, string sha, CancellationToken cancellationToken = default);
    Task<bool> RemoveCommitAsync(string projectPath, string cardId, string sha, CancellationToken cancellationToken = default);
}

/// <summary>
/// SQLite persistence for the kanban board. Singleton with its own connection string and its own
/// schema owner (the <see cref="VibeRails.DB.JobStore"/> pattern): connection-per-operation, busy
/// timeout on every open, and no dependency on <c>Repository.EnsureInitialized()</c> so the stdio
/// MCP host can construct it without running the dashboard's migration pass.
///
/// Every row is scoped by <c>ProjectPath</c> (the git root the board belongs to). Callers never
/// pass a project path they got from a request — see <see cref="IBoardProjectResolver"/>.
/// </summary>
public sealed class BoardStore : IBoardStore
{
    private readonly string _connectionString;

    public BoardStore(string connectionString)
    {
        _connectionString = connectionString;
        EnsureSchema();
    }

    // ------------------------------------------------------------------ columns

    public async Task<bool> EnsureDefaultColumnsAsync(string projectPath, CancellationToken cancellationToken = default)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var count = await ScalarLongAsync(connection, transaction,
            $"SELECT COUNT(*) FROM BoardColumns WHERE ProjectPath = $project{ProjectPathCollation}",
            ("$project", project), cancellationToken);
        if (count > 0)
            return false;

        var now = DateTime.UtcNow;
        var position = 0;
        foreach (var (name, wip, color) in DefaultLanes)
        {
            await InsertColumnAsync(connection, transaction, NewId("col"), project, name, wip, position++, color, now, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<BoardColumnRecord>> GetColumnsAsync(string projectPath, CancellationToken cancellationToken = default)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        return await ReadColumnsAsync(connection, null, project, cancellationToken);
    }

    public async Task<BoardColumnRecord?> GetColumnAsync(string projectPath, string columnId, CancellationToken cancellationToken = default)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        return await ReadColumnAsync(connection, null, project, columnId, cancellationToken);
    }

    public async Task<BoardColumnRecord> CreateColumnAsync(string projectPath, string name, int? wipLimit, string color, CancellationToken cancellationToken = default)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var position = (int)await ScalarLongAsync(connection, transaction,
            $"SELECT COUNT(*) FROM BoardColumns WHERE ProjectPath = $project{ProjectPathCollation}",
            ("$project", project), cancellationToken);
        var id = NewId("col");
        var now = DateTime.UtcNow;
        await InsertColumnAsync(connection, transaction, id, project, name, wipLimit, position, color, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new BoardColumnRecord(id, project, name, wipLimit, position, color, now, now);
    }

    public async Task<BoardColumnRecord?> UpdateColumnAsync(string projectPath, string columnId, string? name, int? wipLimit, bool clearWipLimit, string? color, CancellationToken cancellationToken = default)
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
            WipLimit = clearWipLimit ? null : (wipLimit ?? existing.WipLimit),
            Color = color ?? existing.Color,
            UpdatedUtc = DateTime.UtcNow
        };

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE BoardColumns SET Name = $name, WipLimit = $wip, Color = $color, UpdatedUTC = $updated
                WHERE Id = $id;
                """;
            command.Parameters.AddWithValue("$name", updated.Name);
            command.Parameters.AddWithValue("$wip", updated.WipLimit is int w ? w : DBNull.Value);
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
        var columns = await ReadColumnsAsync(connection, transaction, project, cancellationToken);
        var target = columns.FirstOrDefault(c => string.Equals(c.Id, columnId.Trim(), StringComparison.Ordinal));
        if (target is null)
            return null;
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

    public async Task<IReadOnlyList<BoardColumnRecord>> ReorderColumnsAsync(string projectPath, IReadOnlyList<string> orderedIds, CancellationToken cancellationToken = default)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var columns = await ReadColumnsAsync(connection, transaction, project, cancellationToken);
        var known = columns.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var requested = orderedIds.Select(id => id?.Trim() ?? string.Empty).ToList();
        if (requested.Count != known.Count
            || requested.Distinct(StringComparer.Ordinal).Count() != requested.Count
            || requested.Any(id => !known.Contains(id)))
        {
            throw new BoardValidationException("The lane order must list every lane on this board exactly once.");
        }

        await WriteColumnPositionsAsync(connection, transaction, requested, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await ReadColumnsAsync(connection, null, project, cancellationToken);
    }

    // ------------------------------------------------------------------ cards

    public async Task<IReadOnlyList<BoardCardRecord>> GetCardsAsync(string projectPath, CancellationToken cancellationToken = default)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = CardSelectSql + $" WHERE c.ProjectPath = $project{ProjectPathCollation} ORDER BY c.ColumnId, c.Position, c.Number;";
        command.Parameters.AddWithValue("$project", project);
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
            await ReadCommentsAsync(connection, card.Id, cancellationToken),
            await ReadSessionsAsync(connection, card.Id, cancellationToken),
            await ReadAttachmentsAsync(connection, card.Id, cancellationToken),
            await ReadCommitsAsync(connection, card.Id, cancellationToken));
    }

    public async Task<BoardCardRecord> CreateCardAsync(string projectPath, NewBoardCard card, CancellationToken cancellationToken = default)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);

        var columns = await ReadColumnsAsync(connection, transaction, project, cancellationToken);
        if (columns.Count == 0)
            throw new BoardValidationException("No lane available. Create a lane first.");
        BoardColumnRecord column;
        if (string.IsNullOrWhiteSpace(card.ColumnId))
        {
            column = columns.OrderBy(c => c.Position).First();
        }
        else
        {
            column = columns.FirstOrDefault(c => string.Equals(c.Id, card.ColumnId.Trim(), StringComparison.Ordinal))
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
                    (Id, ProjectPath, Number, ColumnId, Position, Title, Description, Assignee, Priority, Points, Tags, Blocked, CreatedUTC, UpdatedUTC)
                VALUES
                    ($id, $project, $number, $column, $position, $title, $description, $assignee, $priority, $points, $tags, $blocked, $created, $updated);
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
            insert.Parameters.AddWithValue("$points", card.Points is int p ? p : DBNull.Value);
            insert.Parameters.AddWithValue("$tags", SerializeTags(card.Tags));
            insert.Parameters.AddWithValue("$blocked", card.Blocked ? 1 : 0);
            insert.Parameters.AddWithValue("$created", ToDb(now));
            insert.Parameters.AddWithValue("$updated", ToDb(now));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);

        return new BoardCardRecord(id, project, number, column.Id, position, card.Title, card.Description,
            card.Assignee, card.Priority, card.Points, card.Tags, card.Blocked, 0, now, now);
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
        var position = existing.Position;
        if (moving)
        {
            var column = await ReadColumnAsync(connection, transaction, project, patch.ColumnId!.Trim(), cancellationToken)
                ?? throw new BoardValidationException($"Lane not found: {patch.ColumnId}");
            columnId = column.Id;
            position = (int)await ScalarLongAsync(connection, transaction,
                "SELECT COUNT(*) FROM BoardCards WHERE ColumnId = $column AND Id <> $id",
                ("$column", columnId), cancellationToken, ("$id", (object)existing.Id));
        }

        var updated = existing with
        {
            ColumnId = columnId,
            Position = position,
            Title = patch.Title ?? existing.Title,
            Description = patch.Description ?? existing.Description,
            Assignee = patch.ClearAssignee ? null : (patch.Assignee ?? existing.Assignee),
            Priority = patch.Priority ?? existing.Priority,
            Points = patch.ClearPoints ? null : (patch.Points ?? existing.Points),
            Tags = patch.Tags ?? existing.Tags,
            Blocked = patch.Blocked ?? existing.Blocked,
            UpdatedUtc = DateTime.UtcNow
        };

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE BoardCards SET ColumnId = $column, Position = $position, Title = $title, Description = $description,
                    Assignee = $assignee, Priority = $priority, Points = $points, Tags = $tags, Blocked = $blocked, UpdatedUTC = $updated
                WHERE Id = $id;
                """;
            command.Parameters.AddWithValue("$column", updated.ColumnId);
            command.Parameters.AddWithValue("$position", updated.Position);
            command.Parameters.AddWithValue("$title", updated.Title);
            command.Parameters.AddWithValue("$description", updated.Description);
            command.Parameters.AddWithValue("$assignee", (object?)updated.Assignee ?? DBNull.Value);
            command.Parameters.AddWithValue("$priority", updated.Priority);
            command.Parameters.AddWithValue("$points", updated.Points is int p ? p : DBNull.Value);
            command.Parameters.AddWithValue("$tags", SerializeTags(updated.Tags));
            command.Parameters.AddWithValue("$blocked", updated.Blocked ? 1 : 0);
            command.Parameters.AddWithValue("$updated", ToDb(updated.UpdatedUtc));
            command.Parameters.AddWithValue("$id", updated.Id);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        if (moving)
        {
            await RenumberColumnAsync(connection, transaction, existing.ColumnId, cancellationToken);
            await RenumberColumnAsync(connection, transaction, columnId, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return await ReadCardAsync(connection, null, project, updated.Id, cancellationToken);
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
        await using var connection = await OpenAsync(cancellationToken);
        // Board-only databases (including a fresh stdio host) may not yet have Sessions.
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
        await using var linked = connection.CreateCommand();
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

    public async Task<BoardCommentRecord?> AddCommentAsync(string projectPath, string cardId, BoardAuthor author, string body, CancellationToken cancellationToken = default)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var card = await ReadCardAsync(connection, transaction, project, cardId, cancellationToken);
        if (card is null)
            return null;

        var comment = new BoardCommentRecord(NewId("cm"), card.Id, author, body, DateTime.UtcNow);
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO BoardComments (Id, CardId, AuthorKind, AuthorLabel, AuthorCli, SessionId, Body, CreatedUTC)
                VALUES ($id, $card, $kind, $label, $cli, $session, $body, $created);
                """;
            insert.Parameters.AddWithValue("$id", comment.Id);
            insert.Parameters.AddWithValue("$card", card.Id);
            insert.Parameters.AddWithValue("$kind", author.Kind);
            insert.Parameters.AddWithValue("$label", author.Label);
            insert.Parameters.AddWithValue("$cli", (object?)author.Cli ?? DBNull.Value);
            insert.Parameters.AddWithValue("$session", (object?)author.SessionId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$body", body);
            insert.Parameters.AddWithValue("$created", ToDb(comment.CreatedUtc));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await TouchCardAsync(connection, transaction, card.Id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return comment;
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

    public async Task<BoardAttachmentRecord?> AddAttachmentAsync(string projectPath, string cardId, string name, string mimeType, long bytes, string dataUrl, CancellationToken cancellationToken = default)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var card = await ReadCardAsync(connection, transaction, project, cardId, cancellationToken);
        if (card is null)
            return null;

        var record = new BoardAttachmentRecord(NewId("att"), card.Id, name, mimeType, bytes, dataUrl, DateTime.UtcNow);
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO BoardAttachments (Id, CardId, Name, MimeType, Bytes, DataUrl, CreatedUTC)
                VALUES ($id, $card, $name, $mime, $bytes, $data, $created);
                """;
            insert.Parameters.AddWithValue("$id", record.Id);
            insert.Parameters.AddWithValue("$card", record.CardId);
            insert.Parameters.AddWithValue("$name", record.Name);
            insert.Parameters.AddWithValue("$mime", record.MimeType);
            insert.Parameters.AddWithValue("$bytes", record.Bytes);
            insert.Parameters.AddWithValue("$data", record.DataUrl);
            insert.Parameters.AddWithValue("$created", ToDb(record.CreatedUtc));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await TouchCardAsync(connection, transaction, card.Id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return record;
    }

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
            await TouchCardAsync(connection, transaction, card.Id, cancellationToken);
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
        var snapshotJson = JsonSerializer.Serialize(snapshot, AppJsonSerializerContext.Default.SandboxDiffResponse);
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
        return json is null ? null : JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.SandboxDiffResponse);
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
        "SELECT Id, ProjectPath, Name, WipLimit, Position, Color, CreatedUTC, UpdatedUTC FROM BoardColumns";

    private const string CardSelectSql = """
        SELECT c.Id, c.ProjectPath, c.Number, c.ColumnId, c.Position, c.Title, c.Description, c.Assignee, c.Priority,
               c.Points, c.Tags, c.Blocked, c.CreatedUTC, c.UpdatedUTC,
               (SELECT COUNT(*) FROM BoardComments m WHERE m.CardId = c.Id) AS CommentCount
        FROM BoardCards c
        """;

    private const string SessionSelectSql =
        "SELECT s.SessionId, s.CardId, s.TabId, s.Selection, s.Cli, s.DisplayName, s.Origin, s.CreatedUTC FROM BoardCardSessions s";

    private static async Task<IReadOnlyList<BoardColumnRecord>> ReadColumnsAsync(SqliteConnection connection, SqliteTransaction? transaction, string project, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = ColumnSelectSql + $" WHERE ProjectPath = $project{ProjectPathCollation} ORDER BY Position, CreatedUTC;";
        command.Parameters.AddWithValue("$project", project);
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
        command.CommandText = ColumnSelectSql + $" WHERE ProjectPath = $project{ProjectPathCollation} AND Id = $id LIMIT 1;";
        command.Parameters.AddWithValue("$project", project);
        command.Parameters.AddWithValue("$id", columnId.Trim());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadColumn(reader) : null;
    }

    private static BoardColumnRecord ReadColumn(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetInt32(3),
        reader.GetInt32(4),
        reader.GetString(5),
        ParseDb(reader.GetString(6)),
        ParseDb(reader.GetString(7)));

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
        ParseDb(reader.GetString(13)));

    private static async Task<IReadOnlyList<BoardCommentRecord>> ReadCommentsAsync(SqliteConnection connection, string cardId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, CardId, AuthorKind, AuthorLabel, AuthorCli, SessionId, Body, CreatedUTC
            FROM BoardComments WHERE CardId = $card ORDER BY CreatedUTC, Id;
            """;
        command.Parameters.AddWithValue("$card", cardId);
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
                ParseDb(reader.GetString(7))));
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

    private static async Task InsertColumnAsync(SqliteConnection connection, SqliteTransaction transaction, string id, string project, string name, int? wipLimit, int position, string color, DateTime now, CancellationToken cancellationToken)
    {
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO BoardColumns (Id, ProjectPath, Name, WipLimit, Position, Color, CreatedUTC, UpdatedUTC)
            VALUES ($id, $project, $name, $wip, $position, $color, $created, $updated);
            """;
        insert.Parameters.AddWithValue("$id", id);
        insert.Parameters.AddWithValue("$project", project);
        insert.Parameters.AddWithValue("$name", name);
        insert.Parameters.AddWithValue("$wip", wipLimit is int w ? w : DBNull.Value);
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

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private void EnsureSchema()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SchemaSql;
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private static string NewId(string prefix) => prefix + "_" + Guid.NewGuid().ToString("N")[..12];

    private static string SerializeTags(IReadOnlyList<string> tags) =>
        JsonSerializer.Serialize(tags.ToList(), AppJsonSerializerContext.Default.ListString);

    private static IReadOnlyList<string> DeserializeTags(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            return JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.ListString) ?? [];
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
    public static readonly IReadOnlyList<(string Name, int? WipLimit, string Color)> DefaultLanes =
    [
        ("Backlog", null, "#64748b"),
        ("Ready", 8, "#3b82f6"),
        ("Build", 4, "#06b6d4"),
        ("Review", 3, "#f59e0b"),
        ("Shipped", null, "#10b981")
    ];

    private static readonly string SchemaSql = $"""
        CREATE TABLE IF NOT EXISTS BoardColumns (
            Id TEXT PRIMARY KEY,
            ProjectPath TEXT NOT NULL,
            Name TEXT NOT NULL,
            WipLimit INTEGER NULL,
            Position INTEGER NOT NULL,
            Color TEXT NOT NULL,
            CreatedUTC TEXT NOT NULL,
            UpdatedUTC TEXT NOT NULL
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
        INSERT INTO BoardCardSequences (ProjectPath, LastNumber)
            SELECT ProjectPath, MAX(Number) FROM BoardCards
            GROUP BY ProjectPath{ProjectPathCollation}
        ON CONFLICT(ProjectPath) DO UPDATE SET LastNumber = MAX(LastNumber, excluded.LastNumber);

        CREATE TABLE IF NOT EXISTS BoardComments (
            Id TEXT PRIMARY KEY,
            CardId TEXT NOT NULL REFERENCES BoardCards(Id) ON DELETE CASCADE,
            AuthorKind TEXT NOT NULL,
            AuthorLabel TEXT NOT NULL,
            AuthorCli TEXT NULL,
            SessionId TEXT NULL,
            Body TEXT NOT NULL,
            CreatedUTC TEXT NOT NULL
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
