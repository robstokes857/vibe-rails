using System.Data;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VibeRails.DB;
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

    // Reads that reach into state.db (Jobs, JobRuns, Sessions) must tolerate a file another host
    // has not initialised yet, so each one probes the schema first. The probes are hot — the
    // Board activity poll, card list/detail and every get_board_card / list_board_columns pay
    // several per call — and their answer never changes once true, so remember it here.
    private readonly SqliteSchemaFeatures _stateFeatures = new();

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

    /// <summary>
    /// Last-resort name for a project's first board, used only when both the custom project
    /// name and the repository folder name are missing or are themselves "Main".
    /// </summary>
    public const string FallbackBoardName = "Board";

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

    public Task<BoardRecord> CreateBoardAsync(string projectPath, string name, CancellationToken cancellationToken = default) =>
        CreateBoardAsync(projectPath, name, null, cancellationToken);

    public async Task<BoardRecord> CreateBoardAsync(string projectPath, string name, string? displayPrefix, CancellationToken cancellationToken = default)
    {
        var project = NormalizeProjectPath(projectPath);
        var prefix = string.IsNullOrWhiteSpace(displayPrefix) ? null : BoardDisplayIds.NormalizePrefix(displayPrefix);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var position = (int)await ScalarLongAsync(connection, transaction,
            $"SELECT COUNT(*) FROM Boards WHERE ProjectPath = $project{ProjectPathCollation}",
            ("$project", project), cancellationToken);
        var board = await InsertBoardWithDefaultLanesAsync(connection, transaction, project, name, position, cancellationToken, prefix);
        await transaction.CommitAsync(cancellationToken);
        return board;
    }

    public async Task<BoardRecord?> RenameBoardAsync(string projectPath, string boardId, string? name, string? displayPrefix, CancellationToken cancellationToken = default)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var existing = await ReadBoardAsync(connection, transaction, project, boardId, cancellationToken);
        if (existing is null)
            return null;
        // Null keeps a value; an empty prefix returns future cards to the repository default.
        var prefix = displayPrefix is null ? existing.DisplayPrefix
            : displayPrefix.Trim().Length == 0 ? null
            : BoardDisplayIds.NormalizePrefix(displayPrefix);
        var updated = existing with { Name = name ?? existing.Name, UpdatedUtc = DateTime.UtcNow, DisplayPrefix = prefix };
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "UPDATE Boards SET Name = $name, DisplayPrefix = $prefix, UpdatedUTC = $updated WHERE Id = $id;";
            command.Parameters.AddWithValue("$name", updated.Name);
            command.Parameters.AddWithValue("$prefix", (object?)updated.DisplayPrefix ?? DBNull.Value);
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
        // Read before the board transaction: this opens state.db, and holding board.db's write
        // lock across that open is how the two files deadlock each other.
        var name = await ResolveDefaultBoardNameAsync(project, cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var count = await ScalarLongAsync(connection, transaction,
            $"SELECT COUNT(*) FROM Boards WHERE ProjectPath = $project{ProjectPathCollation}",
            ("$project", project), cancellationToken);
        if (count > 0)
            return false;

        await InsertBoardWithDefaultLanesAsync(connection, transaction, project, name, 0, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// The name a project's first board gets: the custom project name the user set, otherwise
    /// the repository folder name. "Main" is never used; a folder that is itself called Main
    /// falls through to <see cref="FallbackBoardName"/>.
    /// </summary>
    private async Task<string> ResolveDefaultBoardNameAsync(string project, CancellationToken cancellationToken)
    {
        string? custom = null;
        try
        {
            await using var state = await OpenStateAsync(cancellationToken);
            await using var command = state.CreateCommand();
            command.CommandText = SqlStrings.SelectLatestProjectDisplayNameByWorkingDirectory;
            command.Parameters.AddWithValue("$workingDirectory", project);
            var value = await command.ExecuteScalarAsync(cancellationToken);
            custom = value is string text ? text : null;
        }
        catch (SqliteException)
        {
            // A host whose state.db has no Sessions table yet still gets a board; the folder name covers it.
        }

        var folder = Path.GetFileName(project.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return ChooseDefaultBoardName(custom, folder);
    }

    /// <summary>Custom name, then folder name, skipping blanks and the retired "Main" default.</summary>
    internal static string ChooseDefaultBoardName(string? customName, string? folderName)
    {
        foreach (var candidate in new[] { customName, folderName })
        {
            var name = candidate?.Trim();
            if (string.IsNullOrEmpty(name) || name.Equals("Main", StringComparison.OrdinalIgnoreCase))
                continue;
            return name.Length > 60 ? name[..60] : name;
        }
        return FallbackBoardName;
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
            WHERE c.ProjectPath = $project{ProjectPathCollation} AND k.BoardId IS NOT NULL AND c.DeletedUTC IS NULL
            GROUP BY k.BoardId;
            """;
        command.Parameters.AddWithValue("$project", project);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            counts[reader.GetString(0)] = reader.GetInt32(1);
        return counts;
    }

    public async Task<IReadOnlyDictionary<string, int>> CountCardsByColumnAsync(string projectPath, CancellationToken cancellationToken = default, string? boardId = null)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var board = await ResolveBoardIdAsync(connection, null, project, boardId, cancellationToken);
        if (board is null)
            return counts;
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT c.ColumnId, COUNT(*) FROM BoardCards c JOIN BoardColumns k ON k.Id = c.ColumnId
            WHERE c.ProjectPath = $project{ProjectPathCollation} AND k.BoardId = $board AND c.DeletedUTC IS NULL
            GROUP BY c.ColumnId;
            """;
        command.Parameters.AddWithValue("$project", project);
        command.Parameters.AddWithValue("$board", board);
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

        // Cards fall to the left-most remaining lane, appended after its existing cards within each
        // attention group; RenumberColumnAsync then makes the positions dense.
        var destination = columns.Where(c => c.Id != target.Id).OrderBy(c => c.Position).First();
        var destinationCards = await ReadColumnCardIdsAsync(connection, transaction, destination.Id, cancellationToken);
        var movingCards = await ReadColumnCardIdsAsync(connection, transaction, target.Id, cancellationToken);
        var nowUtc = DateTime.UtcNow;
        var now = ToDb(nowUtc);
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
            await LogCardMovedAsync(connection, transaction, cardId, target, destination, BoardAuthor.System(), nowUtc, cancellationToken);
        }

        await using (var delete = connection.CreateCommand())
        {
            // Soft-deleted cards still reference the lane and would block its delete. They follow
            // the live cards without a log entry, and the lane-entry triggers their move fires are
            // removed at once: a deleted card never runs an Automation.
            delete.Transaction = transaction;
            delete.CommandText = """
                UPDATE BoardCards SET ColumnId = $destination WHERE ColumnId = $id AND DeletedUTC IS NOT NULL;
                DELETE FROM BoardPendingAutomations WHERE CardId IN (SELECT Id FROM BoardCards WHERE ColumnId = $destination AND DeletedUTC IS NOT NULL);
                DELETE FROM BoardPendingAdditionalAutomations WHERE CardId IN (SELECT Id FROM BoardCards WHERE ColumnId = $destination AND DeletedUTC IS NOT NULL);
                DELETE FROM BoardColumns WHERE Id = $id;
                """;
            delete.Parameters.AddWithValue("$id", target.Id);
            delete.Parameters.AddWithValue("$destination", destination.Id);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        var remaining = columns.Where(c => c.Id != target.Id).OrderBy(c => c.Position).Select(c => c.Id).ToList();
        await RenumberColumnAsync(connection, transaction, destination.Id, cancellationToken);
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
               AND c.DeletedUTC IS NULL
             ORDER BY c.ColumnId, c.Flagged DESC, c.Position, c.Number;
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
            await ReadCommentsAsync(connection, card.Id, cancellationToken),
            await ReadSessionsAsync(connection, card.Id, cancellationToken),
            await ReadAttachmentsAsync(connection, card.Id, cancellationToken),
            await ReadCommitsAsync(connection, card.Id, cancellationToken),
            [])
        {
            LinkedCards = await ReadLinkedCardsAsync(connection, project, card.Id, cancellationToken, includeOtherProjects: true),
            PreviousWork = await ReadHandoffAsync(connection, card.Id, cancellationToken)
        };
    }

    public async Task<BoardCardRecord> CreateCardAsync(string projectPath, NewBoardCard card, CancellationToken cancellationToken = default, BoardAuthor? author = null)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var created = await InsertCardAsync(connection, transaction, project, card, author ?? BoardAuthor.User(), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return created;
    }

    /// <summary>A card minted on viberails.ai keeps its id and key; its number seeds the local high-water mark.</summary>
    private sealed record SyncedCardIdentity(string CardId, string CardKey, int Number);

    /// <summary>
    /// How far above the project's high-water mark a pulled web card's number may move it. The hosted
    /// board numbers a web card one above the highest number it has seen, so a real gap is the cards
    /// created on the web since the last sync. A larger one (a corrupt or hostile key) would jump local
    /// numbering, or exhaust it at <see cref="int.MaxValue"/>.
    /// </summary>
    internal const int MaxAdoptedCardNumberGap = 10_000;

    /// <summary>
    /// Allocates the project's next card number inside the caller's write transaction. The high-water
    /// mark lives apart from card rows, so deleting even the last card cannot make an old key available
    /// again, and allocation and insertion share the transaction across dashboard and MCP processes.
    /// A web-minted <paramref name="seed"/> above the mark and within <see cref="MaxAdoptedCardNumberGap"/>
    /// is adopted, so the two sides keep numbering in step; any other seed takes the next local number.
    /// </summary>
    private static async Task<int> AllocateCardNumberAsync(SqliteConnection connection, SqliteTransaction transaction,
        string project, int seed, CancellationToken cancellationToken)
    {
        var number = await ScalarLongAsync(connection, transaction, """
            INSERT INTO BoardCardSequences (ProjectPath, LastNumber)
                VALUES ($project, CASE WHEN $seed BETWEEN 1 AND $gap THEN $seed ELSE 1 END)
            ON CONFLICT(ProjectPath) DO UPDATE SET LastNumber =
                CASE WHEN $seed > LastNumber AND $seed - LastNumber <= $gap THEN $seed ELSE LastNumber + 1 END
            RETURNING LastNumber;
            """, ("$project", project), cancellationToken, ("$seed", seed), ("$gap", MaxAdoptedCardNumberGap));
        // The rollback leaves the mark where it was; the message replaces an OverflowException.
        return number <= int.MaxValue
            ? (int)number
            : throw new BoardValidationException("This project has used every card number, so a new card cannot be numbered.");
    }

    /// <summary>
    /// Allocates the key and inserts the card, and its created log entry, inside the caller's write
    /// transaction. A <paramref name="synced"/> identity comes from a pulled web card: the number
    /// becomes the web number when that is just above the project's high-water mark (see
    /// <see cref="AllocateCardNumberAsync"/>), else the next local number, and the stored key is the
    /// web one either way.
    /// </summary>
    private async Task<BoardCardRecord> InsertCardAsync(
        SqliteConnection connection, SqliteTransaction transaction, string project, NewBoardCard card, BoardAuthor author, CancellationToken cancellationToken,
        SyncedCardIdentity? synced = null, BoardSyncStamp? stamp = null)
    {
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

        // The project's first card fixes its key prefix, in this same transaction, before the
        // number exists: a prefix chosen while cards already exist would have to be VB.
        var prefix = await EnsureProjectKeyPrefixAsync(connection, transaction, project, cancellationToken);
        var id = synced?.CardId ?? NewId("card");
        var number = await AllocateCardNumberAsync(connection, transaction, project, synced?.Number ?? 0, cancellationToken);
        // A pulled card keeps the number its web key carries. A local key skips a number whose short
        // form a label (or an imported key) already spells, so no label shadows a key minted after it.
        while (synced is null && await ShortKeyClaimedAsync(connection, transaction, project, prefix, number, id, cancellationToken))
            number = await AllocateCardNumberAsync(connection, transaction, project, 0, cancellationToken);
        const int position = 0;
        // The stored key cannot be re-guessed from the number alone; the unique index turns the
        // (astronomically unlikely) collision into a failed write rather than two cards on one key.
        var cardKey = synced?.CardKey ?? BoardKeys.NewStoredKey(prefix, number);
        var now = stamp?.CreatedUtc ?? DateTime.UtcNow;
        var keyNumber = synced?.Number ?? number;
        var displayId = card.DisplayId is not null || synced is not null
            ? await ResolveDisplayIdAsync(connection, transaction, project, column.BoardId, id, card.DisplayId ?? cardKey, synced is not null, keyNumber, cancellationToken)
            : await AllocateDisplayIdAsync(connection, transaction, project, column.BoardId, id, cancellationToken, keyNumber);

        // Who made it travels with the agent mark and nothing else, so a human card never names one.
        var agentMadeBy = card.AgentMade ? card.AgentMadeBy : null;
        var agentMadeSessionId = card.AgentMade ? card.AgentMadeSessionId : null;
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO BoardCards
                    (Id, ProjectPath, Number, ColumnId, Position, Title, Description, Assignee, Priority, Type, Points, Tags, Blocked, Flagged, CreatedUTC, UpdatedUTC, CardKey, DisplayId, AgentMade, AgentMadeBy, AgentMadeSessionId)
                VALUES
                    ($id, $project, $number, $column, $position, $title, $description, $assignee, $priority, $type, $points, $tags, $blocked, $flagged, $created, $updated, $cardKey, $displayId, $agentMade, $agentMadeBy, $agentMadeSession);
                """;
            insert.Parameters.AddWithValue("$id", id);
            insert.Parameters.AddWithValue("$cardKey", cardKey);
            insert.Parameters.AddWithValue("$displayId", displayId);
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
            insert.Parameters.AddWithValue("$agentMade", card.AgentMade ? 1 : 0);
            insert.Parameters.AddWithValue("$agentMadeBy", (object?)agentMadeBy ?? DBNull.Value);
            insert.Parameters.AddWithValue("$agentMadeSession", (object?)agentMadeSessionId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$created", ToDb(now));
            insert.Parameters.AddWithValue("$updated", ToDb(now));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await PromoteCardAsync(connection, transaction, id, cancellationToken);
        await WriteBaseLlmOptionsAsync(connection, transaction, id, card.BaseLlmOptions, cancellationToken);

        var created = new BoardCardRecord(id, project, number, column.Id, position, card.Title, card.Description,
            card.Assignee, card.Priority, card.Points, card.Tags, card.Blocked, 0, now, now, card.BaseLlmOptions, Type: card.Type, BoardId: column.BoardId, Flagged: card.Flagged, KeyPrefix: prefix, StoredKey: cardKey, StoredDisplayId: displayId, AgentMade: card.AgentMade,
            AgentMadeBy: agentMadeBy, AgentMadeSessionId: agentMadeSessionId);
        await LogCardCreatedAsync(connection, transaction, created, column.Name, author, cancellationToken, stamp);
        if (synced is not null) await ReconcileSyncedDisplayIdAsync(connection, transaction, created, card.DisplayId, cancellationToken);
        await InsertDraftLinksAsync(connection, transaction, created, card.LinkedCardIds, cancellationToken);
        return created;
    }

    public Task<BoardCardRecord?> UpdateCardAsync(string projectPath, string cardId, BoardCardPatch patch, CancellationToken cancellationToken = default, BoardAuthor? author = null) =>
        UpdateCardCoreAsync(projectPath, cardId, patch, author ?? BoardAuthor.User(), stamp: null, cancellationToken);

    /// <summary>The update, with the Card Log entry stamped when the change was pulled from viberails.ai.</summary>
    private async Task<BoardCardRecord?> UpdateCardCoreAsync(string projectPath, string cardId, BoardCardPatch patch, BoardAuthor author, BoardSyncStamp? stamp, CancellationToken cancellationToken)
    {
        // Remote state changes already carry their discussion separately. Local agents must
        // save the reason and flag together, even when calling the store directly.
        var flagReason = stamp is null ? BoardAttention.NormalizeReason(patch.Flagged, patch.FlagReason, author) : null;
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var existing = await ReadCardAsync(connection, transaction, project, cardId, cancellationToken, includeDeleted: stamp is not null);
        if (existing is null)
            return null;

        if (stamp is not null)
        {
            RequireSyncBoard(existing, stamp);
            if (await HasSyncStampAsync(connection, transaction, stamp, cancellationToken)) return existing;
            var locked = await GetFieldsChangedAfterAsync(connection, transaction, existing.Id, stamp.RemoteSeq, cancellationToken);
            patch = WithoutLockedFields(patch, locked);
            if (await IsDeletedAsync(connection, transaction, existing.Id, cancellationToken))
            {
                await LogCardChangedAsync(connection, transaction, existing, existing, null, null, author, cancellationToken, stamp);
                await transaction.CommitAsync(cancellationToken);
                return existing;
            }
        }

        var moving = !string.IsNullOrWhiteSpace(patch.ColumnId)
            && !string.Equals(patch.ColumnId.Trim(), existing.ColumnId, StringComparison.Ordinal);
        var columnId = existing.ColumnId;
        var boardId = existing.BoardId;
        const int position = 0;
        string? fromLaneName = null, toLaneName = null;
        if (moving)
        {
            var column = await ReadColumnAsync(connection, transaction, project, patch.ColumnId!.Trim(), cancellationToken)
                ?? throw new BoardValidationException($"Lane not found: {patch.ColumnId}");
            columnId = column.Id;
            boardId = column.BoardId;

            if (stamp?.BoardId is { } syncBoard && column.BoardId != syncBoard)
                throw new BoardValidationException("A synced move must stay on its published board.");
            await RequireSameSideOfSharingAsync(connection, transaction, existing.BoardId, column.BoardId, cancellationToken);
            toLaneName = column.Name;
            fromLaneName = (await ReadColumnAsync(connection, transaction, project, existing.ColumnId, cancellationToken))?.Name;
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

        var displayId = patch.DisplayId is null ? existing.DisplayId
            : await ResolveDisplayIdAsync(connection, transaction, project, boardId, existing.Id, patch.DisplayId, stamp is not null,
                ParseKeyNumber(existing.Key), cancellationToken);
        var updated = existing with
        {
            StoredDisplayId = displayId,
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
                    Assignee = $assignee, Priority = $priority, Type = $type, Points = $points, Tags = $tags, Blocked = $blocked, Flagged = $flagged, DisplayId = $displayId, UpdatedUTC = $updated
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
            command.Parameters.AddWithValue("$displayId", updated.DisplayId);
            command.Parameters.AddWithValue("$updated", ToDb(updated.UpdatedUtc));
            command.Parameters.AddWithValue("$id", updated.Id);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        if (patch.ClearBaseLlmOptions || patch.BaseLlmOptions is not null)
            await WriteBaseLlmOptionsAsync(connection, transaction, updated.Id, updated.BaseLlmOptions, cancellationToken);
        if (moving)
            await RenumberColumnAsync(connection, transaction, existing.ColumnId, cancellationToken);
        await PromoteCardAsync(connection, transaction, updated.Id, cancellationToken);
        await TransferCardLogAsync(connection, transaction, existing, updated, author, cancellationToken);
        await LogCardChangedAsync(connection, transaction, existing, updated, fromLaneName, toLaneName,
            author, cancellationToken, stamp);
        if (flagReason is not null)
            await InsertAttentionAsync(connection, transaction, updated.Id, author, flagReason, cancellationToken);
        await ReconcileMissingSyncedLaneAsync(connection, transaction, updated, stamp, cancellationToken);
        if (stamp is not null) await ReconcileSyncedDisplayIdAsync(connection, transaction, updated, patch.DisplayId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return updated;
    }

    /// <summary>
    /// Soft delete (VB-51): the card leaves every list, lookup and count, and its pending lane
    /// Automations are dropped, but the row, its log and its links stay for restore (VB-54).
    /// </summary>
    public Task<bool> DeleteCardAsync(string projectPath, string cardId, CancellationToken cancellationToken = default, BoardAuthor? author = null) =>
        DeleteCardCoreAsync(projectPath, cardId, author ?? BoardAuthor.User(), stamp: null, cancellationToken);

    private async Task<bool> DeleteCardCoreAsync(string projectPath, string cardId, BoardAuthor author, BoardSyncStamp? stamp, CancellationToken cancellationToken)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var existing = await ReadCardAsync(connection, transaction, project, cardId, cancellationToken, includeDeleted: stamp is not null);
        if (existing is null)
            return false;
        if (stamp is not null)
        {
            RequireSyncBoard(existing, stamp);
            if (await HasSyncStampAsync(connection, transaction, stamp, cancellationToken)) return true;
        }

        var now = stamp?.CreatedUtc ?? DateTime.UtcNow;
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = """
                UPDATE BoardCards SET DeletedUTC = $now, UpdatedUTC = $now WHERE Id = $id;
                DELETE FROM BoardPendingAutomations WHERE CardId = $id;
                DELETE FROM BoardPendingAdditionalAutomations WHERE CardId = $id;
                """;
            delete.Parameters.AddWithValue("$id", existing.Id);
            delete.Parameters.AddWithValue("$now", ToDb(now));
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }
        await LogCardDeletedAsync(connection, transaction, existing.Id, author, now, cancellationToken, stamp);
        await RenumberColumnAsync(connection, transaction, existing.ColumnId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public Task<BoardCardRecord?> MoveCardAsync(string projectPath, string cardId, string columnId, int? position, CancellationToken cancellationToken = default, BoardAuthor? author = null) =>
        MoveCardAsync(projectPath, cardId, columnId, position, skipLaneAutomations: false, cancellationToken, author);

    public async Task<BoardCardRecord?> MoveCardAsync(string projectPath, string cardId, string columnId, int? position, bool skipLaneAutomations, CancellationToken cancellationToken = default, BoardAuthor? author = null)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var moved = await MoveCardInTransactionAsync(connection, transaction, project, cardId, columnId, position, skipLaneAutomations, cancellationToken, author);
        await transaction.CommitAsync(cancellationToken);
        return moved;
    }

    private static async Task<BoardCardRecord?> MoveCardInTransactionAsync(SqliteConnection connection, SqliteTransaction transaction,
        string project, string cardId, string columnId, int? position, bool skipLaneAutomations,
        CancellationToken cancellationToken, BoardAuthor? author)
    {
        var existing = await ReadCardAsync(connection, transaction, project, cardId, cancellationToken);
        if (existing is null)
            return null;
        var column = await ReadColumnAsync(connection, transaction, project, columnId, cancellationToken)
            ?? throw new BoardValidationException($"Lane not found: {columnId}");
        await RequireSameSideOfSharingAsync(connection, transaction, existing.BoardId, column.BoardId, cancellationToken);

        var sourceIds = (await ReadColumnCardIdsAsync(connection, transaction, existing.ColumnId, cancellationToken))
            .Where(id => id != existing.Id).ToList();
        var sameColumn = string.Equals(column.Id, existing.ColumnId, StringComparison.Ordinal);
        var targetIds = sameColumn
            ? sourceIds
            : (await ReadColumnCardIdsAsync(connection, transaction, column.Id, cancellationToken)).Where(id => id != existing.Id).ToList();
        var index = Math.Clamp(position ?? 0, 0, targetIds.Count);
        targetIds.Insert(index, existing.Id);

        var nowUtc = DateTime.UtcNow;
        await using (var move = connection.CreateCommand())
        {
            move.Transaction = transaction;
            move.CommandText = "UPDATE BoardCards SET ColumnId = $column, UpdatedUTC = $updated WHERE Id = $id;";
            move.Parameters.AddWithValue("$column", column.Id);
            move.Parameters.AddWithValue("$updated", ToDb(nowUtc));
            move.Parameters.AddWithValue("$id", existing.Id);
            await move.ExecuteNonQueryAsync(cancellationToken);
        }
        await TransferCardLogAsync(connection, transaction, existing, existing with { ColumnId = column.Id, BoardId = column.BoardId, UpdatedUtc = nowUtc }, author ?? BoardAuthor.User(), cancellationToken);
        // A reorder within the lane is not history; only a lane change is logged.
        if (!sameColumn && await ReadColumnAsync(connection, transaction, project, existing.ColumnId, cancellationToken) is { } source)
            await LogCardMovedAsync(connection, transaction, existing.Id, source, column, author ?? BoardAuthor.User(), nowUtc, cancellationToken);
        if (skipLaneAutomations && !sameColumn)
        {
            // The lane-entry triggers above have just replaced this card's pending entries with
            // the destination lane's. Removing them here, in the same transaction, is the
            // caller's explicit "move without firing" (VB-34); the settings and the move are
            // untouched, and a later entry into the lane records fresh entries as usual.
            await using var skip = connection.CreateCommand();
            skip.Transaction = transaction;
            skip.CommandText = """
                INSERT INTO BoardLaneAutomationDispatch (EventKey, JobId, CardId, ColumnId, DueUnixMs, Status, Reason)
                SELECT EventKey, JobId, CardId, ColumnId, DueUnixMs, 'Skipped', 'Lane Automations skipped at the caller''s request.'
                FROM BoardPendingAutomations WHERE CardId = $id
                UNION ALL
                SELECT EventKey, JobId, CardId, ColumnId, DueUnixMs, 'Skipped', 'Lane Automations skipped at the caller''s request.'
                FROM BoardPendingAdditionalAutomations WHERE CardId = $id;
                DELETE FROM BoardPendingAutomations WHERE CardId = $id;
                DELETE FROM BoardPendingAdditionalAutomations WHERE CardId = $id;
                """;
            skip.Parameters.AddWithValue("$id", existing.Id);
            await skip.ExecuteNonQueryAsync(cancellationToken);
        }
        await WriteCardPositionsAsync(connection, transaction, targetIds, cancellationToken);
        if (!sameColumn)
            await WriteCardPositionsAsync(connection, transaction, sourceIds, cancellationToken);
        return await ReadCardAsync(connection, transaction, project, existing.Id, cancellationToken);
    }

    // ------------------------------------------------------------------ comments

    /// <summary>Reads the agent identity without constructing the dashboard's Repository in stdio hosts.</summary>
    public async Task<BoardAuthor?> FindSessionAuthorAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenStateAsync(cancellationToken);
        // Terminal history stays in state.db; a fresh stdio host may not yet have Sessions.
        if (await _stateFeatures.HasTableAsync(connection, "Sessions", cancellationToken))
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
        linked.CommandText = $"SELECT DisplayName, Cli FROM {AllSessionsSql} WHERE SessionId = $session ORDER BY LinkOrder, CreatedUTC, CardId LIMIT 1;";
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
        => AddCommentAsync(projectPath, cardId, author, body, cancellationToken);

    private async Task<BoardCommentRecord?> InsertCommentRowAsync(string projectPath, string cardId, BoardAuthor author, string body, string kind, CancellationToken cancellationToken, BoardSyncStamp? stamp = null)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var card = await ReadCardAsync(connection, transaction, project, cardId, cancellationToken, includeDeleted: stamp is not null);
        if (card is null)
            return null;
        if (stamp is not null) RequireSyncBoard(card, stamp);

        // Notes use their own id prefix so an agent can tell the two apart in tool output. A pulled
        // web comment keeps its remote id and time and is already marked sent (VB-51).
        var comment = new BoardCommentRecord(stamp?.EntryId ?? NewId(kind == BoardCommentKinds.Note ? "note" : "cm"), card.Id, author, body, stamp?.CreatedUtc ?? DateTime.UtcNow, kind,
            stamp is null ? BoardCommentPurpose.Changes(author) : stamp.Changes);
        if (stamp is not null && await HasSyncStampAsync(connection, transaction, stamp, cancellationToken)) return comment;
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO BoardComments (Id, CardId, AuthorKind, AuthorLabel, AuthorCli, SessionId, Body, CreatedUTC, Kind, RemoteSeq, Changes)
                VALUES ($id, $card, $kind, $label, $cli, $session, $body, $created, $rowKind, $remoteSeq, $changes);
                """;
            insert.Parameters.AddWithValue("$remoteSeq", stamp is null ? DBNull.Value : stamp.RemoteSeq);
            insert.Parameters.AddWithValue("$changes", (object?)comment.Changes ?? DBNull.Value);
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
        if (stamp is not null) await ForgetSkippedEntryAsync(connection, transaction, stamp, cancellationToken);
        if (!await IsDeletedAsync(connection, transaction, card.Id, cancellationToken))
            await TouchCardAsync(connection, transaction, card.Id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return comment;
    }

    public async Task<IReadOnlyList<BoardCommentRecord>> GetNotesAsync(string projectPath, string idOrKey, CancellationToken cancellationToken = default)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        var card = await ReadCardAsync(connection, null, project, idOrKey, cancellationToken);
        return card is null ? [] : await ReadCommentsAsync(connection, card.Id, cancellationToken);
    }

    /// <summary>
    /// Same "the table may not exist in this host" discipline as <see cref="FindSessionAuthorAsync"/>:
    /// a board-only database (fresh stdio host) has neither Sessions nor ChatSummary.
    /// </summary>
    public async Task<BoardSessionOutcomeRecord?> FindSessionOutcomeAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenStateAsync(cancellationToken);
        // Fixtures (and very old files) carry a Sessions table without these columns.
        if (!await _stateFeatures.HasTableAsync(connection, "Sessions", cancellationToken)
            || !_stateFeatures.HasColumn(connection, "Sessions", "EndedUTC")
            || !_stateFeatures.HasColumn(connection, "Sessions", "ExitCode"))
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
        if (await _stateFeatures.HasTableAsync(connection, "ChatSummary", cancellationToken))
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

        var hasLinks = false;
        await using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = $"SELECT s.CardId, c.ProjectPath FROM {AllSessionsSql} s JOIN BoardCards c ON c.Id = s.CardId WHERE s.SessionId = $session;";
            existing.Parameters.AddWithValue("$session", sessionId);
            await using var reader = await existing.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                hasLinks = true;
                if (!string.Equals(reader.GetString(1), project, BoardPaths.ProjectPathComparison))
                    throw new BoardConflictException("That session is linked to a card in another project.");
                if (reader.GetString(0) == card.Id)
                    throw new BoardConflictException("That session is already on this card.");
            }
        }

        var record = new BoardSessionRecord(sessionId, card.Id, tabId, selection, cli, displayName, origin, DateTime.UtcNow);
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            // Keep the original link in the legacy table. Additional links do not replace it,
            // and survive its removal; no data rewrite or historical backfill is needed.
            var table = hasLinks ? "BoardAdditionalCardSessions" : "BoardCardSessions";
            insert.CommandText = $"""
                INSERT INTO {table} (SessionId, CardId, TabId, Selection, Cli, DisplayName, Origin, CreatedUTC)
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
            update.CommandText = """
                UPDATE BoardCardSessions SET DisplayName = $name WHERE SessionId = $session AND CardId = $card;
                UPDATE BoardAdditionalCardSessions SET DisplayName = $name WHERE SessionId = $session AND CardId = $card;
                """;
            update.Parameters.AddWithValue("$name", displayName);
            update.Parameters.AddWithValue("$session", sessionId);
            update.Parameters.AddWithValue("$card", card.Id);
            if (await update.ExecuteNonQueryAsync(cancellationToken) == 0)
                return null;
        }
        await TouchCardAsync(connection, transaction, card.Id, cancellationToken);
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
            delete.CommandText = """
                DELETE FROM BoardCardSessions WHERE SessionId = $session AND CardId = $card;
                DELETE FROM BoardAdditionalCardSessions WHERE SessionId = $session AND CardId = $card;
                """;
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
        command.CommandText = $"""
            SELECT s.SessionId, s.CardId, c.ProjectPath
            FROM {AllSessionsSql} s JOIN BoardCards c ON c.Id = s.CardId
            WHERE s.SessionId = $session AND c.DeletedUTC IS NULL ORDER BY s.LinkOrder, s.CreatedUTC, s.CardId LIMIT 1;
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
        command.CommandText = SessionSelectSql + $" JOIN BoardCards c ON c.Id = s.CardId WHERE c.ProjectPath = $project{ProjectPathCollation} AND c.DeletedUTC IS NULL ORDER BY s.CreatedUTC;";
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

    public async Task<BoardCommitRecord?> AddCommitAsync(string projectPath, string cardId, string sha, string author, string message, DateTime committedUtc, SandboxDiffResponse snapshot, CancellationToken cancellationToken = default, string? sessionId = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var snapshotJson = JsonSerializer.Serialize(snapshot, StorageJsonSerializerContext.Default.SandboxDiffResponse);
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var card = await ReadCardAsync(connection, transaction, project, cardId, cancellationToken);
        if (card is null)
            return null;

        // Membership is read under the same writer transaction as every link/snapshot. A
        // concurrent attach either precedes this entire operation or follows it.
        var targets = await ReadCommitTargetCardsAsync(connection, transaction, project, card.Id, sessionId, cancellationToken);
        var linkedUtc = DateTime.UtcNow;
        foreach (var target in targets)
            await WriteCommitAsync(connection, transaction,
                new BoardCommitRecord(target, sha, author, message, committedUtc, linkedUtc), snapshotJson,
                allowExisting: !string.IsNullOrWhiteSpace(sessionId), cancellationToken);
        var record = (await ReadCommitsAsync(connection, card.Id, cancellationToken, transaction)).Single(c => c.Sha == sha);
        await transaction.CommitAsync(cancellationToken);
        return record;
    }

    private static async Task WriteCommitAsync(SqliteConnection connection, SqliteTransaction transaction,
        BoardCommitRecord record, string snapshotJson, bool allowExisting, CancellationToken cancellationToken)
    {
        bool inserted;
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO BoardCommits (CardId, Sha, Author, Message, CommittedUTC, LinkedUTC)
                VALUES ($card, $sha, $author, $message, $committed, $linked)
                ON CONFLICT(CardId, Sha) DO NOTHING;
                """;
            insert.Parameters.AddWithValue("$card", record.CardId);
            insert.Parameters.AddWithValue("$sha", record.Sha);
            insert.Parameters.AddWithValue("$author", record.Author);
            insert.Parameters.AddWithValue("$message", record.Message);
            insert.Parameters.AddWithValue("$committed", ToDb(record.CommittedUtc));
            insert.Parameters.AddWithValue("$linked", ToDb(record.LinkedUtc));
            inserted = await insert.ExecuteNonQueryAsync(cancellationToken) > 0;
        }
        if (!inserted && !allowExisting)
            throw new BoardConflictException($"{record.ShortSha} is already on this card.");
        bool captured;
        await using (var capture = connection.CreateCommand())
        {
            capture.Transaction = transaction;
            capture.CommandText = "INSERT INTO BoardCommitSnapshots (CardId, Sha, SnapshotJson) VALUES ($card, $sha, $snapshot) ON CONFLICT(CardId, Sha) DO NOTHING;";
            capture.Parameters.AddWithValue("$card", record.CardId);
            capture.Parameters.AddWithValue("$sha", record.Sha);
            capture.Parameters.AddWithValue("$snapshot", snapshotJson);
            captured = await capture.ExecuteNonQueryAsync(cancellationToken) > 0;
        }
        if (inserted || captured)
            await TouchCardAsync(connection, transaction, record.CardId, cancellationToken);
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
        "SELECT Id, ProjectPath, Name, Position, CreatedUTC, UpdatedUTC, DisplayPrefix, SyncEnabled FROM Boards";

    // A property because the prefix join interpolates ProjectPathCollation (see CardSequenceReseedSql).
    private static string CardSelectSql => $"""
        SELECT c.Id, c.ProjectPath, c.Number, c.ColumnId, c.Position, c.Title, c.Description, c.Assignee, c.Priority,
               c.Points, c.Tags, c.Blocked, c.CreatedUTC, c.UpdatedUTC,
               (SELECT COUNT(*) FROM BoardComments m WHERE m.CardId = c.Id AND m.Kind IN ('comment', 'note') AND m.DiscussionHidden = 0 AND NOT EXISTS (SELECT 1 FROM BoardDeletedComments d WHERE d.CommentId = m.Id)) AS CommentCount,
               (SELECT o.OptionsJson FROM BoardCardOptions o WHERE o.CardId = c.Id),
               c.Type,
               (SELECT k.BoardId FROM BoardColumns k WHERE k.Id = c.ColumnId), c.Flagged,
               {CardPrefixSql}, c.CardKey, c.DisplayId, c.AgentMade, c.AgentMadeBy, c.AgentMadeSessionId,
               (SELECT j.IssueKey FROM BoardJiraLinks j WHERE j.CardId = c.Id ORDER BY j.LastPulledUTC DESC LIMIT 1)
        FROM BoardCards c
        {CardPrefixJoinSql}
        """;

    private const string SessionSelectSql =
        "SELECT s.SessionId, s.CardId, s.TabId, s.Selection, s.Cli, s.DisplayName, s.Origin, s.CreatedUTC FROM " + AllSessionsSql + " s";

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
        ParseDb(reader.GetString(5)),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.GetInt32(7) != 0);

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

    /// <summary>
    /// A card by stored key (<c>VB-A7K2P-53</c>, any case), short key (<c>VB-53</c>), display label
    /// or id, in that order of precedence: a key never moves, so the card it names must win over a
    /// label another card was given in the same spelling. 10.11.2 restarted labels at 1 after the
    /// upgrade, so a project whose label prefix is its key prefix can still hold <c>FRON-XXXXX-1</c>
    /// beside a later card labelled <c>FRON-1</c>; that label was never rewritten (no data conversion)
    /// and must not capture the older card's key (VIBE-2 review). Newer labels never spell a key.
    /// Soft-deleted cards are not found unless <paramref name="includeDeleted"/>.
    /// </summary>
    private static async Task<BoardCardRecord?> ReadCardAsync(SqliteConnection connection, SqliteTransaction? transaction, string project, string idOrKey, CancellationToken cancellationToken, bool includeDeleted = false)
    {
        var input = idOrKey.Trim();
        var live = includeDeleted ? "" : " AND c.DeletedUTC IS NULL";
        var scope = $" WHERE c.ProjectPath = $project{ProjectPathCollation}";
        if (input.StartsWith("card_", StringComparison.Ordinal))
            return await ReadOneCardAsync(connection, transaction, CardSelectSql + scope + $" AND c.Id = $input{live} LIMIT 1;", [("$project", project), ("$input", input)], cancellationToken);

        // 1. The immutable key itself.
        var byKey = await ReadOneCardAsync(connection, transaction,
            CardSelectSql + scope + $" AND {CardKeySql} = $input COLLATE NOCASE{live} LIMIT 1;",
            [("$project", project), ("$input", input)], cancellationToken);
        if (byKey is not null) return byKey;

        // 2. A short key, PREFIX-n: the card whose immutable key answers to it. The project's own
        // prefix, or the VB an older binary shows for the same number; another project's prefix is
        // not found rather than silently resolved to this project's number. A stored-key card is
        // found by its number because PREFIX-n is the short form of PREFIX-RRRRR-n. A full stored
        // key had its only chance above.
        if (!BoardKeys.TryParseStored(input, out _) && BoardKeys.TryParse(input, out var prefix, out var number))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            // The exact legacy row (CardKey NULL) sorts first: PREFIX-n is the only key it has.
            command.CommandText = CardSelectSql + scope + $" AND {ShortKeyMatchSql}{live} ORDER BY (c.CardKey IS NULL) DESC LIMIT 2;";
            command.Parameters.AddWithValue("$project", project);
            command.Parameters.AddWithValue("$number", number);
            command.Parameters.AddWithValue("$prefix", prefix);
            command.Parameters.AddWithValue("$short", BoardKeys.Format(prefix, number));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var found = ReadCard(reader);
                // A legacy card (no stored key) wins over a synced card whose stored key merely ends in
                // the same number, because PREFIX-n is the only key the legacy card has; two stored-key
                // cards on one number both have full keys to use.
                if (found.StoredKey is not null && await reader.ReadAsync(cancellationToken))
                    throw new BoardValidationException("That short card key matches more than one card. Use the full card key.");
                return found;
            }
        }

        // 3. The display label, or the key an older writer's row shows in its place.
        var byLabel = await ReadOneCardAsync(connection, transaction,
            CardSelectSql + scope + $" AND {CardDisplayIdSql} = $input COLLATE NOCASE{live} LIMIT 1;",
            [("$project", project), ("$input", input)], cancellationToken);
        if (byLabel is not null || BoardKeys.TryParse(input, out _, out _)) return byLabel;

        // 4. An id without the card_ prefix.
        return await ReadOneCardAsync(connection, transaction, CardSelectSql + scope + $" AND c.Id = $id{live} LIMIT 1;", [("$project", project), ("$id", input)], cancellationToken);
    }

    private static async Task<BoardCardRecord?> ReadOneCardAsync(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        (string Name, object Value)[] parameters, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
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
        Flagged: reader.GetInt32(18) != 0,
        KeyPrefix: reader.GetString(19),
        StoredKey: reader.IsDBNull(20) ? null : reader.GetString(20),
        StoredDisplayId: reader.IsDBNull(21) ? null : reader.GetString(21),
        AgentMade: !reader.IsDBNull(22) && reader.GetInt32(22) != 0,
        AgentMadeBy: reader.IsDBNull(23) ? null : reader.GetString(23),
        AgentMadeSessionId: reader.IsDBNull(24) ? null : reader.GetString(24),
        JiraIssueKey: reader.IsDBNull(25) ? null : reader.GetString(25));

    private static async Task<IReadOnlyList<BoardCommentRecord>> ReadCommentsAsync(SqliteConnection connection, string cardId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, CardId, AuthorKind, AuthorLabel, AuthorCli, SessionId, Body, CreatedUTC, Kind, Changes
            FROM BoardComments WHERE CardId = $card AND Kind IN ('comment', 'note') AND DiscussionHidden = 0
              AND NOT EXISTS (SELECT 1 FROM BoardDeletedComments d WHERE d.CommentId = BoardComments.Id) ORDER BY CreatedUTC, Id;
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
                ParseDb(reader.GetString(7)),
                reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetString(9)));
        }
        return comments;
    }

    private static async Task<IReadOnlyList<BoardSessionRecord>> ReadSessionsAsync(SqliteConnection connection, string cardId, CancellationToken cancellationToken, int limit = int.MaxValue, bool newestFirst = false)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = SessionSelectSql + " WHERE s.CardId = $card ORDER BY s.CreatedUTC" + (newestFirst ? " DESC" : "") + ", s.SessionId LIMIT $limit;";
        command.Parameters.AddWithValue("$card", cardId);
        command.Parameters.AddWithValue("$limit", limit);
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

    private static async Task<IReadOnlyList<BoardCommitRecord>> ReadCommitsAsync(SqliteConnection connection, string cardId, CancellationToken cancellationToken, SqliteTransaction? transaction = null, int limit = int.MaxValue)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT CardId, Sha, Author, Message, CommittedUTC, LinkedUTC FROM BoardCommits WHERE CardId = $card ORDER BY CommittedUTC DESC, Sha LIMIT $limit;";
        command.Parameters.AddWithValue("$card", cardId);
        command.Parameters.AddWithValue("$limit", limit);
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

    private static async Task<BoardRecord> InsertBoardWithDefaultLanesAsync(SqliteConnection connection, SqliteTransaction transaction, string project, string name, int position, CancellationToken cancellationToken, string? displayPrefix = null, bool createDefaultLanes = true)
    {
        var now = DateTime.UtcNow;
        var board = new BoardRecord(NewId("brd"), project, name, position, now, now, displayPrefix, SyncEnabled: false);
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO Boards (Id, ProjectPath, Name, Position, CreatedUTC, UpdatedUTC, DisplayPrefix, SyncEnabled)
                VALUES ($id, $project, $name, $position, $created, $updated, $prefix, 0);
                """;
            insert.Parameters.AddWithValue("$id", board.Id);
            insert.Parameters.AddWithValue("$project", project);
            insert.Parameters.AddWithValue("$name", name);
            insert.Parameters.AddWithValue("$prefix", (object?)displayPrefix ?? DBNull.Value);
            insert.Parameters.AddWithValue("$position", position);
            insert.Parameters.AddWithValue("$created", ToDb(now));
            insert.Parameters.AddWithValue("$updated", ToDb(now));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        if (!createDefaultLanes) return board;
        var lanePosition = 0;
        foreach (var (laneName, color, recipeId) in DefaultLaneTemplate)
        {
            var columnId = NewId("col");
            await InsertColumnAsync(connection, transaction, columnId, project, board.Id, laneName, lanePosition++, color, now, cancellationToken);
            // This is a template role, captured once at creation, never a runtime lane-name lookup.
            if (recipeId is null) continue;
            await using var seed = connection.CreateCommand();
            seed.Transaction = transaction;
            seed.CommandText = "INSERT INTO BoardStarterWorkflows (ColumnId, RecipeId) VALUES ($column, $recipe);";
            seed.Parameters.AddWithValue("$column", columnId);
            seed.Parameters.AddWithValue("$recipe", recipeId);
            await seed.ExecuteNonQueryAsync(cancellationToken);
        }
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
        command.CommandText = "SELECT Id FROM BoardCards WHERE ColumnId = $column AND DeletedUTC IS NULL ORDER BY Flagged DESC, Position, Number;";
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
        // Attention always wins over activity and explicit drag positions. Preserve the requested
        // order within each group, and keep dense positions consistent with the read projection.
        var groupedIds = new List<string>(orderedIds.Count);
        await using (var order = connection.CreateCommand())
        {
            order.Transaction = transaction;
            order.CommandText = """
                SELECT c.Id FROM json_each($ids) requested
                JOIN BoardCards c ON c.Id = requested.value
                ORDER BY c.Flagged DESC, CAST(requested.key AS INTEGER);
                """;
            order.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(orderedIds.ToList(), StorageJsonSerializerContext.Default.ListString));
            await using var reader = await order.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) groupedIds.Add(reader.GetString(0));
        }
        orderedIds = groupedIds;
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
        await PromoteCardAsync(connection, transaction, cardId, cancellationToken);
    }

    // Card activity belongs at the top of its attention group, while explicit drag positions use
    // WriteCardPositionsAsync directly. Only the touched card's timestamp changes.
    private static async Task PromoteCardAsync(SqliteConnection connection, SqliteTransaction transaction, string cardId, CancellationToken cancellationToken)
    {
        await using var column = connection.CreateCommand();
        column.Transaction = transaction;
        column.CommandText = "SELECT ColumnId FROM BoardCards WHERE Id = $id;";
        column.Parameters.AddWithValue("$id", cardId);
        if (await column.ExecuteScalarAsync(cancellationToken) is not string columnId)
            return;
        var ids = await ReadColumnCardIdsAsync(connection, transaction, columnId, cancellationToken);
        ids.Remove(cardId);
        ids.Insert(0, cardId);
        await WriteCardPositionsAsync(connection, transaction, ids, cancellationToken);
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

    private static async Task<string?> ScalarStringAsync(SqliteConnection connection, SqliteTransaction? transaction, string sql, (string Name, object Value) parameter, CancellationToken cancellationToken, params (string Name, object Value)[] more)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        foreach (var (name, value) in more) command.Parameters.AddWithValue(name, value);
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
        SqliteMigrationRunner.Apply(connection, "board-review-settings", 1, MigrationKind.Additive, (db, transaction) =>
            SqliteSchema.Execute(db, transaction, ReviewSettingsSchemaSql));
        SqliteMigrationRunner.Apply(connection, "board-reviews", 1, MigrationKind.Additive, (db, transaction) =>
            SqliteSchema.Execute(db, transaction, ReviewsSchemaSql));
        SqliteMigrationRunner.Apply(connection, "board-recall", 1, MigrationKind.Additive, (db, transaction) =>
            SqliteSchema.Execute(db, transaction, RecallSchemaSql));
        SqliteMigrationRunner.Apply(connection, "board-search", 1, MigrationKind.Additive, (db, transaction) =>
            SqliteSchema.Execute(db, transaction, SearchSchemaSql));
        SqliteMigrationRunner.Apply(connection, "board-agent-completion", 1, MigrationKind.Additive, (db, transaction) =>
            SqliteSchema.Execute(db, transaction, AgentCompletionSchemaSql));
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
        SqliteMigrationRunner.Apply(connection, "board", 10, MigrationKind.Additive, (db, transaction) =>
            SqliteSchema.Execute(db, transaction, AdditionalCardSessionsSchemaSql));
        // board/11: per-project card key prefixes; existing projects are seeded with VB (see
        // BoardStore.ProjectKeys.cs), so no key changes on upgrade.
        SqliteMigrationRunner.Apply(connection, "board", 11, MigrationKind.Additive, ApplyProjectKeysMigration);
        // board/12: one Jira Cloud connection per board and the issue-link table. Additive, no
        // backfill; an older binary ignores both tables.
        SqliteMigrationRunner.Apply(connection, "board", 12, MigrationKind.Additive, (db, transaction) =>
            SqliteSchema.Execute(db, transaction, JiraSchemaSql));
        // board/13: a deleted board takes its Jira connection with it. Additive (a trigger); existing
        // rows are not cleaned up, the scheduler just never pulls a connection whose board is gone.
        SqliteMigrationRunner.Apply(connection, "board", 13, MigrationKind.Additive, (db, transaction) =>
            SqliteSchema.Execute(db, transaction, JiraBoardDeleteTriggerSql));
        // board/14 (VB-51): the Card Log (change rows in BoardComments), stored random card keys,
        // soft-deleted cards and the viberails.ai sync links (BoardStore.CardLog.cs). Additive, no
        // backfill; an older binary ignores the columns and reads comment/note rows only.
        SqliteMigrationRunner.Apply(connection, "board", 14, MigrationKind.Additive, (db, transaction) =>
        {
            SqliteSchema.AdoptStatement(db, transaction, "ALTER TABLE BoardCards ADD COLUMN CardKey TEXT");
            SqliteSchema.AdoptStatement(db, transaction, "ALTER TABLE BoardCards ADD COLUMN DeletedUTC TEXT");
            SqliteSchema.AdoptStatement(db, transaction, "ALTER TABLE BoardComments ADD COLUMN Changes TEXT");
            SqliteSchema.AdoptStatement(db, transaction, "ALTER TABLE BoardComments ADD COLUMN RemoteSeq INTEGER");
            SqliteSchema.Execute(db, transaction, CardLogSchemaSql);
        });
        SqliteMigrationRunner.Apply(connection, "board", 15, MigrationKind.Additive, (db, transaction) =>
            SqliteSchema.Execute(db, transaction, BoardHistorySchemaSql));
        SqliteMigrationRunner.Apply(connection, "board", 16, MigrationKind.Additive, (db, transaction) =>
            SqliteSchema.AdoptStatement(db, transaction, "ALTER TABLE BoardSyncLinks ADD COLUMN DestinationKey TEXT"));
        SqliteMigrationRunner.Apply(connection, "board", 17, MigrationKind.Additive, (db, transaction) =>
            SqliteSchema.Execute(db, transaction, RejectedFieldsSchemaSql));
        // board/18: the board/15 history triggers recreated NULL-safe (BoardHistory.BoardId is NOT
        // NULL, but a lane's BoardId is NULL until adoption). Drop-and-recreate from the corrected
        // constant; no row is read, rewritten or deleted.
        SqliteMigrationRunner.Apply(connection, "board", 18, MigrationKind.Additive, (db, transaction) =>
            SqliteSchema.Execute(db, transaction, BoardHistoryTriggerResetSql + BoardHistorySchemaSql));
        // board/19 (VB-63): agent-context samples, one row per card launch (BoardStore.ContextSamples.cs).
        // Additive; an older binary ignores the table and the `context` change entries it pairs with.
        SqliteMigrationRunner.Apply(connection, "board", 19, MigrationKind.Additive, (db, transaction) =>
            SqliteSchema.Execute(db, transaction, ContextSamplesSchemaSql));
        SqliteMigrationRunner.Apply(connection, "board", 20, MigrationKind.Additive, ApplyDisplayIdsMigration);
        // board/21: pulled sync entries this version cannot apply, recorded so the cursor moves on and
        // the status view counts them (BoardStore.Sync.cs). Additive; an older binary ignores the table.
        SqliteMigrationRunner.Apply(connection, "board", 21, MigrationKind.Additive, (db, transaction) =>
            SqliteSchema.Execute(db, transaction, SkippedEntriesSchemaSql));
        // board/22: the desktop version that last tried each skipped entry, so the first sync of a newer
        // version tries it again (BoardStore.Sync.cs). Additive and nullable: a row from before it counts
        // as tried by an earlier version, and an older binary never names the column.
        SqliteMigrationRunner.Apply(connection, "board", 22, MigrationKind.Additive, (db, transaction) =>
            SqliteSchema.AdoptStatement(db, transaction, "ALTER TABLE BoardSyncSkippedEntries ADD COLUMN Version TEXT"));
        // Existing publication consent covered card text only. Activity remains off until the
        // owner enables it through the updated publish dialog; the cursor survives root restarts.
        SqliteMigrationRunner.Apply(connection, "board", 23, MigrationKind.Additive, (db, transaction) =>
        {
            SqliteSchema.AdoptStatement(db, transaction, "ALTER TABLE BoardSyncLinks ADD COLUMN ActivitySchema INTEGER NOT NULL DEFAULT 0");
            SqliteSchema.AdoptStatement(db, transaction, "ALTER TABLE BoardSyncLinks ADD COLUMN ActivityAfter TEXT");
        });
        // board/24 (VIBE-11): a card created by an agent through MCP is marked once, at insert.
        // Additive and default 0, so every card an older binary or the board UI created stays
        // human-made, and an older binary never names the column.
        SqliteMigrationRunner.Apply(connection, "board", 24, MigrationKind.Additive, (db, transaction) =>
            SqliteSchema.AdoptStatement(db, transaction, "ALTER TABLE BoardCards ADD COLUMN AgentMade INTEGER NOT NULL DEFAULT 0"));
        SqliteMigrationRunner.Apply(connection, "board", 25, MigrationKind.Additive, (db, transaction) =>
        {
            SqliteSchema.Execute(db, transaction, CardActionsSchemaSql);
            SqliteSchema.AdoptStatement(db, transaction, "ALTER TABLE BoardComments ADD COLUMN SyncBoardId TEXT");
            SqliteSchema.AdoptStatement(db, transaction, "ALTER TABLE BoardComments ADD COLUMN DiscussionHidden INTEGER NOT NULL DEFAULT 0");
            SqliteSchema.AdoptStatement(db, transaction, "ALTER TABLE BoardComments ADD COLUMN TransferRemoteSeq INTEGER");
            SqliteSchema.Execute(db, transaction, TransferDeliverySchemaSql);
        });
        SqliteMigrationRunner.Apply(connection, "board", 26, MigrationKind.Additive, (db, transaction) =>
            SqliteSchema.Execute(db, transaction, SharedOriginsSchemaSql));
        // board/27 (VIBE-96): the agent that made an AgentMade card and its VibeRails session, written
        // once at insert beside AgentMade. Additive and nullable with no backfill: earlier agent cards
        // keep only the mark, and an older binary never names the columns.
        SqliteMigrationRunner.Apply(connection, "board", 27, MigrationKind.Additive, (db, transaction) =>
        {
            SqliteSchema.AdoptStatement(db, transaction, "ALTER TABLE BoardCards ADD COLUMN AgentMadeBy TEXT");
            SqliteSchema.AdoptStatement(db, transaction, "ALTER TABLE BoardCards ADD COLUMN AgentMadeSessionId TEXT");
        });
        // board/28 (VIBE-102): Jira connections made from a board link (BoardStore.Jira.cs). Additive and
        // nullable with no backfill; an older binary's upsert never names the columns, so it keeps them.
        SqliteMigrationRunner.Apply(connection, "board", 28, MigrationKind.Additive, (db, transaction) =>
        {
            foreach (var statement in JiraBoardLinkColumnsSql)
                SqliteSchema.AdoptStatement(db, transaction, statement);
        });
        // board/29 (VIBE-102): BoardJiraLinks.Mapping, so a changed lane map or points field re-applies
        // unchanged issues. Additive and nullable; an older binary's link writes never name it.
        SqliteMigrationRunner.Apply(connection, "board", 29, MigrationKind.Additive, (db, transaction) =>
            SqliteSchema.AdoptStatement(db, transaction, JiraLinkMappingColumnSql));
        // board/30: marks a connection whose BoardId is already its dedicated Jira board.
        // Nullable and no startup backfill; ordinary Connect/pull performs the requested separation.
        SqliteMigrationRunner.Apply(connection, "board", 30, MigrationKind.Additive, (db, transaction) =>
            SqliteSchema.AdoptStatement(db, transaction, "ALTER TABLE BoardJiraConnections ADD COLUMN DedicatedBoard INTEGER"));
        // Existing boards keep their automatic publication; newly created boards require opt-in.
        SqliteMigrationRunner.Apply(connection, "board", 31, MigrationKind.Additive, (db, transaction) =>
            SqliteSchema.AdoptStatement(db, transaction, "ALTER TABLE Boards ADD COLUMN SyncEnabled INTEGER NOT NULL DEFAULT 1"));
        SqliteMigrationRunner.Apply(connection, "board-attention", 1, MigrationKind.Additive, (db, transaction) =>
            SqliteSchema.Execute(db, transaction, AttentionSchemaSql));
        SqliteMigrationRunner.Apply(connection, "board-lane-dispatch", 1, MigrationKind.Additive, ApplyLaneDispatchSchema);
        SqliteMigrationRunner.Apply(connection, "board-checks", 1, MigrationKind.Additive, (db, transaction) =>
            SqliteSchema.Execute(db, transaction, ChecksSchemaSql));
        SqliteMigrationRunner.Apply(connection, "board-starter-workflows", 1, MigrationKind.Additive, (db, transaction) =>
            SqliteSchema.Execute(db, transaction, StarterWorkflowSchemaSql));
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
            repair.CommandText = CardSequenceReseedSql;
            repair.ExecuteNonQuery();
        }
        AdoptOrphanLanes(connection, transaction);
        transaction.Commit();
    }

    /// <summary>
    /// Lanes without a board (pre-board/4 rows, or rows an older binary added) join their
    /// project's first board. A project that has none gets one, named after the repository
    /// folder rather than "Main". Board ids follow the same <c>brd_</c> + 12 hex shape as NewId.
    /// The custom project name is not available here: this runs inside schema setup.
    /// </summary>
    private static void AdoptOrphanLanes(SqliteConnection connection, SqliteTransaction transaction)
    {
        var projects = new List<string>();
        using (var orphans = connection.CreateCommand())
        {
            orphans.Transaction = transaction;
            orphans.CommandText = $"""
                SELECT MIN(ProjectPath) FROM BoardColumns
                WHERE BoardId IS NULL
                  AND NOT EXISTS (SELECT 1 FROM Boards b WHERE b.ProjectPath = BoardColumns.ProjectPath{ProjectPathCollation})
                GROUP BY ProjectPath{ProjectPathCollation};
                """;
            using var reader = orphans.ExecuteReader();
            while (reader.Read())
                projects.Add(reader.GetString(0));
        }

        var now = ToDb(DateTime.UtcNow);
        foreach (var project in projects)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO Boards (Id, ProjectPath, Name, Position, CreatedUTC, UpdatedUTC)
                VALUES ($id, $project, $name, 0, $now, $now);
                """;
            insert.Parameters.AddWithValue("$id", NewId("brd"));
            insert.Parameters.AddWithValue("$project", project);
            insert.Parameters.AddWithValue("$name", ChooseDefaultBoardName(null, Path.GetFileName(project)));
            insert.Parameters.AddWithValue("$now", now);
            insert.ExecuteNonQuery();
        }

        using var adopt = connection.CreateCommand();
        adopt.Transaction = transaction;
        adopt.CommandText = $"""
            UPDATE BoardColumns SET BoardId = (
                SELECT b.Id FROM Boards b WHERE b.ProjectPath = BoardColumns.ProjectPath{ProjectPathCollation}
                ORDER BY b.Position, b.CreatedUTC LIMIT 1)
            WHERE BoardId IS NULL;
            """;
        adopt.ExecuteNonQuery();
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

    private static readonly IReadOnlyList<(string Name, string Color, string? RecipeId)> DefaultLaneTemplate =
    [
        ("Backlog", "#64748b", null),
        ("Ready", "#3b82f6", null),
        ("Build", "#06b6d4", null),
        ("Review", "#f59e0b", VibeRails.DTOs.BoardReviewDefaults.RecipeId),
        ("Done", "#10b981", null)
    ];

    /// <summary>The lanes offered for first and additional local boards.</summary>
    public static readonly IReadOnlyList<(string Name, string Color)> DefaultLanes =
        DefaultLaneTemplate.Select(lane => (lane.Name, lane.Color)).ToArray();

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
