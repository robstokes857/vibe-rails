using System.Globalization;
using Microsoft.Data.Sqlite;
using VibeRails.DB;

namespace VibeRails.Data.Sqlite;

/// <summary>
/// Review requests and decisions in the normal state database (VB-50). Additive component
/// <c>review-requests/1</c> (the two tables), <c>/2</c> (the hosted copy's reference, key
/// fingerprint, expiry and receipts as nullable columns), <c>/3</c> (the session-queue receipt),
/// <c>/4</c> (a decision's line comments as JSON) and <c>/5</c> (when a send was attempted, written
/// before the hosted POST); older versions ignore what they do not know.
/// </summary>
public sealed class ReviewRequestStore : IReviewRequestStore
{
    private readonly string _connectionString;

    /// <summary>Uses the normal state database with automatic additive schema setup.</summary>
    public ReviewRequestStore(string connectionString)
    {
        _connectionString = connectionString;
        StateDatabaseSchema.Ensure(connectionString);
    }

    /// <inheritdoc />
    public async Task AddAsync(ReviewRequestRecord request, CancellationToken ct = default)
    {
        using var db = SqliteConnectionFactory.Open(_connectionString);
        using var command = db.CreateCommand();
        await InsertAsync(command, request, ct);
    }

    /// <inheritdoc />
    public async Task<ReviewRequestRecord?> FindOpenUnsentAsync(string projectPath, string treeId, CancellationToken ct = default)
    {
        using var db = SqliteConnectionFactory.Open(_connectionString);
        using var command = db.CreateCommand();
        return await FindOpenUnsentAsync(command, projectPath, treeId, ct);
    }

    /// <inheritdoc />
    public async Task<ReviewRequestRecord> AddOrGetOpenUnsentAsync(ReviewRequestRecord request, CancellationToken ct = default)
    {
        using var db = SqliteConnectionFactory.Open(_connectionString);
        using var transaction = db.BeginTransaction(System.Data.IsolationLevel.Serializable);
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        var existing = await FindOpenUnsentAsync(command, request.ProjectPath, request.TreeId, ct);
        if (existing is not null) return existing;
        command.Parameters.Clear();
        await InsertAsync(command, request, ct);
        transaction.Commit();
        return request;
    }

    private static async Task<ReviewRequestRecord?> FindOpenUnsentAsync(SqliteCommand command, string projectPath, string treeId, CancellationToken ct)
    {
        command.CommandText = RequestColumns + """
             FROM ReviewRequests WHERE ProjectPath = $project AND TreeId = $tree
             AND Scope = 'staged' AND Status = 'open'
             AND SentUTC IS NULL AND RemoteId IS NULL AND SendAttemptedUTC IS NULL
             ORDER BY CreatedUTC DESC, Id LIMIT 1;
            """;
        command.Parameters.AddWithValue("$project", projectPath);
        command.Parameters.AddWithValue("$tree", treeId);
        using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadRequest(reader) : null;
    }

    private static async Task InsertAsync(SqliteCommand command, ReviewRequestRecord request, CancellationToken ct)
    {
        command.CommandText = """
            INSERT INTO ReviewRequests (Id, ProjectPath, Scope, TreeId, BaseCommit, CardKey, Status, BundleJson, CreatedUTC, UpdatedUTC)
            VALUES ($id, $project, $scope, $tree, $base, $card, $status, $bundle, $created, $updated);
            """;
        command.Parameters.AddWithValue("$id", request.Id);
        command.Parameters.AddWithValue("$project", request.ProjectPath);
        command.Parameters.AddWithValue("$scope", request.Scope);
        command.Parameters.AddWithValue("$tree", request.TreeId);
        command.Parameters.AddWithValue("$base", (object?)request.BaseCommit ?? DBNull.Value);
        command.Parameters.AddWithValue("$card", (object?)request.CardKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", request.Status);
        command.Parameters.AddWithValue("$bundle", request.BundleJson);
        command.Parameters.AddWithValue("$created", Stamp(request.CreatedUTC));
        command.Parameters.AddWithValue("$updated", Stamp(request.UpdatedUTC));
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <inheritdoc />
    public async Task<ReviewRequestRecord?> GetAsync(string id, CancellationToken ct = default)
    {
        using var db = SqliteConnectionFactory.Open(_connectionString);
        using var command = db.CreateCommand();
        command.CommandText = RequestColumns + " FROM ReviewRequests WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadRequest(reader) : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReviewRequestRecord>> ListAsync(string projectPath, int limit, CancellationToken ct = default)
    {
        using var db = SqliteConnectionFactory.Open(_connectionString);
        using var command = db.CreateCommand();
        command.CommandText = RequestColumns + " FROM ReviewRequests WHERE ProjectPath = $project ORDER BY CreatedUTC DESC, Id LIMIT $limit;";
        command.Parameters.AddWithValue("$project", projectPath);
        command.Parameters.AddWithValue("$limit", Math.Max(0, limit));
        using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<ReviewRequestRecord>();
        while (await reader.ReadAsync(ct)) result.Add(ReadRequest(reader));
        return result;
    }

    /// <inheritdoc />
    public async Task<bool> UpdateStatusAsync(string id, string status, DateTime updatedUtc, CancellationToken ct = default)
    {
        using var db = SqliteConnectionFactory.Open(_connectionString);
        using var command = db.CreateCommand();
        command.CommandText = "UPDATE ReviewRequests SET Status = $status, UpdatedUTC = $updated WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$updated", Stamp(updatedUtc));
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        using var db = SqliteConnectionFactory.Open(_connectionString);
        using var command = db.CreateCommand();
        // Decisions cascade through the foreign key; foreign keys are on for every connection opened here.
        command.CommandText = "DELETE FROM ReviewRequests WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    /// <inheritdoc />
    public async Task<ReviewDecisionOutcome?> RecordDecisionAsync(ReviewDecisionRecord decision, DateTime updatedUtc, CancellationToken ct = default)
    {
        using var db = SqliteConnectionFactory.Open(_connectionString);
        // BEGIN IMMEDIATE: the write lock is taken before the status is read, so a cancel that
        // lands while an approval is being verified is either seen here or waits for this commit.
        using var transaction = db.BeginTransaction(System.Data.IsolationLevel.Serializable);
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT Status FROM ReviewRequests WHERE Id = $request;";
        command.Parameters.AddWithValue("$request", decision.RequestId);
        if (await command.ExecuteScalarAsync(ct) is not string status || status == "cancelled")
            return null;

        command.CommandText = """
            INSERT INTO ReviewDecisions (RequestId, Reviewer, Decision, Comment, KeyId, Fingerprint, TreeId, SignatureBase64, DecidedUTC, VerifiedUTC, RemoteId, LineCommentsJson)
            VALUES ($request, $reviewer, $decision, $comment, $key, $fingerprint, $tree, $signature, $decided, $verified, $remote, $lineComments)
            RETURNING Id;
            """;
        command.Parameters.AddWithValue("$reviewer", decision.Reviewer);
        command.Parameters.AddWithValue("$decision", decision.Decision);
        command.Parameters.AddWithValue("$comment", (object?)decision.Comment ?? DBNull.Value);
        command.Parameters.AddWithValue("$key", (object?)decision.KeyId?.ToString("D") ?? DBNull.Value);
        command.Parameters.AddWithValue("$fingerprint", (object?)decision.Fingerprint ?? DBNull.Value);
        command.Parameters.AddWithValue("$tree", (object?)decision.TreeId ?? DBNull.Value);
        command.Parameters.AddWithValue("$signature", (object?)decision.SignatureBase64 ?? DBNull.Value);
        command.Parameters.AddWithValue("$decided", Stamp(decision.DecidedUTC));
        command.Parameters.AddWithValue("$verified", decision.VerifiedUTC is { } verified ? Stamp(verified) : DBNull.Value);
        command.Parameters.AddWithValue("$remote", (object?)decision.RemoteId ?? DBNull.Value);
        command.Parameters.AddWithValue("$lineComments", (object?)decision.LineCommentsJson ?? DBNull.Value);
        var id = (long)(await command.ExecuteScalarAsync(ct) ?? throw new InvalidOperationException("The review decision was not stored."));

        command.CommandText = """
            SELECT CASE
                WHEN EXISTS (SELECT 1 FROM ReviewDecisions WHERE RequestId = $request AND Decision = 'approved' AND VerifiedUTC IS NOT NULL) THEN 'approved'
                WHEN EXISTS (SELECT 1 FROM ReviewDecisions WHERE RequestId = $request AND Decision = 'rejected') THEN 'rejected'
                ELSE 'open' END;
            """;
        var newStatus = (string)(await command.ExecuteScalarAsync(ct) ?? "open");
        command.CommandText = "UPDATE ReviewRequests SET Status = $status, UpdatedUTC = $updated WHERE Id = $request;";
        command.Parameters.AddWithValue("$status", newStatus);
        command.Parameters.AddWithValue("$updated", Stamp(updatedUtc));
        await command.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
        return new ReviewDecisionOutcome(decision with { Id = id }, newStatus);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReviewDecisionRecord>> GetDecisionsAsync(string requestId, CancellationToken ct = default)
    {
        using var db = SqliteConnectionFactory.Open(_connectionString);
        using var command = db.CreateCommand();
        command.CommandText = DecisionColumns + " FROM ReviewDecisions d WHERE d.RequestId = $request ORDER BY d.DecidedUTC, d.Id;";
        command.Parameters.AddWithValue("$request", requestId);
        return await ReadDecisionsAsync(command, ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReviewDecisionRecord>> FindVerifiedApprovalsAsync(string treeId, CancellationToken ct = default)
    {
        using var db = SqliteConnectionFactory.Open(_connectionString);
        using var command = db.CreateCommand();
        command.CommandText = DecisionColumns + """
             FROM ReviewDecisions d JOIN ReviewRequests r ON r.Id = d.RequestId
            WHERE d.TreeId = $tree AND d.Decision = 'approved' AND d.VerifiedUTC IS NOT NULL
              AND d.SignatureBase64 IS NOT NULL AND r.Status <> 'cancelled'
            ORDER BY d.VerifiedUTC DESC, d.Id DESC;
            """;
        command.Parameters.AddWithValue("$tree", treeId);
        return await ReadDecisionsAsync(command, ct);
    }

    /// <inheritdoc />
    public async Task<string?> MarkSentAsync(string id, string remoteId, string keyFingerprint, DateTime sentUtc, DateTime expiresUtc, CancellationToken ct = default)
    {
        using var db = SqliteConnectionFactory.Open(_connectionString);
        using var command = db.CreateCommand();
        // Idempotent for the same reference (a retry of the same send) and refused for a different
        // one: two roots sending the same request get the same hosted reference back, so a mismatch
        // means a stale local copy and must not overwrite what the poller is already following. The
        // reference is stored whatever the status, because a request cancelled during its send still
        // needs it to revoke the hosted copy; the status comes back in the same statement so the
        // caller never queues uploads for one (VIBE-213, item 3).
        command.CommandText = """
            UPDATE ReviewRequests
               SET SentUTC = COALESCE(SentUTC, $sent), RemoteId = $remote, SentKeyFingerprint = $fingerprint,
                   ExpiresUTC = $expires, UpdatedUTC = $sent
             WHERE Id = $id AND (RemoteId IS NULL OR RemoteId = $remote)
            RETURNING Status;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$remote", remoteId);
        command.Parameters.AddWithValue("$fingerprint", keyFingerprint);
        command.Parameters.AddWithValue("$sent", Stamp(sentUtc));
        command.Parameters.AddWithValue("$expires", Stamp(expiresUtc));
        return await command.ExecuteScalarAsync(ct) as string;
    }

    /// <inheritdoc />
    public async Task<bool> MarkRemoteCancelledAsync(string id, DateTime cancelledUtc, CancellationToken ct = default)
    {
        using var db = SqliteConnectionFactory.Open(_connectionString);
        using var command = db.CreateCommand();
        command.CommandText = "UPDATE ReviewRequests SET RemoteCancelledUTC = COALESCE(RemoteCancelledUTC, $at) WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$at", Stamp(cancelledUtc));
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    /// <inheritdoc />
    public async Task<bool> MarkSessionsQueuedAsync(string id, DateTime queuedUtc, CancellationToken ct = default)
    {
        using var db = SqliteConnectionFactory.Open(_connectionString);
        using var command = db.CreateCommand();
        command.CommandText = "UPDATE ReviewRequests SET SessionsQueuedUTC = COALESCE(SessionsQueuedUTC, $at) WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$at", Stamp(queuedUtc));
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    /// <inheritdoc />
    public async Task<bool> MarkSendAttemptAsync(string id, string keyFingerprint, DateTime attemptedUtc, CancellationToken ct = default)
    {
        using var db = SqliteConnectionFactory.Open(_connectionString);
        using var command = db.CreateCommand();
        // One statement claims the attempt: the first attempt's time is kept, the fingerprint is
        // pinned, and a request already sent or pinned to another fingerprint is left alone (VIBE-212).
        // A same-key retry matches its own pin and may post the stored bundle again.
        command.CommandText = """
            UPDATE ReviewRequests
               SET SendAttemptedUTC = COALESCE(SendAttemptedUTC, $at), SentKeyFingerprint = $fingerprint
             WHERE Id = $id AND RemoteId IS NULL AND (SentKeyFingerprint IS NULL OR SentKeyFingerprint = $fingerprint);
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$fingerprint", keyFingerprint);
        command.Parameters.AddWithValue("$at", Stamp(attemptedUtc));
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    /// <inheritdoc />
    public async Task<bool> ClearSendAttemptAsync(string id, string keyFingerprint, DateTime attemptedUtc, DateTime updatedUtc, CancellationToken ct = default)
    {
        using var db = SqliteConnectionFactory.Open(_connectionString);
        using var command = db.CreateCommand();
        // Never touches a sent request: a concurrent same-key retry may have recorded the reference meanwhile.
        // Only the attempt the refusal answered is cleared: another root sharing state.db, or a concurrent
        // send that claimed first, keeps its own pin (VIBE-213, item 9).
        command.CommandText = """
            UPDATE ReviewRequests
               SET SendAttemptedUTC = NULL, SentKeyFingerprint = NULL, UpdatedUTC = $updated
             WHERE Id = $id AND RemoteId IS NULL AND SendAttemptedUTC = $attempted AND SentKeyFingerprint = $fingerprint;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$fingerprint", keyFingerprint);
        command.Parameters.AddWithValue("$attempted", Stamp(attemptedUtc));
        command.Parameters.AddWithValue("$updated", Stamp(updatedUtc));
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReviewRequestRecord>> ListSendAttemptsPendingAsync(CancellationToken ct = default)
    {
        using var db = SqliteConnectionFactory.Open(_connectionString);
        using var command = db.CreateCommand();
        command.CommandText = RequestColumns + """
             FROM ReviewRequests
            WHERE SendAttemptedUTC IS NOT NULL AND RemoteId IS NULL
            ORDER BY SendAttemptedUTC, Id;
            """;
        using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<ReviewRequestRecord>();
        while (await reader.ReadAsync(ct)) result.Add(ReadRequest(reader));
        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReviewRequestRecord>> ListSentPendingAsync(DateTime expiresAfterUtc, CancellationToken ct = default)
    {
        using var db = SqliteConnectionFactory.Open(_connectionString);
        using var command = db.CreateCommand();
        // Stamps are fixed-width round-trip UTC text, so the text comparison orders like the times.
        command.CommandText = RequestColumns + """
             FROM ReviewRequests
            WHERE RemoteId IS NOT NULL
              AND (Status <> 'cancelled' OR RemoteCancelledUTC IS NULL)
              AND (ExpiresUTC IS NULL OR ExpiresUTC >= $expiresAfter)
            ORDER BY SentUTC, Id;
            """;
        command.Parameters.AddWithValue("$expiresAfter", Stamp(expiresAfterUtc));
        using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<ReviewRequestRecord>();
        while (await reader.ReadAsync(ct)) result.Add(ReadRequest(reader));
        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlySet<long>> GetRemoteDecisionIdsAsync(string requestId, CancellationToken ct = default)
    {
        using var db = SqliteConnectionFactory.Open(_connectionString);
        using var command = db.CreateCommand();
        command.CommandText = "SELECT RemoteId FROM ReviewDecisions WHERE RequestId = $request AND RemoteId IS NOT NULL;";
        command.Parameters.AddWithValue("$request", requestId);
        using var reader = await command.ExecuteReaderAsync(ct);
        var result = new HashSet<long>();
        while (await reader.ReadAsync(ct)) result.Add(reader.GetInt64(0));
        return result;
    }

    private const string RequestColumns =
        "SELECT Id, ProjectPath, Scope, TreeId, BaseCommit, CardKey, Status, BundleJson, CreatedUTC, UpdatedUTC, "
        + "SentUTC, RemoteId, SentKeyFingerprint, ExpiresUTC, RemoteCancelledUTC, SessionsQueuedUTC, SendAttemptedUTC";

    private const string DecisionColumns =
        "SELECT d.Id, d.RequestId, d.Reviewer, d.Decision, d.Comment, d.KeyId, d.Fingerprint, d.TreeId, d.SignatureBase64, d.DecidedUTC, d.VerifiedUTC, d.RemoteId, d.LineCommentsJson";

    private static async Task<IReadOnlyList<ReviewDecisionRecord>> ReadDecisionsAsync(SqliteCommand command, CancellationToken ct)
    {
        using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<ReviewDecisionRecord>();
        while (await reader.ReadAsync(ct))
        {
            result.Add(new ReviewDecisionRecord(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : Guid.Parse(reader.GetString(5)),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                Parse(reader.GetString(9)),
                reader.IsDBNull(10) ? null : Parse(reader.GetString(10)),
                reader.IsDBNull(11) ? null : reader.GetInt64(11),
                reader.IsDBNull(12) ? null : reader.GetString(12)));
        }
        return result;
    }

    private static ReviewRequestRecord ReadRequest(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.GetString(6),
        reader.GetString(7),
        Parse(reader.GetString(8)),
        Parse(reader.GetString(9)),
        reader.IsDBNull(10) ? null : Parse(reader.GetString(10)),
        reader.IsDBNull(11) ? null : reader.GetString(11),
        reader.IsDBNull(12) ? null : reader.GetString(12),
        reader.IsDBNull(13) ? null : Parse(reader.GetString(13)),
        reader.IsDBNull(14) ? null : Parse(reader.GetString(14)),
        reader.IsDBNull(15) ? null : Parse(reader.GetString(15)),
        reader.IsDBNull(16) ? null : Parse(reader.GetString(16)));

    private static string Stamp(DateTime value) => value.ToUniversalTime().ToString("O");

    private static DateTime Parse(string value) =>
        DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
