using Microsoft.Data.Sqlite;
using VibeRails.DB;

namespace VibeRails.Data.Sqlite;

/// <summary>Short SQLite leases keep remote title calls outside capture and database transactions.</summary>
public sealed class ConversationTitleStore : IConversationTitleStore
{
    private readonly string _connectionString;

    /// <summary>Uses the normal state database and its automatic additive schema setup.</summary>
    public ConversationTitleStore(string connectionString)
    {
        _connectionString = connectionString;
        StateDatabaseSchema.Ensure(connectionString);
    }

    internal const string EnqueueSql = """
        INSERT OR IGNORE INTO ConversationTitleRequests
            (SessionId, UserInputId, RetryAfterUTC, MessageCount, CharacterCount, LatestInputId)
        SELECT s.Id, $inputId, $now, 1, $characters, $inputId FROM Sessions s
        WHERE s.Id=$sessionId AND s.ConversationTitleEligible=1 AND COALESCE(s.SessionDisplayName,'')=''
            AND s.JobRunId IS NULL AND COALESCE(s.ParentSessionId,'')='' AND lower(s.Cli)<>'shell'
            AND NOT EXISTS(SELECT 1 FROM UserInputs u WHERE u.SessionId=s.Id AND u.Id<>$inputId);
        UPDATE ConversationTitleRequests
        SET MessageCount=MessageCount+1, CharacterCount=CharacterCount+$characters, LatestInputId=$inputId
        WHERE SessionId=$sessionId AND UserInputId<>$inputId AND Cancelled=0 AND GenerationCount<2;
        """;

    // Explicit naming, including a rename to the same text, cancels an in-flight automatic title.
    internal const string CancelSql = """
        UPDATE Sessions SET ConversationTitleEligible=0 WHERE Id=$sessionId;
        UPDATE ConversationTitleRequests SET CompletedUTC=$now, Cancelled=1, Lease=NULL, LeaseUntilUTC=NULL
        WHERE SessionId=$sessionId AND Cancelled=0;
        """;

    public async Task<ConversationTitleWork?> ClaimAsync(DateTime now, CancellationToken ct, string? settingsFingerprint = null)
    {
        using var db = SqliteConnectionFactory.Open(_connectionString);
        // Every root runs this every few seconds, so find a candidate with a plain WAL read and
        // take the writer lock only to claim one. Idle ticks must not touch the write lock.
        using var select = db.CreateCommand();
        select.CommandText = """
            SELECT q.SessionId, COALESCE(s.SessionDisplayName,''),
                CASE WHEN $fingerprint IS NOT NULL AND q.SettingsFingerprint IS NOT $fingerprint THEN 0 ELSE q.Attempts END,
                (s.JobRunId IS NULL AND COALESCE(s.ParentSessionId,'')='' AND lower(s.Cli)<>'shell' AND
                    (COALESCE(s.SessionDisplayName,'')='' OR s.SessionDisplayName=substr(u.InputText,1,120)
                        OR COALESCE(s.SessionDisplayName=q.LastTitle,0))), q.LatestInputId, q.GenerationCount
            FROM ConversationTitleRequests q
            JOIN Sessions s ON s.Id=q.SessionId JOIN UserInputs u ON u.Id=q.UserInputId
            WHERE q.Cancelled=0 AND q.GenerationCount<2
                AND ((q.GenerationCount=0 AND (q.MessageCount>=2 OR q.CharacterCount>=300))
                    OR (q.GenerationCount=1 AND q.LatestInputId>q.LastInputId
                        AND (q.MessageCount>=10 OR q.CharacterCount>=750)))
                AND (q.RetryAfterUTC<=$now OR ($fingerprint IS NOT NULL AND q.SettingsFingerprint IS NOT $fingerprint))
                AND NOT EXISTS(SELECT 1 FROM ConversationTitleApiBackoff b
                    WHERE b.Fingerprint=$fingerprint AND b.RetryAfterUTC>$now)
                AND (q.LeaseUntilUTC IS NULL OR q.LeaseUntilUTC<=$now)
            ORDER BY q.RetryAfterUTC, q.UserInputId LIMIT 1;
            """;
        select.Parameters.AddWithValue("$now", now.ToString("O"));
        select.Parameters.AddWithValue("$fingerprint", (object?)settingsFingerprint ?? DBNull.Value);
        ConversationTitleWork work;
        using (var reader = await select.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) return null;
            work = new(reader.GetString(0), reader.GetString(1),
                Guid.NewGuid().ToString("N"), reader.GetInt32(2) + 1, reader.GetBoolean(3), reader.GetInt64(4), reader.GetInt32(5));
        }
        // The guard repeats the due/lease conditions, so a root that claimed, deferred or completed
        // the row since the read makes this match nothing; the next tick tries again.
        using var claim = db.CreateCommand();
        claim.CommandText = """
            UPDATE ConversationTitleRequests SET Lease=$lease, LeaseUntilUTC=$until, Attempts=$attempts,
                SettingsFingerprint=$fingerprint
            WHERE SessionId=$sessionId AND Cancelled=0 AND GenerationCount=$generation
                AND (LeaseUntilUTC IS NULL OR LeaseUntilUTC<=$now)
                AND (RetryAfterUTC<=$now OR ($fingerprint IS NOT NULL AND SettingsFingerprint IS NOT $fingerprint));
            """;
        claim.Parameters.AddWithValue("$lease", work.Lease);
        claim.Parameters.AddWithValue("$until", now.AddMinutes(2).ToString("O"));
        claim.Parameters.AddWithValue("$now", now.ToString("O"));
        claim.Parameters.AddWithValue("$sessionId", work.SessionId);
        claim.Parameters.AddWithValue("$generation", work.Generation);
        claim.Parameters.AddWithValue("$fingerprint", (object?)settingsFingerprint ?? DBNull.Value);
        claim.Parameters.AddWithValue("$attempts", work.Attempts);
        return await claim.ExecuteNonQueryAsync(ct) == 1 ? work : null;
    }

    public async Task<bool> CompleteAsync(ConversationTitleWork work, string? title, DateTime now, CancellationToken ct)
    {
        using var db = SqliteConnectionFactory.Open(_connectionString);
        using var tx = db.BeginTransaction(deferred: false);
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE Sessions SET SessionDisplayName=$title
            WHERE Id=$sessionId AND $title IS NOT NULL
                AND JobRunId IS NULL AND COALESCE(ParentSessionId,'')='' AND lower(Cli)<>'shell'
                AND EXISTS(SELECT 1 FROM ConversationTitleRequests q WHERE q.SessionId=Sessions.Id
                    AND q.Lease=$lease AND q.LeaseUntilUTC>$now AND q.Cancelled=0 AND q.GenerationCount=$generation
                    AND (COALESCE(Sessions.SessionDisplayName,'')=$expected
                        OR ($expected='' AND Sessions.SessionDisplayName=(
                            SELECT substr(u.InputText,1,120) FROM UserInputs u WHERE u.Id=q.UserInputId))))
            RETURNING Id;
            """;
        cmd.Parameters.AddWithValue("$title", work.Eligible ? (object?)title ?? DBNull.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$expected", work.CurrentName);
        cmd.Parameters.AddWithValue("$sessionId", work.SessionId);
        cmd.Parameters.AddWithValue("$lease", work.Lease);
        cmd.Parameters.AddWithValue("$now", now.ToString("O"));
        cmd.Parameters.AddWithValue("$generation", work.Generation);
        var applied = await cmd.ExecuteScalarAsync(ct) is not null;
        cmd.CommandText = """
            UPDATE ConversationTitleRequests SET CompletedUTC=$now, Lease=NULL, LeaseUntilUTC=NULL,
                GenerationCount=GenerationCount+$applied, LastInputId=$inputId,
                LastTitle=CASE WHEN $applied=1 THEN $title ELSE LastTitle END,
                Cancelled=CASE WHEN $applied=1 THEN 0 ELSE 1 END, Attempts=0, RetryAfterUTC=$now
            WHERE SessionId=$sessionId AND Lease=$lease AND LeaseUntilUTC>$now AND Cancelled=0 AND GenerationCount=$generation;
            """;
        cmd.Parameters.AddWithValue("$applied", applied ? 1 : 0);
        cmd.Parameters.AddWithValue("$inputId", work.InputId);
        await cmd.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return applied;
    }

    public async Task DeferAsync(ConversationTitleWork work, DateTime retryAt, CancellationToken ct, string? settingsFingerprint = null)
    {
        using var db = SqliteConnectionFactory.Open(_connectionString);
        using var tx = db.BeginTransaction(deferred: false);
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE ConversationTitleRequests SET RetryAfterUTC=$retry, Lease=NULL, LeaseUntilUTC=NULL
            WHERE SessionId=$sessionId AND Lease=$lease AND Cancelled=0;
            """;
        cmd.Parameters.AddWithValue("$retry", retryAt.ToString("O"));
        cmd.Parameters.AddWithValue("$sessionId", work.SessionId);
        cmd.Parameters.AddWithValue("$lease", work.Lease);
        var changed = await cmd.ExecuteNonQueryAsync(ct);
        if (changed > 0 && settingsFingerprint is not null)
        {
            cmd.CommandText = """
                INSERT INTO ConversationTitleApiBackoff(Fingerprint,RetryAfterUTC) VALUES($fingerprint,$retry)
                ON CONFLICT(Fingerprint) DO UPDATE SET RetryAfterUTC=MAX(RetryAfterUTC,excluded.RetryAfterUTC);
                """;
            cmd.Parameters.AddWithValue("$fingerprint", settingsFingerprint);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task<string?> GetChangeStampAsync(CancellationToken ct)
    {
        using var db = SqliteConnectionFactory.Open(_connectionString);
        using var command = db.CreateCommand();
        command.CommandText = "SELECT CompletedUTC FROM ConversationTitleRequests WHERE LastTitle IS NOT NULL ORDER BY CompletedUTC DESC LIMIT 1";
        return await command.ExecuteScalarAsync(ct) as string;
    }
}
