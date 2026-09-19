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

    public async Task<BoardLaneAutomation?> SaveLaneAutomationAsync(string projectPath, string columnId, long? jobId, int expectedRevision, CancellationToken cancellationToken = default)
    {
        if (jobId is <= 0 || expectedRevision < 0)
            throw new BoardValidationException("Invalid Automation or settings revision.");
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var current = await ReadLaneAutomationAsync(connection, transaction, project, columnId, cancellationToken);
        if (current is null) return null;
        if (current.Revision != expectedRevision)
            throw new BoardConflictException("Lane automation changed while you were editing. Reopen the lane to load the latest version.");
        if (jobId is not null)
        {
            await using var validate = connection.CreateCommand();
            validate.Transaction = transaction;
            validate.CommandText = $"SELECT COUNT(*) FROM Jobs WHERE Id = $job AND ProjectPath = $project{ProjectPathCollation} AND DeletedUTC IS NULL AND Enabled = 1;";
            validate.Parameters.AddWithValue("$job", jobId.Value);
            validate.Parameters.AddWithValue("$project", project);
            if (Convert.ToInt64(await validate.ExecuteScalarAsync(cancellationToken)) == 0)
                throw new BoardValidationException("Choose an enabled Automation from this project.");
        }
        var saved = new BoardLaneAutomation(jobId, checked(current.Revision + 1));
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO BoardLaneAutomations (ColumnId, JobId, Revision) VALUES ($column, $job, $revision)
            ON CONFLICT(ColumnId) DO UPDATE SET JobId = excluded.JobId, Revision = excluded.Revision;
            DELETE FROM BoardPendingAutomations WHERE ColumnId = $column;
            """;
        command.Parameters.AddWithValue("$column", columnId.Trim());
        command.Parameters.AddWithValue("$job", (object?)jobId ?? DBNull.Value);
        command.Parameters.AddWithValue("$revision", saved.Revision);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return saved;
    }

    private static async Task<BoardLaneAutomation?> ReadLaneAutomationAsync(SqliteConnection connection, SqliteTransaction? transaction, string project, string columnId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT a.JobId, a.Revision FROM BoardColumns c
            LEFT JOIN BoardLaneAutomations a ON a.ColumnId = c.Id
            WHERE c.ProjectPath = $project{ProjectPathCollation} AND c.Id = $column;
            """;
        command.Parameters.AddWithValue("$project", project);
        command.Parameters.AddWithValue("$column", columnId.Trim());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new(reader.IsDBNull(0) ? null : reader.GetInt64(0), reader.IsDBNull(1) ? 0 : reader.GetInt32(1));
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
