using VibeRails.DTOs;

namespace VibeRails.DB;

public partial class Repository
{
    public async Task<bool> QueueSessionShareUploadAsync(string sessionId, string keyFingerprint,
        DateTime requestedUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO SessionShareUploads(SessionId, KeyFingerprint, RequestedUTC)
            SELECT Id, $key, $now FROM Sessions WHERE Id = $id
            ON CONFLICT(SessionId, KeyFingerprint) DO UPDATE SET
                RequestedUTC = excluded.RequestedUTC, CompletedUTC = NULL;
            """;
        cmd.Parameters.AddWithValue("$id", sessionId);
        cmd.Parameters.AddWithValue("$key", keyFingerprint);
        cmd.Parameters.AddWithValue("$now", requestedUtc.ToUniversalTime().ToString("O"));
        if (await cmd.ExecuteNonQueryAsync(cancellationToken) != 1) return false;
        cmd.CommandText = "UPDATE Sessions SET ExportAttempts = 0, ExportNextAttemptUTC = NULL WHERE Id = $id;";
        await cmd.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> EnsureSessionShareUploadAsync(string sessionId, string keyFingerprint,
        DateTime requestedUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        // The hosted response confirms this recording is missing. New requests and previously
        // completed requests may restart; repeated refreshes of pending work preserve backoff.
        cmd.CommandText = """
            INSERT INTO SessionShareUploads(SessionId, KeyFingerprint, RequestedUTC)
            SELECT Id, $key, $now FROM Sessions WHERE Id = $id
            ON CONFLICT(SessionId, KeyFingerprint) DO UPDATE SET
                RequestedUTC = excluded.RequestedUTC, CompletedUTC = NULL
            WHERE SessionShareUploads.CompletedUTC IS NOT NULL;
            """;
        cmd.Parameters.AddWithValue("$id", sessionId);
        cmd.Parameters.AddWithValue("$key", keyFingerprint);
        cmd.Parameters.AddWithValue("$now", requestedUtc.ToUniversalTime().ToString("O"));
        if (await cmd.ExecuteNonQueryAsync(cancellationToken) == 1)
        {
            cmd.CommandText = "UPDATE Sessions SET ExportAttempts = 0, ExportNextAttemptUTC = NULL WHERE Id = $id;";
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        cmd.CommandText = "SELECT 1 FROM SessionShareUploads WHERE SessionId = $id AND KeyFingerprint = $key;";
        var exists = await cmd.ExecuteScalarAsync(cancellationToken) is not null;
        await transaction.CommitAsync(cancellationToken);
        return exists;
    }

    public async Task<UnexportedSessionRef?> GetNextSharedSessionAsync(string keyFingerprint,
        DateTime nowUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT s.Id, s.ExportAttempts FROM SessionShareUploads q JOIN Sessions s ON s.Id = q.SessionId
            WHERE q.KeyFingerprint = $key AND q.CompletedUTC IS NULL
              AND s.EndedUTC IS NOT NULL AND s.EndedUTC <= $now
              AND (s.ExportNextAttemptUTC IS NULL OR s.ExportNextAttemptUTC <= $now)
            ORDER BY q.RequestedUTC, s.Id LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$key", keyFingerprint);
        cmd.Parameters.AddWithValue("$now", nowUtc.ToUniversalTime().ToString("O"));
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new UnexportedSessionRef(reader.GetString(0), reader.GetInt32(1)) : null;
    }

    public async Task<bool> CanExportSessionToKeyAsync(string sessionId, string keyFingerprint,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT 1 FROM Sessions s WHERE s.Id = $id AND s.EndedUTC IS NOT NULL AND (
                EXISTS (SELECT 1 FROM SessionShareUploads q WHERE q.SessionId = s.Id
                    AND q.CompletedUTC IS NULL AND q.KeyFingerprint = $key)
                OR (s.ExportedUTC IS NULL AND NOT EXISTS (SELECT 1 FROM SessionShareUploads q
                    WHERE q.SessionId = s.Id AND q.CompletedUTC IS NULL))) LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$id", sessionId);
        cmd.Parameters.AddWithValue("$key", keyFingerprint);
        return await cmd.ExecuteScalarAsync(cancellationToken) is not null;
    }

    public async Task<bool> AcknowledgeSessionExportAsync(string keyFingerprint, string sessionId,
        DateTime exportedUtc, string? proxyCoverage, long? proxyMaxRowId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        // Preserve the original retention evidence on a re-export: the earlier envelope may
        // contain proxy rows no longer present locally. Never invent evidence from a later copy.
        cmd.CommandText = """
            UPDATE Sessions SET
                ExportedProxyCoverage = CASE WHEN ExportedUTC IS NULL THEN $coverage ELSE ExportedProxyCoverage END,
                ExportedProxyMaxRowId = CASE WHEN ExportedUTC IS NULL THEN $maxRow ELSE ExportedProxyMaxRowId END,
                ExportedUTC = COALESCE(ExportedUTC, $now)
            WHERE Id = $id AND EndedUTC IS NOT NULL AND (ExportedUTC IS NULL OR EXISTS (
                SELECT 1 FROM SessionShareUploads WHERE SessionId = $id
                    AND KeyFingerprint = $key AND CompletedUTC IS NULL));
            """;
        cmd.Parameters.AddWithValue("$id", sessionId);
        cmd.Parameters.AddWithValue("$key", keyFingerprint);
        cmd.Parameters.AddWithValue("$now", exportedUtc.ToUniversalTime().ToString("O"));
        cmd.Parameters.AddWithValue("$coverage", (object?)proxyCoverage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$maxRow", (object?)proxyMaxRowId ?? DBNull.Value);
        if (await cmd.ExecuteNonQueryAsync(cancellationToken) != 1) return false;
        cmd.CommandText = """
            UPDATE SessionShareUploads SET CompletedUTC = $now
            WHERE SessionId = $id AND KeyFingerprint = $key AND CompletedUTC IS NULL;
            """;
        await cmd.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
