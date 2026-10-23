using System.Globalization;
using Microsoft.Data.Sqlite;
using VibeRails.DB;

namespace VibeRails.Data.Sqlite;

/// <summary>
/// Review requests and decisions in the normal state database (VB-50). Additive component
/// <c>review-requests/1</c>; older versions ignore the two tables.
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
    public async Task<ReviewDecisionRecord> AddDecisionAsync(ReviewDecisionRecord decision, CancellationToken ct = default)
    {
        using var db = SqliteConnectionFactory.Open(_connectionString);
        using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO ReviewDecisions (RequestId, Reviewer, Decision, Comment, KeyId, Fingerprint, TreeId, SignatureBase64, DecidedUTC, VerifiedUTC)
            VALUES ($request, $reviewer, $decision, $comment, $key, $fingerprint, $tree, $signature, $decided, $verified)
            RETURNING Id;
            """;
        command.Parameters.AddWithValue("$request", decision.RequestId);
        command.Parameters.AddWithValue("$reviewer", decision.Reviewer);
        command.Parameters.AddWithValue("$decision", decision.Decision);
        command.Parameters.AddWithValue("$comment", (object?)decision.Comment ?? DBNull.Value);
        command.Parameters.AddWithValue("$key", (object?)decision.KeyId?.ToString("D") ?? DBNull.Value);
        command.Parameters.AddWithValue("$fingerprint", (object?)decision.Fingerprint ?? DBNull.Value);
        command.Parameters.AddWithValue("$tree", (object?)decision.TreeId ?? DBNull.Value);
        command.Parameters.AddWithValue("$signature", (object?)decision.SignatureBase64 ?? DBNull.Value);
        command.Parameters.AddWithValue("$decided", Stamp(decision.DecidedUTC));
        command.Parameters.AddWithValue("$verified", decision.VerifiedUTC is { } verified ? Stamp(verified) : DBNull.Value);
        var id = (long)(await command.ExecuteScalarAsync(ct) ?? throw new InvalidOperationException("The review decision was not stored."));
        return decision with { Id = id };
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

    private const string RequestColumns =
        "SELECT Id, ProjectPath, Scope, TreeId, BaseCommit, CardKey, Status, BundleJson, CreatedUTC, UpdatedUTC";

    private const string DecisionColumns =
        "SELECT d.Id, d.RequestId, d.Reviewer, d.Decision, d.Comment, d.KeyId, d.Fingerprint, d.TreeId, d.SignatureBase64, d.DecidedUTC, d.VerifiedUTC";

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
                reader.IsDBNull(10) ? null : Parse(reader.GetString(10))));
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
        Parse(reader.GetString(9)));

    private static string Stamp(DateTime value) => value.ToUniversalTime().ToString("O");

    private static DateTime Parse(string value) =>
        DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
