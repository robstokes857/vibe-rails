using System.Data;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VibeRails.Data.Sqlite;
using VibeRails.DTOs;

namespace VibeRails.Services.Board;

/// <summary>
/// The board manager (VIBE-52 follow-up, VIBE-204): one manager per board, its launches, and the
/// reads a manager lives on, the board's event stream and its Agent cards. Everything here is
/// additive: <c>board-agent-workflow/2</c> added the two tables (already applied on machines that
/// ran the first build, so its SQL never changes), and <c>board-agent-workflow/3</c> the machine-wide
/// switch plus the manager's standing orders and worker limit. An older binary ignores all of it, and
/// a card it saves in Agent mode still carries its own manager, so nothing here changes what it reads.
/// </summary>
public sealed partial class BoardStore
{
    // board-agent-workflow/2, as shipped. Never edit: databases that applied it keep this shape.
    private const string BoardManagerSchemaSql = """
        CREATE TABLE IF NOT EXISTS BoardManagers (
            BoardId TEXT PRIMARY KEY REFERENCES Boards(Id) ON DELETE CASCADE,
            Selection TEXT NOT NULL,
            OptionsJson TEXT,
            Revision INTEGER NOT NULL,
            UpdatedUTC TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS BoardManagerSessions (
            SessionId TEXT PRIMARY KEY,
            BoardId TEXT NOT NULL REFERENCES Boards(Id) ON DELETE CASCADE,
            TabId TEXT,
            Selection TEXT NOT NULL,
            Cli TEXT NOT NULL,
            StartedUTC TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS IX_BoardManagerSessions_Board ON BoardManagerSessions(BoardId, StartedUTC);
        """;

    // board-agent-workflow/3: the "AI board manager" switch, one row, absent until first saved (off).
    private const string BoardManagerFeatureSchemaSql = """
        CREATE TABLE IF NOT EXISTS BoardManagerFeature (
            Id INTEGER PRIMARY KEY CHECK (Id = 1),
            Enabled INTEGER NOT NULL,
            UpdatedUTC TEXT NOT NULL
        );
        """;

    /// <summary>board-agent-workflow/3: the switch table and two nullable manager columns. No backfill.</summary>
    private static void ApplyBoardManagerFeatureMigration(SqliteConnection db, SqliteTransaction transaction)
    {
        SqliteSchema.Execute(db, transaction, BoardManagerFeatureSchemaSql);
        SqliteSchema.AdoptStatement(db, transaction, "ALTER TABLE BoardManagers ADD COLUMN Instructions TEXT");
        SqliteSchema.AdoptStatement(db, transaction, "ALTER TABLE BoardManagers ADD COLUMN MaxWorkers INTEGER");
    }

    // Board-managed: Agent workflow and no manager of its own. Used by every read below.
    private const string BoardManagedCardSql =
        "c.WorkflowMode = 'agent' AND (c.ManagerSelection IS NULL OR TRIM(c.ManagerSelection) = '')";

    public async Task<bool> GetBoardManagerEnabledAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await ReadBoardManagerEnabledAsync(connection, null, cancellationToken);
    }

    public async Task<bool> SetBoardManagerEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO BoardManagerFeature (Id, Enabled, UpdatedUTC) VALUES (1, $enabled, $updated)
                ON CONFLICT(Id) DO UPDATE SET Enabled = excluded.Enabled, UpdatedUTC = excluded.UpdatedUTC;
                """;
            command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
            command.Parameters.AddWithValue("$updated", ToDb(DateTime.UtcNow));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        var saved = await ReadBoardManagerEnabledAsync(connection, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return saved;
    }

    private static async Task<bool> ReadBoardManagerEnabledAsync(SqliteConnection connection, SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT Enabled FROM BoardManagerFeature WHERE Id = 1;";
        return await command.ExecuteScalarAsync(cancellationToken) is long enabled && enabled != 0;
    }

    public async Task<BoardManagerSettingsRecord?> GetBoardManagerAsync(string projectPath, string boardId, CancellationToken cancellationToken = default)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        return await ReadBoardManagerAsync(connection, null, project, boardId, cancellationToken);
    }

    public async Task<BoardManagerSettingsRecord?> SaveBoardManagerAsync(string projectPath, string boardId, BoardManagerSettings settings, int expectedRevision, CancellationToken cancellationToken = default)
    {
        if (settings.MaxWorkers is < 1 or > BoardManagerSettings.MaxWorkersLimit)
            throw new BoardValidationException($"Workers at a time must be between 1 and {BoardManagerSettings.MaxWorkersLimit}.");
        if (settings.Instructions?.Length > BoardManagerSettings.MaxInstructionsLength)
            throw new BoardValidationException($"Manager instructions are limited to {BoardManagerSettings.MaxInstructionsLength} characters.");
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var current = await ReadBoardManagerAsync(connection, transaction, project, boardId, cancellationToken);
        if (current is null) return null;
        // Shared copies of another account's board are read-only (VIBE-235), settings included.
        await RequireWritableBoardAsync(connection, transaction, boardId.Trim(), cancellationToken);
        if (current.Revision != expectedRevision)
            throw new BoardConflictException("The board manager changed while you were editing. Reopen settings to load the latest version; your draft has not been saved.");
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        BoardManagerSettingsRecord updated;
        if (!settings.IsConfigured)
        {
            // Clearing removes the row: the next read says "no manager" at revision 0, and a stale
            // editor that still holds the old revision gets a conflict instead of a silent overwrite.
            command.CommandText = "DELETE FROM BoardManagers WHERE BoardId = $id;";
            command.Parameters.AddWithValue("$id", boardId.Trim());
            updated = new(BoardManagerSettings.None, 0);
        }
        else
        {
            var instructions = string.IsNullOrWhiteSpace(settings.Instructions) ? null : settings.Instructions.Trim();
            updated = new(settings with { Instructions = instructions }, checked(current.Revision + 1));
            command.CommandText = """
                INSERT INTO BoardManagers (BoardId, Selection, OptionsJson, Revision, UpdatedUTC, Instructions, MaxWorkers)
                VALUES ($id, $selection, $options, $revision, $updated, $instructions, $maxWorkers)
                ON CONFLICT(BoardId) DO UPDATE SET Selection = excluded.Selection, OptionsJson = excluded.OptionsJson, Revision = excluded.Revision,
                    UpdatedUTC = excluded.UpdatedUTC, Instructions = excluded.Instructions, MaxWorkers = excluded.MaxWorkers;
                """;
            command.Parameters.AddWithValue("$id", boardId.Trim());
            command.Parameters.AddWithValue("$selection", settings.Selection!);
            command.Parameters.AddWithValue("$options", settings.BaseLlmOptions is null ? DBNull.Value
                : JsonSerializer.Serialize(settings.BaseLlmOptions, StorageJsonSerializerContext.Default.BaseLlmOptions));
            command.Parameters.AddWithValue("$revision", updated.Revision);
            command.Parameters.AddWithValue("$updated", ToDb(DateTime.UtcNow));
            command.Parameters.AddWithValue("$instructions", (object?)instructions ?? DBNull.Value);
            command.Parameters.AddWithValue("$maxWorkers", settings.MaxWorkers);
        }
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return updated;
    }

    private static async Task<BoardManagerSettingsRecord?> ReadBoardManagerAsync(SqliteConnection connection, SqliteTransaction? transaction, string project, string boardId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // A LEFT JOIN distinguishes a scoped board without a manager from a missing/foreign board.
        command.CommandText = $"""
            SELECT m.Selection, m.OptionsJson, m.Revision, m.Instructions, m.MaxWorkers FROM Boards b
            LEFT JOIN BoardManagers m ON m.BoardId = b.Id
            WHERE b.ProjectPath = $project{ProjectPathCollation} AND b.Id = $id;
            """;
        command.Parameters.AddWithValue("$project", project);
        command.Parameters.AddWithValue("$id", boardId.Trim());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        if (reader.IsDBNull(0)) return new(BoardManagerSettings.None, 0);
        var options = reader.IsDBNull(1) ? null : JsonSerializer.Deserialize(reader.GetString(1), StorageJsonSerializerContext.Default.BaseLlmOptions);
        // A row the first build wrote has neither column: no standing orders, one worker at a time.
        var maxWorkers = reader.IsDBNull(4) ? BoardManagerSettings.DefaultMaxWorkers
            : (int)Math.Clamp(reader.GetInt64(4), 1, BoardManagerSettings.MaxWorkersLimit);
        return new(new BoardManagerSettings(reader.GetString(0), options, reader.IsDBNull(3) ? null : reader.GetString(3), maxWorkers), reader.GetInt32(2));
    }

    /// <summary>
    /// Agent mode needs someone to manage the card: its own manager, or the board's while the AI board
    /// manager is on. Both are read inside the caller's write transaction, so a card cannot be saved as
    /// managed by a board manager that was cleared, or a switch that was turned off, a moment earlier.
    /// With the switch off this is exactly the rule before board managers existed.
    /// </summary>
    private static async Task ValidateAgentWorkflowAsync(SqliteConnection connection, SqliteTransaction transaction, string project,
        string workflowMode, string? managerSelection, string boardId, CancellationToken cancellationToken)
    {
        if (!BoardWorkflowModes.IsValid(workflowMode))
            throw new BoardValidationException("Workflow must be manual or agent.");
        if (workflowMode != BoardWorkflowModes.Agent || !string.IsNullOrWhiteSpace(managerSelection)) return;
        if (!await ReadBoardManagerEnabledAsync(connection, transaction, cancellationToken))
            throw new BoardValidationException("Select a manager agent for the Agent workflow.");
        var board = string.IsNullOrEmpty(boardId) ? null : await ReadBoardManagerAsync(connection, transaction, project, boardId, cancellationToken);
        if (board?.Settings.IsConfigured != true)
            throw new BoardValidationException("Select a manager agent for the Agent workflow, or set up this board's manager in Board settings > Manager.");
    }

    public async Task<BoardManagerSessionRecord?> RecordBoardManagerSessionAsync(string projectPath, string boardId, string sessionId, string? tabId, string selection, string cli, CancellationToken cancellationToken = default)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        if (await ReadBoardManagerAsync(connection, transaction, project, boardId, cancellationToken) is null) return null;
        var record = new BoardManagerSessionRecord(sessionId, boardId.Trim(), tabId, selection, cli, DateTime.UtcNow);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO BoardManagerSessions (SessionId, BoardId, TabId, Selection, Cli, StartedUTC) VALUES ($session, $board, $tab, $selection, $cli, $started)
            ON CONFLICT(SessionId) DO UPDATE SET BoardId = excluded.BoardId, TabId = excluded.TabId, Selection = excluded.Selection, Cli = excluded.Cli;
            """;
        command.Parameters.AddWithValue("$session", record.SessionId);
        command.Parameters.AddWithValue("$board", record.BoardId);
        command.Parameters.AddWithValue("$tab", (object?)record.TabId ?? DBNull.Value);
        command.Parameters.AddWithValue("$selection", record.Selection);
        command.Parameters.AddWithValue("$cli", record.Cli);
        command.Parameters.AddWithValue("$started", ToDb(record.StartedUtc));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return record;
    }

    public async Task<IReadOnlyList<BoardManagerSessionRecord>> GetBoardManagerSessionsAsync(string projectPath, string boardId, int limit = 10, CancellationToken cancellationToken = default)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT s.SessionId, s.BoardId, s.TabId, s.Selection, s.Cli, s.StartedUTC
            FROM BoardManagerSessions s JOIN Boards b ON b.Id = s.BoardId
            WHERE b.ProjectPath = $project{ProjectPathCollation} AND b.Id = $board
            ORDER BY s.StartedUTC DESC, s.rowid DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$project", project);
        command.Parameters.AddWithValue("$board", boardId.Trim());
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100));
        var sessions = new List<BoardManagerSessionRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            sessions.Add(new(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3), reader.GetString(4), ParseDb(reader.GetString(5))));
        return sessions;
    }

    /// <summary>Characters of an entry's body read for an event; the tool shows 300 of them.</summary>
    private const int EventBodyReadChars = 600;

    /// <summary>Most entries returned for one shared stamp; far beyond any real burst, it only bounds a pathological one.</summary>
    private const int MaxEventCohort = 10_000;

    public async Task<IReadOnlyList<BoardEventRecord>> GetBoardEventsAsync(string projectPath, string boardId, DateTime sinceUtc, int limit = 100,
        bool managedOnly = false, string? excludeSessionId = null, CancellationToken cancellationToken = default)
    {
        var project = NormalizeProjectPath(projectPath);
        limit = Math.Clamp(limit, 1, 500);
        var since = ToDb(sinceUtc);
        var exclude = string.IsNullOrWhiteSpace(excludeSessionId) ? null : excludeSessionId.Trim();
        await using var connection = await OpenAsync(cancellationToken);
        var events = await ReadBoardEventsAsync(connection, project, boardId.Trim(), since, null, limit + 1, managedOnly, exclude, cancellationToken);
        if (events.Count <= limit) return events.Select(item => item.Event).ToList();
        // A full batch: drop the trailing entries that share the last returned stamp with the row
        // just past the limit, so the next cursor (the last stamp returned) cannot skip any of them.
        var cut = events[limit].Stamp;
        var kept = events.Take(limit).Where(item => !string.Equals(item.Stamp, cut, StringComparison.Ordinal)).Select(item => item.Event).ToList();
        if (kept.Count > 0) return kept;
        // The whole batch is one stamp: return that whole group, however large, so the cursor can
        // move past it without leaving any of its entries behind.
        return (await ReadBoardEventsAsync(connection, project, boardId.Trim(), since, cut, MaxEventCohort, managedOnly, exclude, cancellationToken))
            .Select(item => item.Event).ToList();
    }

    /// <summary>
    /// Two sources, one stream, both stamped in the ToDb round-trip format, which orders as text:
    /// 1. The Card Log (VB-51): every card write appends a BoardComments row in its own IMMEDIATE
    ///    transaction, so moves, edits, comments, handoffs and flags arrive in commit order. Hidden
    ///    and deleted discussion stays out, exactly as it does for card reads; a card's deletion is
    ///    the one entry kept for a deleted card, so a manager learns the card is gone.
    /// 2. Completion reports (BoardAgentCompletions), which are not Card Log rows: a worker that
    ///    reports and exits without a comment still wakes its manager.
    /// managedOnly keeps the cards the board manager owns, plus a workflow or manager change on any
    /// card: switching a card to Manual, or giving it its own manager, takes it out of scope.
    /// <paramref name="exactStamp"/> reads one stamp's whole group.
    /// </summary>
    private static async Task<List<(string Stamp, BoardEventRecord Event)>> ReadBoardEventsAsync(SqliteConnection connection, string project, string boardId,
        string since, string? exactStamp, int limit, bool managedOnly, string? excludeSessionId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT Stamp, CardId, CardKey, DisplayId, LaneName, Kind, AuthorKind, AuthorLabel, Body, Attention FROM (
                SELECT m.CreatedUTC AS Stamp, 0 AS Source, m.rowid AS RowOrder, c.Id AS CardId, {CardKeySql} AS CardKey, c.DisplayId AS DisplayId,
                       lane.Name AS LaneName, m.Kind AS Kind, m.AuthorKind AS AuthorKind, m.AuthorLabel AS AuthorLabel,
                       substr(m.Body, 1, {EventBodyReadChars}) AS Body,
                       CASE WHEN m.Kind = 'comment' AND m.Changes LIKE '%"attention"%' THEN m.Changes END AS Attention
                FROM BoardComments m
                JOIN BoardCards c ON c.Id = m.CardId
                JOIN BoardColumns lane ON lane.Id = c.ColumnId
                {CardPrefixJoinSql}
                WHERE c.ProjectPath = $project{ProjectPathCollation} AND lane.BoardId = $board
                  AND (c.DeletedUTC IS NULL OR m.Kind = 'deleted')
                  AND m.CreatedUTC > $since AND ($exact IS NULL OR m.CreatedUTC = $exact) AND m.DiscussionHidden = 0
                  AND NOT EXISTS (SELECT 1 FROM BoardDeletedComments d WHERE d.CommentId = m.Id)
                  AND ($managedOnly = 0 OR ({BoardManagedCardSql})
                       OR (m.Kind = 'change' AND (m.Changes LIKE '%"workflowMode"%' OR m.Changes LIKE '%"managerSelection"%')))
                  AND ($exclude IS NULL OR m.SessionId IS NULL OR m.SessionId <> $exclude)
                UNION ALL
                SELECT a.CompletedUTC, 1, a.rowid, c.Id, {CardKeySql}, c.DisplayId, lane.Name, 'completion', 'agent', s.DisplayName,
                       substr(a.Outcome || ': ' || a.Summary, 1, {EventBodyReadChars}), NULL
                FROM BoardAgentCompletions a
                JOIN {AllSessionsSql} s ON s.SessionId = a.SessionId
                JOIN BoardCards c ON c.Id = s.CardId
                JOIN BoardColumns lane ON lane.Id = c.ColumnId
                {CardPrefixJoinSql}
                WHERE a.ProjectPath = $project{ProjectPathCollation} AND c.ProjectPath = $project{ProjectPathCollation} AND lane.BoardId = $board
                  AND c.DeletedUTC IS NULL AND a.CompletedUTC > $since AND ($exact IS NULL OR a.CompletedUTC = $exact)
                  AND ($managedOnly = 0 OR ({BoardManagedCardSql}))
                  AND ($exclude IS NULL OR a.SessionId <> $exclude)
            )
            ORDER BY Stamp, Source, RowOrder LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$project", project);
        command.Parameters.AddWithValue("$board", boardId);
        command.Parameters.AddWithValue("$since", since);
        command.Parameters.AddWithValue("$exact", (object?)exactStamp ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$managedOnly", managedOnly ? 1 : 0);
        command.Parameters.AddWithValue("$exclude", (object?)excludeSessionId ?? DBNull.Value);
        var events = new List<(string Stamp, BoardEventRecord Event)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var stamp = reader.GetString(0);
            events.Add((stamp, new BoardEventRecord(ParseDb(stamp), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4), reader.GetString(5),
                reader.GetString(6), reader.GetString(7), reader.IsDBNull(8) ? "" : reader.GetString(8),
                !reader.IsDBNull(9) && BoardAttention.IsAttention(reader.GetString(9)))));
        }
        return events;
    }

    public async Task<IReadOnlyList<BoardManagedCardRecord>> GetManagedCardsAsync(string projectPath, string boardId, CancellationToken cancellationToken = default)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        var cards = new List<BoardCardRecord>();
        await using (var command = connection.CreateCommand())
        {
            // CardSelectSql ends on its prefix join without trailing whitespace; the newline keeps the JOIN apart.
            command.CommandText = CardSelectSql + "\n" + $"""
                JOIN BoardColumns lane ON lane.Id = c.ColumnId
                WHERE c.ProjectPath = $project{ProjectPathCollation} AND lane.BoardId = $board AND c.DeletedUTC IS NULL
                  AND c.WorkflowMode = 'agent'
                ORDER BY lane.Position, c.Flagged DESC, c.Position, c.Number;
                """;
            command.Parameters.AddWithValue("$project", project);
            command.Parameters.AddWithValue("$board", boardId.Trim());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) cards.Add(ReadCard(reader));
        }
        if (cards.Count == 0) return [];
        var lanes = (await ReadColumnsAsync(connection, null, project, boardId.Trim(), cancellationToken)).ToDictionary(lane => lane.Id, lane => lane.Name);
        // Discussion does not bump UpdatedUTC, so the newest visible entry is read alongside it.
        var latest = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                SELECT m.CardId, MAX(m.CreatedUTC) FROM BoardComments m
                JOIN BoardCards c ON c.Id = m.CardId
                JOIN BoardColumns lane ON lane.Id = c.ColumnId
                WHERE c.ProjectPath = $project{ProjectPathCollation} AND lane.BoardId = $board AND c.DeletedUTC IS NULL AND c.WorkflowMode = 'agent'
                GROUP BY m.CardId;
                """;
            command.Parameters.AddWithValue("$project", project);
            command.Parameters.AddWithValue("$board", boardId.Trim());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                if (!reader.IsDBNull(1)) latest[reader.GetString(0)] = ParseDb(reader.GetString(1));
        }
        return cards.Select(card => new BoardManagedCardRecord(card,
                lanes.GetValueOrDefault(card.ColumnId, "(no lane)"),
                latest.TryGetValue(card.Id, out var activity) && activity > card.UpdatedUtc ? activity : card.UpdatedUtc,
                OwnManager: !string.IsNullOrWhiteSpace(card.ManagerSelection)))
            .ToList();
    }

    public async Task<IReadOnlyList<BoardManagedWorkerSession>> GetManagedWorkerSessionsAsync(string projectPath, string boardId, CancellationToken cancellationToken = default)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        // Primary and additional attachments (board/10) both carry the launch origin; a worker
        // launched for a card is always linked to it, so this is every worker the manager started.
        command.CommandText = $"""
            SELECT s.SessionId, c.Id, {CardKeySql}, c.DisplayId, s.Origin, s.CreatedUTC
            FROM {AllSessionsSql} s
            JOIN BoardCards c ON c.Id = s.CardId
            JOIN BoardColumns lane ON lane.Id = c.ColumnId
            {CardPrefixJoinSql}
            WHERE c.ProjectPath = $project{ProjectPathCollation} AND lane.BoardId = $board AND c.DeletedUTC IS NULL
              AND {BoardManagedCardSql}
              AND s.Origin IN ('{BoardSessionRecord.WorkerOrigin}', '{BoardSessionRecord.PlanningWorkerOrigin}');
            """;
        command.Parameters.AddWithValue("$project", project);
        command.Parameters.AddWithValue("$board", boardId.Trim());
        var sessions = new List<BoardManagedWorkerSession>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            sessions.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4), ParseDb(reader.GetString(5))));
        return sessions;
    }
}
