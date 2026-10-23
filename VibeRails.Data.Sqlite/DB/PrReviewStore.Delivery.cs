using Microsoft.Data.Sqlite;
using VibeRails.Data.Sqlite;

namespace VibeRails.DB;

public sealed partial class PrReviewStore
{
    private static void EnsureDeliverySchema(SqliteConnection connection) =>
        SqliteMigrationRunner.Apply(connection, "pr-review-delivery", 1, MigrationKind.Additive, (db, tx) =>
        {
            SqliteSchema.Execute(db, tx, """
                CREATE TABLE PrReviewerSettings (Id INTEGER PRIMARY KEY CHECK (Id = 1), Enabled INTEGER NOT NULL);
                INSERT INTO PrReviewerSettings (Id, Enabled) VALUES (1, 0);
                CREATE TABLE PrReviewDeliveries (
                    ReviewId INTEGER PRIMARY KEY,
                    Repository TEXT NOT NULL COLLATE NOCASE,
                    PrNumber INTEGER NOT NULL,
                    HeadSha TEXT NOT NULL,
                    Event TEXT NOT NULL,
                    Body TEXT NOT NULL,
                    Marker TEXT NOT NULL UNIQUE,
                    Status TEXT NOT NULL,
                    Login TEXT,
                    GitHubId INTEGER,
                    Error TEXT
                );
                CREATE INDEX idx_pr_review_deliveries_pr ON PrReviewDeliveries (Repository, PrNumber);
                """);
        });

    /// <inheritdoc />
    public async Task<PrReviewerSettings> GetReviewerSettingsAsync(CancellationToken ct = default)
    {
        await using var db = await OpenAsync("", ct);
        using var command = Command(db, null, "SELECT Enabled FROM PrReviewerSettings WHERE Id = 1;");
        return new(Convert.ToInt32(command.ExecuteScalar()) != 0);
    }

    /// <inheritdoc />
    public async Task<PrReviewerSettings> SetReviewerSettingsAsync(bool enabled, CancellationToken ct = default)
    {
        await using var db = await OpenAsync("", ct);
        using var command = Command(db, null, "UPDATE PrReviewerSettings SET Enabled = $enabled WHERE Id = 1;",
            ("$enabled", enabled ? 1 : 0));
        await command.ExecuteNonQueryAsync(ct);
        return new(enabled);
    }

    /// <inheritdoc />
    public async Task<PrReviewDelivery> EnsureDeliveryAsync(string repository, long reviewId, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(repository, ct);
        using var tx = db.BeginTransaction(deferred: false);
        var review = ReadReview(db, tx, repository, reviewId)
            ?? throw new InvalidOperationException("The review was not found.");
        if (review.Status != PrReviewStatus.Completed) throw new InvalidOperationException("The review is unfinished.");
        var delivery = EnsureDelivery(db, tx, review);
        tx.Commit();
        return delivery;
    }

    private static PrReviewDelivery EnsureDelivery(SqliteConnection db, SqliteTransaction tx, PrReview review)
    {
        var marker = $"<!-- viberails-review:{Guid.NewGuid():N} -->";
        Execute(db, tx, """
            INSERT INTO PrReviewDeliveries (ReviewId, Repository, PrNumber, HeadSha, Event, Body, Marker, Status)
            VALUES ($id, $repository, $number, $head, $event, $body, $marker,
                CASE (SELECT Enabled FROM PrReviewerSettings WHERE Id = 1) WHEN 1 THEN 'pending' ELSE 'disabled' END)
            ON CONFLICT (ReviewId) DO NOTHING;
            """, ("$id", review.Id), ("$repository", review.Repository), ("$number", review.PrNumber), ("$head", review.HeadSha.ToLowerInvariant()),
            ("$event", review.Blocking || review.Verdict == PrReviewVerdict.RequestChanges ? "REQUEST_CHANGES" : "COMMENT"),
            ("$body", review.Feedback + "\n\n" + marker), ("$marker", marker));
        var delivery = ReadDelivery(db, tx, review.Repository, review.Id)
            ?? throw new InvalidOperationException("A different repository owns this delivery.");
        return delivery;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PrReviewDelivery>> GetDeliveriesAsync(string repository, int number, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(repository, ct);
        using var command = Command(db, null,
            "SELECT * FROM PrReviewDeliveries WHERE Repository = $repository AND PrNumber = $number;",
            ("$repository", repository), ("$number", number));
        using var reader = command.ExecuteReader();
        var deliveries = new List<PrReviewDelivery>();
        while (reader.Read()) deliveries.Add(MapDelivery(reader));
        return deliveries;
    }

    /// <inheritdoc />
    public async Task<bool> ClaimDeliveryAsync(string repository, long reviewId, string login, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(repository, ct);
        using var command = Command(db, null, """
            UPDATE PrReviewDeliveries SET Status = 'unknown', Login = $login, Error = 'GitHub delivery is unconfirmed. Retry checks for a receipt without resending.'
            WHERE Repository = $repository AND ReviewId = $id AND Status IN ('pending', 'failed', 'disabled')
                AND EXISTS (SELECT 1 FROM PrReviewerSettings WHERE Id = 1 AND Enabled = 1);
            """, ("$repository", repository), ("$id", reviewId), ("$login", login));
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    /// <inheritdoc />
    public async Task SetDeliveryResultAsync(string repository, long reviewId, string status, long? githubId, string? error,
        CancellationToken ct = default, string? expectedStatus = null)
    {
        await using var db = await OpenAsync(repository, ct);
        using var command = Command(db, null, """
            UPDATE PrReviewDeliveries SET Status = $status, GitHubId = $githubId, Error = $error
            WHERE Repository = $repository AND ReviewId = $id AND Status <> 'published'
                AND ($expected IS NULL OR Status = $expected);
            """, ("$repository", repository), ("$id", reviewId), ("$status", status), ("$githubId", githubId), ("$error", error),
            ("$expected", expectedStatus));
        await command.ExecuteNonQueryAsync(ct);
    }

    private static PrReviewDelivery? ReadDelivery(SqliteConnection db, SqliteTransaction tx, string repository, long id)
    {
        using var command = Command(db, tx, "SELECT * FROM PrReviewDeliveries WHERE Repository = $repository AND ReviewId = $id;",
            ("$repository", repository), ("$id", id));
        using var reader = command.ExecuteReader();
        return reader.Read() ? MapDelivery(reader) : null;
    }

    private static PrReviewDelivery MapDelivery(SqliteDataReader r) => new(r.GetInt64(0), r.GetString(1), r.GetInt32(2),
        r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7),
        r.IsDBNull(8) ? null : r.GetString(8), r.IsDBNull(9) ? null : r.GetInt64(9), r.IsDBNull(10) ? null : r.GetString(10));
}
