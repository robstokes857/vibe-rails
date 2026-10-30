using System.Data;
using Microsoft.Data.Sqlite;

namespace VibeRails.Services.Board;

public sealed partial class BoardStore
{
    public async Task<BoardLaneAutomation?> GetLaneAutomationAsync(string projectPath, string columnId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await ReadLaneAutomationAsync(connection, null, NormalizeProjectPath(projectPath), columnId, cancellationToken);
    }

    public async Task<BoardLaneAutomation?> SaveLaneAutomationAsync(string projectPath, string columnId, IReadOnlyList<long> jobIds, int expectedRevision, CancellationToken cancellationToken = default)
    {
        var selected = jobIds.ToArray();
        if (selected.Any(id => id <= 0) || selected.Distinct().Count() != selected.Length || expectedRevision < 0)
            throw new BoardValidationException("Choose distinct Automations and a valid settings revision.");
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var current = await ReadLaneAutomationAsync(connection, transaction, project, columnId, cancellationToken);
        if (current is null) return null;
        var column = await ReadColumnAsync(connection, transaction, project, columnId, cancellationToken);
        await RequireWritableBoardAsync(connection, transaction, column!.BoardId, cancellationToken);
        if (current.Revision != expectedRevision)
            throw new BoardConflictException("Lane automation changed while you were editing. Reopen the lane to load the latest version.");
        // Jobs are local Automation definitions in state.db. Only read them here; the scheduler
        // validates them again when it queues the run, since the two stores commit independently.
        await using var state = selected.Length > 0 ? await OpenStateAsync(cancellationToken) : null;
        foreach (var jobId in selected)
        {
            await using var validate = state!.CreateCommand();
            validate.CommandText = $"SELECT COUNT(*) FROM Jobs WHERE Id = $job AND ProjectPath = $project{ProjectPathCollation} AND DeletedUTC IS NULL AND Enabled = 1;";
            validate.Parameters.AddWithValue("$job", jobId);
            validate.Parameters.AddWithValue("$project", project);
            if (Convert.ToInt64(await validate.ExecuteScalarAsync(cancellationToken)) == 0)
                throw new BoardValidationException("Choose an enabled Automation from this project.");
        }
        var saved = new BoardLaneAutomation(selected, checked(current.Revision + 1));
        await WriteLaneSelectionAsync(connection, transaction, columnId.Trim(), saved, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return saved;
    }

    /// <summary>
    /// A deleted Automation must neither keep showing in lane settings nor queue entries for new
    /// cards. Each lane that selects it is rewritten through the settings-save path, so the lane's
    /// pending entries are cancelled and its revision advances for any open editor.
    /// </summary>
    public async Task<int> RemoveJobFromLanesAsync(long jobId, CancellationToken cancellationToken = default)
    {
        if (jobId <= 0) return 0;
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var lanes = new Dictionary<string, (List<long> JobIds, int Revision)>(StringComparer.Ordinal);
        await using (var query = connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = """
                SELECT a.ColumnId, a.JobId, a.Revision, extra.JobId FROM BoardLaneAutomations a
                LEFT JOIN BoardLaneAdditionalAutomations extra ON extra.ColumnId = a.ColumnId
                WHERE a.JobId = $job OR a.ColumnId IN (SELECT ColumnId FROM BoardLaneAdditionalAutomations WHERE JobId = $job)
                ORDER BY a.ColumnId, extra.Position;
                """;
            query.Parameters.AddWithValue("$job", jobId);
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var columnId = reader.GetString(0);
                if (!lanes.TryGetValue(columnId, out var lane))
                {
                    lanes[columnId] = lane = ([], reader.GetInt32(2));
                    if (!reader.IsDBNull(1)) lane.JobIds.Add(reader.GetInt64(1));
                }
                if (!reader.IsDBNull(3)) lane.JobIds.Add(reader.GetInt64(3));
            }
        }
        foreach (var (columnId, lane) in lanes)
            await WriteLaneSelectionAsync(connection, transaction, columnId,
                new(lane.JobIds.Where(id => id != jobId).ToArray(), checked(lane.Revision + 1)), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return lanes.Count;
    }

    // Updating the legacy header clears additional selections and pending entries, including
    // when an older binary saves a single selection. Rebuild the remainder in this transaction.
    private static async Task WriteLaneSelectionAsync(SqliteConnection connection, SqliteTransaction transaction, string columnId,
        BoardLaneAutomation selection, CancellationToken cancellationToken)
    {
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO BoardLaneAutomations (ColumnId, JobId, Revision) VALUES ($column, $job, $revision)
                ON CONFLICT(ColumnId) DO UPDATE SET JobId = excluded.JobId, Revision = excluded.Revision;
                DELETE FROM BoardPendingAutomations WHERE ColumnId = $column;
                """;
            command.Parameters.AddWithValue("$column", columnId);
            command.Parameters.AddWithValue("$job", (object?)selection.JobId ?? DBNull.Value);
            command.Parameters.AddWithValue("$revision", selection.Revision);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        for (var position = 1; position < selection.JobIds.Count; position++)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO BoardLaneAdditionalAutomations (ColumnId, JobId, Position) VALUES ($column, $job, $position);";
            insert.Parameters.AddWithValue("$column", columnId);
            insert.Parameters.AddWithValue("$job", selection.JobIds[position]);
            insert.Parameters.AddWithValue("$position", position);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<BoardLaneAutomation?> ReadLaneAutomationAsync(SqliteConnection connection, SqliteTransaction? transaction, string project, string columnId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT a.JobId, a.Revision, extra.JobId FROM BoardColumns c
            LEFT JOIN BoardLaneAutomations a ON a.ColumnId = c.Id
            LEFT JOIN BoardLaneAdditionalAutomations extra ON extra.ColumnId = c.Id
            WHERE c.ProjectPath = $project{ProjectPathCollation} AND c.Id = $column
            ORDER BY extra.Position;
            """;
        command.Parameters.AddWithValue("$project", project);
        command.Parameters.AddWithValue("$column", columnId.Trim());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var jobIds = new List<long>();
        if (!reader.IsDBNull(0)) jobIds.Add(reader.GetInt64(0));
        var revision = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
        do
        {
            if (!reader.IsDBNull(2)) jobIds.Add(reader.GetInt64(2));
        } while (await reader.ReadAsync(cancellationToken));
        return new(jobIds, revision);
    }

    // Triggers write only to new tables: legacy card writers still work and also participate in
    // debounce. The pending row is replaced in the SAME transaction as a move, including MCP,
    // form saves, cross-board moves and the relocation caused by deleting a lane. Reordering
    // within a lane and ordinary edits are deliberately not new lane entries.
    private const string LaneAutomationSchemaSql = """
        CREATE TABLE IF NOT EXISTS BoardLaneAutomations (
            ColumnId TEXT PRIMARY KEY REFERENCES BoardColumns(Id) ON DELETE CASCADE,
            JobId INTEGER NULL,
            Revision INTEGER NOT NULL
        );
        CREATE TABLE IF NOT EXISTS BoardPendingAutomations (
            CardId TEXT PRIMARY KEY REFERENCES BoardCards(Id) ON DELETE CASCADE,
            ColumnId TEXT NOT NULL REFERENCES BoardColumns(Id) ON DELETE CASCADE,
            JobId INTEGER NOT NULL,
            EventKey TEXT NOT NULL,
            DueUnixMs INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS IX_BoardPendingAutomations_Due ON BoardPendingAutomations(DueUnixMs);
        CREATE TRIGGER IF NOT EXISTS BoardCards_LaneAutomation_Insert AFTER INSERT ON BoardCards BEGIN
            INSERT INTO BoardPendingAutomations (CardId, ColumnId, JobId, EventKey, DueUnixMs)
            SELECT NEW.Id, NEW.ColumnId, a.JobId, lower(hex(randomblob(16))), CAST(unixepoch('subsec') * 1000 AS INTEGER) + 60000
            FROM BoardLaneAutomations a WHERE a.ColumnId = NEW.ColumnId AND a.JobId IS NOT NULL;
        END;
        CREATE TRIGGER IF NOT EXISTS BoardCards_LaneAutomation_Move AFTER UPDATE OF ColumnId ON BoardCards
        WHEN OLD.ColumnId <> NEW.ColumnId BEGIN
            DELETE FROM BoardPendingAutomations WHERE CardId = NEW.Id;
            INSERT INTO BoardPendingAutomations (CardId, ColumnId, JobId, EventKey, DueUnixMs)
            SELECT NEW.Id, NEW.ColumnId, a.JobId, lower(hex(randomblob(16))), CAST(unixepoch('subsec') * 1000 AS INTEGER) + 60000
            FROM BoardLaneAutomations a WHERE a.ColumnId = NEW.ColumnId AND a.JobId IS NOT NULL;
        END;
        """;
}
