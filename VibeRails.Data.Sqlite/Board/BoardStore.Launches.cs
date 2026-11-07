using System.Text.Json;
using Microsoft.Data.Sqlite;
using VibeRails.Data.Sqlite;
using VibeRails.DTOs;

namespace VibeRails.Services.Board;

public sealed partial class BoardStore
{
    private static void ApplyLaunchSchema(SqliteConnection db, SqliteTransaction tx)
    {
        SqliteSchema.Execute(db, tx, """
            CREATE TABLE BoardLaunchLanes(ColumnId TEXT PRIMARY KEY REFERENCES BoardColumns(Id) ON DELETE CASCADE, Revision INTEGER NOT NULL);
            CREATE TABLE BoardLaunchSelections(ColumnId TEXT NOT NULL REFERENCES BoardColumns(Id) ON DELETE CASCADE,
                Position INTEGER NOT NULL, DefinitionId TEXT NOT NULL, DefinitionType TEXT NOT NULL, Gate TEXT NOT NULL,
                PRIMARY KEY(ColumnId, Position), UNIQUE(ColumnId,DefinitionId));
            CREATE TABLE BoardLaunchEvents(Id TEXT PRIMARY KEY, EntryId TEXT NOT NULL, ProjectPath TEXT NOT NULL,
                CardId TEXT NOT NULL, ColumnId TEXT NOT NULL, BoardId TEXT, DefinitionId TEXT NOT NULL,
                DefinitionType TEXT NOT NULL, Gate TEXT NOT NULL, Position INTEGER NOT NULL, DueUnixMs INTEGER NOT NULL,
                Current INTEGER NOT NULL, RunId TEXT, ProcessStatus TEXT NOT NULL DEFAULT 'queued',
                Verdict TEXT, SkipRequested INTEGER NOT NULL DEFAULT 0, Summary TEXT);
            CREATE INDEX IX_BoardLaunchEvents_Current ON BoardLaunchEvents(Current,DueUnixMs);
            CREATE INDEX IX_BoardLaunchEvents_Card ON BoardLaunchEvents(CardId,Current,EntryId,Position);
            CREATE TABLE BoardLaunchReports(Id INTEGER PRIMARY KEY AUTOINCREMENT, EventId TEXT NOT NULL,
                Verdict TEXT NOT NULL, Summary TEXT NOT NULL, SessionId TEXT, ReviewId TEXT, CreatedUTC TEXT NOT NULL);
            """);
        foreach (var (name, operation, condition) in new[] { ("Insert", "INSERT", ""), ("Move", "UPDATE OF ColumnId", "WHEN OLD.ColumnId<>NEW.ColumnId") })
            SqliteSchema.Execute(db, tx, $"""
                CREATE TRIGGER BoardCards_Launch_{name} AFTER {operation} ON BoardCards {condition} BEGIN
                    UPDATE BoardLaunchEvents SET Current=0 WHERE CardId=NEW.Id AND Current=1;
                    INSERT INTO BoardLaunchEvents(Id,EntryId,ProjectPath,CardId,ColumnId,BoardId,DefinitionId,DefinitionType,Gate,Position,DueUnixMs,Current)
                    SELECT lower(hex(randomblob(16))), NEW.Id || ':' || NEW.UpdatedUTC, NEW.ProjectPath,NEW.Id,NEW.ColumnId,
                        c.BoardId,s.DefinitionId,s.DefinitionType,s.Gate,s.Position,CAST(unixepoch('subsec')*1000 AS INTEGER)+60000,1
                    FROM BoardLaunchSelections s JOIN BoardColumns c ON c.Id=s.ColumnId WHERE s.ColumnId=NEW.ColumnId;
                END;
                """);
        SqliteSchema.Execute(db, tx, """
            CREATE TRIGGER BoardCards_Launch_Delete AFTER DELETE ON BoardCards BEGIN
                UPDATE BoardLaunchEvents SET Current=0 WHERE CardId=OLD.Id;
            END;
            CREATE TRIGGER BoardCards_Launch_Retire AFTER UPDATE OF DeletedUTC ON BoardCards WHEN NEW.DeletedUTC IS NOT NULL BEGIN
                UPDATE BoardLaunchEvents SET Current=0 WHERE CardId=NEW.Id;
            END;
            """);
    }

    public async Task<BoardLaunchLane?> GetLaunchLaneAsync(string project, string columnId, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        return await ReadLaunchLaneAsync(db, null, NormalizeProjectPath(project), columnId, ct);
    }
    private static async Task<BoardLaunchLane?> ReadLaunchLaneAsync(SqliteConnection db, SqliteTransaction? tx,
        string project, string columnId, CancellationToken ct)
    {
        if (await ReadColumnAsync(db, tx, project, columnId, ct) is null) return null;
        await using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT Revision FROM BoardLaunchLanes WHERE ColumnId=$id;";
        cmd.Parameters.AddWithValue("$id", columnId);
        var revision = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct) ?? 0L);
        cmd.CommandText = "SELECT DefinitionId,DefinitionType,Gate FROM BoardLaunchSelections WHERE ColumnId=$id ORDER BY Position;";
        await using var r = await cmd.ExecuteReaderAsync(ct);
        List<BoardLaunchSelection> values = [];
        while (await r.ReadAsync(ct)) values.Add(new(r.GetString(0), r.GetString(1), r.GetString(2)));
        return new(values, revision);
    }
    public async Task<BoardLaunchLane?> SaveLaunchLaneAsync(string project, string columnId, BoardLaunchLane lane, CancellationToken ct = default)
    {
        if (lane.Selections is null || lane.Selections.Count > 40 || lane.Selections.Any(s => s is null || string.IsNullOrWhiteSpace(s.DefinitionId))
            || lane.Selections.Select(s => s.DefinitionId).Distinct().Count() != lane.Selections.Count
            || lane.Selections.Any(s => s.DefinitionType is not ("preset" or "action") || s.Gate is not ("process" or "result" or "review")))
            throw new BoardValidationException("Choose up to 40 distinct definitions and a supported gate.");
        project = NormalizeProjectPath(project);
        await using var db = await OpenAsync(ct);
        await using var tx = db.BeginTransaction(deferred: false);
        var current = await ReadLaunchLaneAsync(db, tx, project, columnId, ct);
        if (current is null) return null;
        var column = await ReadColumnAsync(db, tx, project, columnId, ct);
        await RequireWritableBoardAsync(db, tx, column!.BoardId, ct);
        if (current.Revision != lane.Revision) throw new BoardConflictException("Lane launches changed. Reload the lane before saving.");
        await using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO BoardLaunchLanes VALUES($column,$revision) ON CONFLICT(ColumnId) DO UPDATE SET Revision=excluded.Revision;
            DELETE FROM BoardLaunchSelections WHERE ColumnId=$column;
            """;
        cmd.Parameters.AddWithValue("$column", columnId);
        cmd.Parameters.AddWithValue("$revision", lane.Revision + 1);
        await cmd.ExecuteNonQueryAsync(ct);
        for (var i = 0; i < lane.Selections.Count; i++)
        {
            var selection = lane.Selections[i];
            cmd.CommandText = "INSERT INTO BoardLaunchSelections VALUES($column,$position,$definition,$type,$gate);";
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("$column", columnId);
            cmd.Parameters.AddWithValue("$position", i);
            cmd.Parameters.AddWithValue("$definition", selection.DefinitionId);
            cmd.Parameters.AddWithValue("$type", selection.DefinitionType);
            cmd.Parameters.AddWithValue("$gate", selection.Gate);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return lane with { Revision = lane.Revision + 1 };
    }

    public async Task<IReadOnlyList<BoardLaunchEvent>> GetLaunchEventsAsync(string? project = null, string? cardId = null, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        await using var cmd = db.CreateCommand();
        cmd.CommandText = $"""
            SELECT Id,ProjectPath,CardId,ColumnId,BoardId,DefinitionId,DefinitionType,Gate,Position,DueUnixMs,Current,
                RunId,ProcessStatus,Verdict,SkipRequested,Summary FROM BoardLaunchEvents
            WHERE ($project IS NULL OR ProjectPath=$project{ProjectPathCollation}) AND ($card IS NULL OR CardId=$card)
                AND ($card IS NOT NULL OR Current=1 OR RunId IS NOT NULL AND ProcessStatus IN ('queued','claimed','running','uncertain'))
            ORDER BY DueUnixMs,EntryId,Position;
            """;
        cmd.Parameters.AddWithValue("$project", project is null ? DBNull.Value : NormalizeProjectPath(project));
        cmd.Parameters.AddWithValue("$card", (object?)cardId ?? DBNull.Value);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        List<BoardLaunchEvent> events = [];
        while (await r.ReadAsync(ct)) events.Add(new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
            r.IsDBNull(4) ? null : r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7), r.GetInt32(8),
            DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(9)).UtcDateTime, r.GetBoolean(10),
            r.IsDBNull(11) ? null : r.GetString(11), r.GetString(12), r.IsDBNull(13) ? null : r.GetString(13),
            r.GetBoolean(14), r.IsDBNull(15) ? null : r.GetString(15)));
        return events;
    }

    public async Task<bool> AcknowledgeLaunchEventAsync(string id, string runId, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        await using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE BoardLaunchEvents SET RunId=$run WHERE Id=$id AND (RunId IS NULL OR RunId=$run);";
        cmd.Parameters.AddWithValue("$run", runId); cmd.Parameters.AddWithValue("$id", id);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }
    public async Task<bool> UpdateLaunchEventAsync(string id, string runId, string processStatus, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        await using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE BoardLaunchEvents SET ProcessStatus=$status WHERE Id=$id AND RunId=$run;";
        cmd.Parameters.AddWithValue("$status", processStatus); cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$run", runId);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }
    public async Task<bool> RetryLaunchEventAsync(string id, string previousRun, string newRun, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            UPDATE BoardLaunchEvents SET RunId=$new,ProcessStatus='queued',Verdict=NULL,Summary=NULL
            WHERE Id=$id AND RunId=$previous AND Current=1 AND SkipRequested=0
                AND (COALESCE(Verdict,'')<>'passed' OR ProcessStatus IN ('failed','cancelled','timed-out','interrupted'));
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$previous", previousRun);
        cmd.Parameters.AddWithValue("$new", newRun);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }
    public async Task<bool> ReportLaunchEventAsync(string project, string cardId, string id, string verdict, string summary,
        string? sessionId, string? reviewId, bool userSkip = false, CancellationToken ct = default, string? expectedRunId = null)
    {
        if (verdict is not ("passed" or "failed" or "reviewing" or "fixing" or "skipped") || verdict == "skipped" && !userSkip)
            throw new BoardValidationException("Unsupported lane verdict.");
        await using var db = await OpenAsync(ct);
        await using var tx = db.BeginTransaction(deferred: false);
        if (await ReadCardAsync(db, tx, NormalizeProjectPath(project), cardId, ct) is not { } card) return false;
        await RequireWritableBoardAsync(db, tx, card.BoardId, ct);
        await using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE BoardLaunchEvents SET Verdict=$verdict,Summary=$summary,SkipRequested=$skip
            WHERE Id=$id AND CardId=$card AND Current=1
                AND (COALESCE(Verdict,'') NOT IN ('passed','skipped') OR $skip=1)
                AND ($run IS NULL OR RunId=$run);
            """;
        cmd.Parameters.AddWithValue("$verdict", verdict); cmd.Parameters.AddWithValue("$summary", summary);
        cmd.Parameters.AddWithValue("$run", (object?)expectedRunId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$skip", userSkip); cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$card", cardId);
        if (await cmd.ExecuteNonQueryAsync(ct) != 1) return false;
        cmd.CommandText = "INSERT INTO BoardLaunchReports(EventId,Verdict,Summary,SessionId,ReviewId,CreatedUTC) VALUES($id,$verdict,$summary,$session,$review,$now);";
        cmd.Parameters.AddWithValue("$session", (object?)sessionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$review", (object?)reviewId ?? DBNull.Value); cmd.Parameters.AddWithValue("$now", ToDb(DateTime.UtcNow));
        await cmd.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }
}
