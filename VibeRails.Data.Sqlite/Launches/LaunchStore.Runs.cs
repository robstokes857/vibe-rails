using System.Text.Json;
using Microsoft.Data.Sqlite;
using VibeRails.DTOs;
using VibeRails.Launches;

namespace VibeRails.Data.Sqlite;

public sealed partial class LaunchStore
{
    private const string RunColumns = "Id,Sequence,DefinitionId,DefinitionType,ProjectPath,OccurrenceId,Snapshot,Status,QueuedUTC,SourceRunId,ClaimToken,OwnerPid,OwnerStartedUTC,ClaimedUTC,StartedUTC,EndedUTC,CancelRequested,SessionId,TabId,ResolvedPrompt,ResolvedWorkingDirectory,ExitCode,Error,EvidenceId,Verdict,DeadlineUTC,ExecutionPid,ExecutionStartedUTC";
    private static LaunchRun ReadRun(SqliteDataReader r) => new(r.GetString(0), r.GetInt64(1), r.GetString(2),
        r.GetString(3), r.GetString(4), r.GetString(5),
        JsonSerializer.Deserialize(r.GetString(6), StorageJsonSerializerContext.Default.LaunchSnapshot)!,
        r.GetString(7), Date(r.GetString(8)), Text(r, 9), Text(r, 10), Number(r, 11),
        Time(r, 12), Time(r, 13), Time(r, 14), Time(r, 15), r.GetBoolean(16), Text(r, 17),
        Text(r, 18), Text(r, 19), Text(r, 20), Number(r, 21), Text(r, 22), Text(r, 23), Text(r, 24), Time(r, 25), Number(r, 26), Time(r, 27));
    private static string? Text(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static int? Number(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt32(i);
    private static DateTime? Time(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : Date(r.GetString(i));

    public async Task<LaunchRun> EnqueueAsync(LaunchSnapshot snapshot, string? sourceRunId = null, CancellationToken ct = default)
    {
        LaunchValidation.ValidateSnapshot(snapshot);
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: false);
        // Delivery identity wins over a changed definition: a lost acknowledgement can always find its original run.
        using (var existing = Command(db, $"SELECT {RunColumns} FROM Runs WHERE ProjectPath=$project AND OccurrenceId=$event;",
            tx, ("$project", snapshot.Context.ProjectPath), ("$event", snapshot.Context.OccurrenceId)))
        using (var reader = await existing.ExecuteReaderAsync(ct))
            if (await reader.ReadAsync(ct)) return ReadRun(reader);

        if ((snapshot.Preset is null) == (snapshot.Action is null)) throw new LaunchValidationException("Choose exactly one preset or Action.");
        if (sourceRunId is not null)
        {
            using var source = Command(db, "SELECT Snapshot,Status FROM Runs WHERE Id=$id AND ProjectPath=$project;", tx,
                ("$id", sourceRunId), ("$project", snapshot.Context.ProjectPath));
            using var reader = await source.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct) || reader.GetString(1) is "queued" or "claimed" or "running" or "uncertain")
                throw new LaunchConflictException("Only a finished run can be retried.");
            var original = JsonSerializer.Deserialize(reader.GetString(0), StorageJsonSerializerContext.Default.LaunchSnapshot)!;
            // The caller can supply a fresh occurrence identity, but cannot change any retry input.
            snapshot = original with { Context = original.Context with { OccurrenceId = snapshot.Context.OccurrenceId } };
            LaunchValidation.ValidateSnapshot(snapshot);
        }
        // Validate the definition the run will actually execute: a retry's is the source run's, swapped
        // in above, never whatever the caller passed in (VIBE-296).
        await RequireAvailable(db, tx, snapshot.DefinitionType, snapshot.DefinitionId,
            sourceRunId is null ? snapshot.Revision : null,
            automatic: sourceRunId is null && snapshot.Context.TriggerKind != "manual", ct);
        var id = "run_" + Guid.NewGuid().ToString("N");
        using (var cmd = Command(db, """
            INSERT INTO Runs(Id,DefinitionId,DefinitionType,ProjectPath,OccurrenceId,Snapshot,Status,QueuedUTC,SourceRunId)
            VALUES($id,$definition,$type,$project,$event,$snapshot,'queued',$now,$source);
            """, tx, ("$id", id), ("$definition", snapshot.DefinitionId), ("$type", snapshot.DefinitionType),
            ("$project", snapshot.Context.ProjectPath), ("$event", snapshot.Context.OccurrenceId),
            ("$snapshot", JsonSerializer.Serialize(snapshot, StorageJsonSerializerContext.Default.LaunchSnapshot)),
            ("$now", Stamp(DateTime.UtcNow)), ("$source", sourceRunId)))
            await cmd.ExecuteNonQueryAsync(ct);
        tx.Commit();
        return (await GetRunAsync(id, ct))!;
    }

    public async Task<LaunchRun?> FindOccurrenceAsync(string project, string occurrence, CancellationToken ct = default)
    {
        using var db = Open();
        using var cmd = Command(db, $"SELECT {RunColumns} FROM Runs WHERE ProjectPath=$project AND OccurrenceId=$occurrence;",
            null, ("$project", project), ("$occurrence", occurrence));
        using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadRun(reader) : null;
    }

    public async Task<LaunchRun?> GetRunAsync(string id, CancellationToken ct = default)
    {
        using var db = Open();
        using var cmd = Command(db, $"SELECT {RunColumns} FROM Runs WHERE Id=$id;", null, ("$id", id));
        using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadRun(reader) : null;
    }

    public async Task<LaunchRunPage> ListRunsAsync(string projectPath, string? definitionId = null,
        long? before = null, int limit = 50, CancellationToken ct = default)
    {
        if (limit is < 1 or > 100 || before is <= 0) throw new LaunchValidationException("Use a positive cursor and a page size between 1 and 100.");
        using var db = Open();
        using var cmd = Command(db, $"""
            SELECT {RunColumns} FROM Runs WHERE ProjectPath=$project AND ($definition IS NULL OR DefinitionId=$definition)
            AND ($before IS NULL OR Sequence<$before) ORDER BY Sequence DESC LIMIT $limit;
            """, null, ("$project", projectPath), ("$definition", definitionId), ("$before", before), ("$limit", limit + 1));
        using var reader = await cmd.ExecuteReaderAsync(ct);
        List<LaunchRun> runs = [];
        while (await reader.ReadAsync(ct)) runs.Add(ReadRun(reader));
        var more = runs.Count > limit;
        if (more) runs.RemoveAt(limit);
        return new(runs, more ? runs[^1].Sequence : null);
    }

    public async Task<IReadOnlyList<LaunchRun>> GetActiveRunsAsync(CancellationToken ct = default)
    {
        using var db = Open();
        using var cmd = Command(db, $"SELECT {RunColumns} FROM Runs WHERE Status IN ('queued','claimed','running','uncertain') ORDER BY Sequence;");
        using var reader = await cmd.ExecuteReaderAsync(ct);
        List<LaunchRun> runs = [];
        while (await reader.ReadAsync(ct)) runs.Add(ReadRun(reader));
        return runs;
    }

    public async Task<IReadOnlyList<LaunchRun>> GetBoardRunsAsync(string project, string cardId, int offset = 0, string? runId = null, string? sessionId = null, CancellationToken ct = default)
    {
        if (offset is < 0 or > 1_000_000) throw new LaunchValidationException("Invalid run offset.");
        using var db = Open();
        using var cmd = Command(db, $"""
            SELECT {RunColumns} FROM Runs WHERE ProjectPath=$project AND json_extract(Snapshot,'$.context.cardId')=$card
                AND ($run IS NULL OR Id=$run) AND ($session IS NULL OR SessionId=$session)
            ORDER BY Sequence DESC LIMIT 50 OFFSET $offset;
            """, null, ("$project", project), ("$card", cardId), ("$run", runId), ("$session", sessionId), ("$offset", offset));
        using var reader = await cmd.ExecuteReaderAsync(ct);
        List<LaunchRun> runs = [];
        while (await reader.ReadAsync(ct)) runs.Add(ReadRun(reader));
        return runs;
    }

    public async Task<bool> RecordVerdictAsync(LaunchClaim claim, string verdict, string? evidenceId, CancellationToken ct = default)
    {
        using var db = Open();
        using var cmd = Command(db, "UPDATE Runs SET Verdict=$verdict,EvidenceId=$evidence WHERE Id=$id AND ClaimToken=$token AND Status='running';",
            null, ("$verdict", verdict), ("$evidence", evidenceId), ("$id", claim.RunId), ("$token", claim.Token));
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> RecordProcessAsync(LaunchClaim claim, int pid, DateTime startedUtc, CancellationToken ct = default)
    {
        using var db = Open();
        using var cmd = Command(db, "UPDATE Runs SET ExecutionPid=$pid,ExecutionStartedUTC=$started WHERE Id=$id AND ClaimToken=$token AND Status='running';",
            null, ("$pid", pid), ("$started", Stamp(startedUtc)), ("$id", claim.RunId), ("$token", claim.Token));
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<LaunchClaim?> TryClaimAsync(string id, int capacity, int ownerPid, DateTime ownerStartedUtc, CancellationToken ct = default)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: false);
        var token = Guid.NewGuid().ToString("N");
        using var cmd = Command(db, """
            UPDATE Runs SET Status='claimed',ClaimToken=$token,OwnerPid=$pid,OwnerStartedUTC=$started,ClaimedUTC=$now
            WHERE Id=$id AND Status='queued' AND CancelRequested=0
                AND (SELECT count(*) FROM Runs WHERE Status IN ('claimed','running','uncertain'))<$capacity
                AND NOT EXISTS(SELECT 1 FROM Runs active WHERE active.DefinitionId=Runs.DefinitionId
                    AND active.ProjectPath=Runs.ProjectPath AND active.Status IN ('claimed','running','uncertain'));
            """, tx, ("$token", token), ("$pid", ownerPid), ("$started", Stamp(ownerStartedUtc)),
            ("$now", Stamp(DateTime.UtcNow)), ("$id", id), ("$capacity", capacity));
        if (await cmd.ExecuteNonQueryAsync(ct) != 1) return null;
        tx.Commit();
        return new(id, token);
    }

    public async Task<bool> SetTabAsync(LaunchClaim claim, string tabId, CancellationToken ct = default)
    {
        using var db = Open();
        using var cmd = Command(db, "UPDATE Runs SET TabId=$tab WHERE Id=$id AND ClaimToken=$token AND Status='claimed' AND TabId IS NULL AND CancelRequested=0;",
            null, ("$tab", tabId), ("$id", claim.RunId), ("$token", claim.Token));
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> StartAsync(LaunchClaim claim, string? sessionId, string? prompt, string workingDirectory,
        int ownerPid, DateTime ownerStartedUtc, DateTime? deadlineUtc = null, CancellationToken ct = default)
    {
        using var db = Open();
        using var cmd = Command(db, """
            UPDATE Runs SET Status='running',StartedUTC=$now,SessionId=$session,ResolvedPrompt=$prompt,
            ResolvedWorkingDirectory=$directory,OwnerPid=$pid,OwnerStartedUTC=$owner,DeadlineUTC=$deadline
            WHERE Id=$id AND ClaimToken=$token AND Status='claimed' AND CancelRequested=0;
            """, null, ("$now", Stamp(DateTime.UtcNow)), ("$session", sessionId), ("$prompt", prompt),
            ("$directory", workingDirectory), ("$pid", ownerPid), ("$owner", Stamp(ownerStartedUtc)),
            ("$deadline", deadlineUtc is { } deadline ? Stamp(deadline) : null), ("$id", claim.RunId), ("$token", claim.Token));
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> CompleteUnstartedAsync(LaunchClaim claim, string status, string? error = null, CancellationToken ct = default)
    {
        if (status is not ("failed" or "cancelled" or "uncertain"))
            throw new ArgumentOutOfRangeException(nameof(status));
        using var db = Open();
        using var cmd = Command(db, """
            UPDATE Runs SET Status=CASE WHEN CancelRequested=1 AND $status<>'uncertain' THEN 'cancelled' ELSE $status END,
                EndedUTC=CASE WHEN $status='uncertain' THEN NULL ELSE $now END, Error=$error
            WHERE Id=$id AND ClaimToken=$token AND Status='claimed' AND StartedUTC IS NULL;
            """, null, ("$status", status), ("$now", Stamp(DateTime.UtcNow)), ("$error", error),
            ("$id", claim.RunId), ("$token", claim.Token));
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> CompleteAsync(LaunchClaim claim, string status, int? exitCode = null, string? error = null,
        string? evidenceId = null, string? verdict = null, CancellationToken ct = default)
    {
        if (status is not ("succeeded" or "failed" or "cancelled" or "timed-out" or "interrupted" or "uncertain"))
            throw new ArgumentOutOfRangeException(nameof(status));
        using var db = Open();
        using var cmd = Command(db, """
            UPDATE Runs SET Status=CASE WHEN CancelRequested=1 AND $status<>'uncertain' THEN 'cancelled' ELSE $status END,
                EndedUTC=CASE WHEN $status='uncertain' THEN NULL ELSE $now END,
                ExitCode=$exit,Error=$error,EvidenceId=COALESCE($evidence,EvidenceId),Verdict=COALESCE($verdict,Verdict)
            WHERE Id=$id AND ClaimToken=$token AND Status IN ('claimed','running','uncertain')
                AND ($status<>'uncertain' OR Status='claimed' OR DefinitionType='action' AND ExecutionPid IS NULL);
            """, null, ("$status", status), ("$now", Stamp(DateTime.UtcNow)), ("$exit", exitCode),
            ("$error", error), ("$evidence", evidenceId), ("$verdict", verdict), ("$id", claim.RunId), ("$token", claim.Token));
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> CancelAsync(string id, string projectPath, CancellationToken ct = default)
    {
        using var db = Open();
        using var cmd = Command(db, """
            UPDATE Runs SET CancelRequested=1, Status=CASE WHEN Status='queued' THEN 'cancelled' ELSE Status END,
            EndedUTC=CASE WHEN Status='queued' THEN $now ELSE EndedUTC END
            WHERE Id=$id AND ProjectPath=$project AND Status IN ('queued','claimed','running','uncertain');
            """, null, ("$now", Stamp(DateTime.UtcNow)), ("$id", id), ("$project", projectPath));
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<IReadOnlyList<string>> GetOpenProjectRootsAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        using var db = Open();
        // A lease taken by an older build has no recorded root; its key is still a usable answer.
        using var cmd = Command(db, """
            SELECT COALESCE(p.ProjectRoot, substr(l.Name,9)) FROM RuntimeLeases l
            LEFT JOIN ProjectRootPaths p ON p.ProjectKey=substr(l.Name,9)
            WHERE l.Name LIKE 'project:%' AND l.UntilUTC>$now;
            """, null, ("$now", Stamp(nowUtc)));
        using var reader = await cmd.ExecuteReaderAsync(ct);
        List<string> roots = [];
        while (await reader.ReadAsync(ct)) roots.Add(reader.GetString(0));
        return roots;
    }

    private const string LeaseSql = """
        INSERT INTO RuntimeLeases VALUES($name,$owner,$until) ON CONFLICT(Name) DO UPDATE SET
        Owner=excluded.Owner,UntilUTC=excluded.UntilUTC WHERE RuntimeLeases.Owner=$owner OR RuntimeLeases.UntilUTC<=$now;
        """;

    public async Task<bool> TryLeaseAsync(string name, string owner, DateTime nowUtc, TimeSpan duration, CancellationToken ct = default)
    {
        using var db = Open();
        using var cmd = Command(db, LeaseSql, null, ("$name", name), ("$owner", owner), ("$until", Stamp(nowUtc + duration)), ("$now", Stamp(nowUtc)));
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> TryLeaseProjectAsync(string projectKey, string projectRoot, string owner, DateTime nowUtc, TimeSpan duration, CancellationToken ct = default)
    {
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: false);
        using (var lease = Command(db, LeaseSql, tx, ("$name", "project:" + projectKey), ("$owner", owner),
            ("$until", Stamp(nowUtc + duration)), ("$now", Stamp(nowUtc))))
            if (await lease.ExecuteNonQueryAsync(ct) != 1) return false;
        // Unchanged on every tick after the first, so the upsert's WHERE keeps it from rewriting the row.
        using (var path = Command(db, """
            INSERT INTO ProjectRootPaths(ProjectKey,ProjectRoot) VALUES($key,$root) ON CONFLICT(ProjectKey) DO UPDATE SET
            ProjectRoot=excluded.ProjectRoot WHERE ProjectRootPaths.ProjectRoot<>excluded.ProjectRoot;
            """, tx, ("$key", projectKey), ("$root", projectRoot)))
            await path.ExecuteNonQueryAsync(ct);
        tx.Commit();
        return true;
    }

    public async Task SaveCheckEvidenceAsync(string runId, VibeRails.Services.Board.BoardCheckRecord check, CancellationToken ct = default)
    {
        using var db = Open();
        using var cmd = Command(db, "INSERT OR IGNORE INTO ActionEvidence(RunId,Payload) VALUES($run,$payload);", null,
            ("$run", runId), ("$payload", JsonSerializer.Serialize(check, StorageJsonSerializerContext.Default.BoardCheckRecord)));
        await cmd.ExecuteNonQueryAsync(ct);
    }
    public async Task<VibeRails.Services.Board.BoardCheckRecord?> GetCheckEvidenceAsync(string runId, CancellationToken ct = default)
    {
        using var db = Open();
        using var cmd = Command(db, "SELECT Payload FROM ActionEvidence WHERE RunId=$run;", null, ("$run", runId));
        return await cmd.ExecuteScalarAsync(ct) is string json
            ? JsonSerializer.Deserialize(json, StorageJsonSerializerContext.Default.BoardCheckRecord) : null;
    }
}
