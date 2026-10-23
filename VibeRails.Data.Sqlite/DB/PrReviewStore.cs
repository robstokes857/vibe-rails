using System.Globalization;
using Microsoft.Data.Sqlite;
using VibeRails.Data.Sqlite;

namespace VibeRails.DB;

/// <summary>
/// Lead-review records for GitHub pull requests in <c>pr_reviews.db</c>. Connection per
/// operation; the additive <c>pr-reviews/1</c> schema is created on first use so the store works
/// in any host without the dashboard's migration pass. Every multi-statement write takes the
/// writer lock up front (<c>BEGIN IMMEDIATE</c>) so a read-then-write cannot interleave with
/// another process's write to the same PR.
/// </summary>
public sealed partial class PrReviewStore(string connectionString, TimeProvider? timeProvider = null,
    string? legacyConnectionString = null) : IPrReviewStore
{
    internal const int Generation = 1;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private volatile bool _schemaReady;

    // Fields off the newest completed review. Reviewed time first; the id breaks a tie between
    // two passes completed in the same tick.
    private const string NewestCompleted = """
        FROM PrReviews latest
        WHERE latest.Repository = p.Repository AND latest.PrNumber = p.Number AND latest.Status = 'completed'
        ORDER BY COALESCE(latest.ReviewedUTC, latest.StartedUTC) DESC, latest.Id DESC
        LIMIT 1
        """;

    // Positional: MapPullRequest reads these columns by index.
    private const string PullRequestProjection = $"""
        SELECT p.Repository, p.Number, p.Title, p.Author, p.OpenedUTC, p.Summary,
            EXISTS (SELECT 1 FROM PrReviews pending
                WHERE pending.Repository = p.Repository AND pending.PrNumber = p.Number AND pending.Status = 'in_progress'),
            (SELECT COUNT(*) FROM PrReviews counted WHERE counted.Repository = p.Repository AND counted.PrNumber = p.Number),
            (SELECT COUNT(*) FROM PrReviews done
                WHERE done.Repository = p.Repository AND done.PrNumber = p.Number AND done.Status = 'completed'),
            COALESCE((SELECT latest.Blocking {NewestCompleted}), 0),
            (SELECT NULLIF(latest.HeadSha, '') {NewestCompleted}),
            (SELECT latest.ReviewedUTC {NewestCompleted}),
            p.GitHubState, p.GitHubStateUTC, p.GitHubCheckedUTC, p.CreatedUTC, p.UpdatedUTC
        FROM PrReviewPullRequests p
        """;

    private const string ReviewColumns =
        "Id, Repository, PrNumber, HeadSha, Status, Verdict, Blocking, Feedback, StartedUTC, ReviewedUTC, UpdatedUTC";

    /// <inheritdoc />
    public async Task<IReadOnlyList<PrReviewPullRequest>> ListAsync(string repository, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(repository, ct);
        await using var command = Command(db, null,
            PullRequestProjection + " WHERE p.Repository = $repository ORDER BY p.UpdatedUTC DESC, p.Number DESC;",
            ("$repository", repository));
        var result = new List<PrReviewPullRequest>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(MapPullRequest(reader));
        return result;
    }

    /// <inheritdoc />
    public async Task<PrReviewPullRequest?> GetAsync(string repository, int number, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(repository, ct);
        return ReadPullRequest(db, null, repository, number);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PrReview>> GetReviewsAsync(string repository, int number, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(repository, ct);
        await using var command = Command(db, null, $"""
            SELECT {ReviewColumns} FROM PrReviews
            WHERE Repository = $repository AND PrNumber = $number
            ORDER BY CASE Status WHEN 'in_progress' THEN 0 ELSE 1 END,
                COALESCE(ReviewedUTC, StartedUTC) DESC, Id DESC;
            """, ("$repository", repository), ("$number", number));
        var result = new List<PrReview>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(MapReview(reader));
        return result;
    }

    /// <inheritdoc />
    public Task<PrReviewWriteResult> UpsertAsync(string repository, int number, PrReviewPullRequestFields fields,
        CancellationToken ct = default) => WriteAsync(repository, ct, (db, tx, now) =>
        {
            var upsert = UpsertPullRequest(db, tx, repository, number, fields, now);
            return upsert.Outcome == PrReviewWriteOutcome.MissingField
                ? upsert
                : upsert with { PullRequest = ReadPullRequest(db, tx, repository, number) };
        });

    /// <inheritdoc />
    public Task<PrReviewWriteResult> UpdateAsync(string repository, int number, PrReviewPullRequestFields fields,
        CancellationToken ct = default) => WriteAsync(repository, ct, (db, tx, now) =>
        {
            if (ReadPullRequest(db, tx, repository, number) is null)
                return new PrReviewWriteResult(PrReviewWriteOutcome.NotTracked);
            UpsertPullRequest(db, tx, repository, number, fields, now);
            return new PrReviewWriteResult(PrReviewWriteOutcome.Saved, ReadPullRequest(db, tx, repository, number));
        });

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(string repository, int number, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(repository, ct);
        await using var command = Command(db, null,
            "DELETE FROM PrReviewPullRequests WHERE Repository = $repository AND Number = $number;",
            ("$repository", repository), ("$number", number));
        return await command.ExecuteNonQueryAsync(ct) > 0;
    }

    /// <inheritdoc />
    public Task<PrReviewWriteResult> StartReviewAsync(string repository, int number, PrReviewPullRequestFields fields,
        string? headSha, CancellationToken ct = default) => WriteAsync(repository, ct, (db, tx, now) =>
        {
            var upsert = UpsertPullRequest(db, tx, repository, number, fields, now);
            if (upsert.Outcome == PrReviewWriteOutcome.MissingField) return upsert;

            if (ReadPendingReview(db, tx, repository, number) is { } pending)
            {
                if (headSha is not null && headSha != pending.HeadSha)
                {
                    Execute(db, tx, "UPDATE PrReviews SET HeadSha = $head, UpdatedUTC = $now WHERE Id = $id;",
                        ("$head", headSha), ("$now", now), ("$id", pending.Id));
                }
                return new PrReviewWriteResult(PrReviewWriteOutcome.Saved,
                    ReadPullRequest(db, tx, repository, number), ReadReview(db, tx, repository, pending.Id));
            }

            using var insert = Command(db, tx, """
                INSERT INTO PrReviews (Repository, PrNumber, HeadSha, Status, Blocking, Feedback, StartedUTC, UpdatedUTC)
                VALUES ($repository, $number, $head, 'in_progress', 0, '', $now, $now)
                RETURNING Id;
                """, ("$repository", repository), ("$number", number), ("$head", headSha ?? ""), ("$now", now));
            var id = Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
            return new PrReviewWriteResult(PrReviewWriteOutcome.Created,
                ReadPullRequest(db, tx, repository, number), ReadReview(db, tx, repository, id));
        });

    /// <inheritdoc />
    public Task<PrReviewWriteResult> SaveDraftAsync(string repository, int number, PrReviewFields fields, string? summary,
        CancellationToken ct = default) => WriteAsync(repository, ct, (db, tx, now) =>
        {
            if (RequirePendingReview(db, tx, repository, number, out var pending) is { } missing) return missing;
            Execute(db, tx, """
                UPDATE PrReviews SET
                    HeadSha = COALESCE($head, HeadSha), Verdict = COALESCE($verdict, Verdict),
                    Blocking = COALESCE($blocking, Blocking), Feedback = COALESCE($feedback, Feedback), UpdatedUTC = $now
                WHERE Id = $id;
                """, ("$head", fields.HeadSha), ("$verdict", fields.Verdict), ("$blocking", Flag(fields.Blocking)),
                ("$feedback", fields.Feedback), ("$now", now), ("$id", pending!.Id));
            TouchPullRequest(db, tx, repository, number, summary, now);
            return new PrReviewWriteResult(PrReviewWriteOutcome.Saved,
                ReadPullRequest(db, tx, repository, number), ReadReview(db, tx, repository, pending.Id));
        });

    /// <inheritdoc />
    public Task<PrReviewWriteResult> CompleteReviewAsync(string repository, int number, string verdict, bool blocking,
        string feedback, string? headSha, string? summary, CancellationToken ct = default) => WriteAsync(repository, ct, (db, tx, now) =>
        {
            if (RequirePendingReview(db, tx, repository, number, out var pending) is { } missing) return missing;
            Execute(db, tx, """
                UPDATE PrReviews SET
                    HeadSha = COALESCE($head, HeadSha), Status = 'completed', Verdict = $verdict,
                    Blocking = $blocking, Feedback = $feedback, ReviewedUTC = $now, UpdatedUTC = $now
                WHERE Id = $id;
                """, ("$head", headSha), ("$verdict", verdict), ("$blocking", blocking ? 1 : 0),
                ("$feedback", feedback), ("$now", now), ("$id", pending!.Id));
            TouchPullRequest(db, tx, repository, number, summary, now);
            return new PrReviewWriteResult(PrReviewWriteOutcome.Saved,
                ReadPullRequest(db, tx, repository, number), ReadReview(db, tx, repository, pending.Id));
        });

    /// <inheritdoc />
    public async Task<PrReview?> UpdateReviewAsync(string repository, long id, PrReviewFields fields, CancellationToken ct = default)
    {
        var result = await WriteAsync(repository, ct, (db, tx, now) =>
        {
            if (ReadReview(db, tx, repository, id) is not { } existing)
                return new PrReviewWriteResult(PrReviewWriteOutcome.NotTracked);
            Execute(db, tx, """
                UPDATE PrReviews SET
                    HeadSha = COALESCE($head, HeadSha), Verdict = COALESCE($verdict, Verdict),
                    Blocking = COALESCE($blocking, Blocking), Feedback = COALESCE($feedback, Feedback), UpdatedUTC = $now
                WHERE Id = $id;
                """, ("$head", fields.HeadSha), ("$verdict", fields.Verdict), ("$blocking", Flag(fields.Blocking)),
                ("$feedback", fields.Feedback), ("$now", now), ("$id", id));
            TouchPullRequest(db, tx, repository, existing.PrNumber, null, now);
            return new PrReviewWriteResult(PrReviewWriteOutcome.Saved, Review: ReadReview(db, tx, repository, id));
        });
        return result.Review;
    }

    /// <inheritdoc />
    public async Task<int?> DeleteReviewAsync(string repository, long id, CancellationToken ct = default)
    {
        var result = await WriteAsync(repository, ct, (db, tx, now) =>
        {
            if (ReadReview(db, tx, repository, id) is not { } existing)
                return new PrReviewWriteResult(PrReviewWriteOutcome.NotTracked);
            Execute(db, tx, "DELETE FROM PrReviews WHERE Id = $id;", ("$id", id));
            TouchPullRequest(db, tx, repository, existing.PrNumber, null, now);
            return new PrReviewWriteResult(PrReviewWriteOutcome.Saved, Review: existing);
        });
        return result.Review?.PrNumber;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<int>> ListNumbersAsync(string repository, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(repository, ct);
        await using var command = Command(db, null,
            "SELECT Number FROM PrReviewPullRequests WHERE Repository = $repository ORDER BY Number;",
            ("$repository", repository));
        var result = new List<int>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(reader.GetInt32(0));
        return result;
    }

    /// <inheritdoc />
    public async Task<PrGitHubSyncCounts> RecordGitHubStatesAsync(string repository, IReadOnlyList<PrGitHubStateUpdate> states,
        CancellationToken ct = default)
    {
        foreach (var state in states)
        {
            if (!PrGitHubState.IsRecordable(state.State))
                throw new ArgumentException($"GitHub state '{state.State}' is not open, merged or closed.", nameof(states));
        }

        var recorded = 0;
        var changed = 0;
        await WriteAsync(repository, ct, (db, tx, now) =>
        {
            foreach (var state in states)
            {
                using var before = Command(db, tx,
                    "SELECT GitHubState FROM PrReviewPullRequests WHERE Repository = $repository AND Number = $number;",
                    ("$repository", repository), ("$number", state.Number));
                if (before.ExecuteScalar() is not string previous) continue;

                // UpdatedUTC means "last review write". A background poll bumping it would
                // reshuffle the history into poll order, so the sync never touches it.
                Execute(db, tx, """
                    UPDATE PrReviewPullRequests SET GitHubState = $state, GitHubStateUTC = $stateAt, GitHubCheckedUTC = $now
                    WHERE Repository = $repository AND Number = $number;
                    """, ("$state", state.State), ("$stateAt", state.StateUtc?.ToUniversalTime().ToString("O")),
                    ("$now", now), ("$repository", repository), ("$number", state.Number));
                recorded++;
                if (!string.Equals(previous, state.State, StringComparison.Ordinal)) changed++;
            }
            return new PrReviewWriteResult(PrReviewWriteOutcome.Saved);
        });
        return new PrGitHubSyncCounts(recorded, changed);
    }

    internal static void EnsureSchema(SqliteConnection connection)
    {
        SqliteMigrationRunner.RequireGenerationAtMost(connection, Generation, "pr_reviews.db");
        SqliteMigrationRunner.Apply(connection, "pr-reviews", 1, MigrationKind.Additive, (db, transaction) =>
        {
            // Verdict and GitHubState are validated in code, not by CHECK: a future value would
            // otherwise need a table rebuild (a breaking migration) instead of an additive one.
            SqliteSchema.Execute(db, transaction, """
                CREATE TABLE IF NOT EXISTS PrReviewPullRequests (
                    Repository       TEXT    NOT NULL COLLATE NOCASE,
                    Number           INTEGER NOT NULL CHECK (Number > 0),
                    Title            TEXT    NOT NULL,
                    Author           TEXT    NOT NULL,
                    OpenedUTC        TEXT    NOT NULL,
                    Summary          TEXT    NOT NULL DEFAULT '',
                    GitHubState      TEXT    NOT NULL DEFAULT '',
                    GitHubStateUTC   TEXT,
                    GitHubCheckedUTC TEXT,
                    CreatedUTC       TEXT    NOT NULL,
                    UpdatedUTC       TEXT    NOT NULL,
                    PRIMARY KEY (Repository, Number)
                );
                """);
            SqliteSchema.Execute(db, transaction, """
                CREATE TABLE IF NOT EXISTS PrReviews (
                    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
                    Repository  TEXT    NOT NULL COLLATE NOCASE,
                    PrNumber    INTEGER NOT NULL,
                    HeadSha     TEXT    NOT NULL DEFAULT '',
                    Status      TEXT    NOT NULL CHECK (Status IN ('in_progress', 'completed')),
                    Verdict     TEXT,
                    Blocking    INTEGER NOT NULL DEFAULT 0 CHECK (Blocking IN (0, 1)),
                    Feedback    TEXT    NOT NULL DEFAULT '',
                    StartedUTC  TEXT    NOT NULL,
                    ReviewedUTC TEXT,
                    UpdatedUTC  TEXT    NOT NULL,
                    FOREIGN KEY (Repository, PrNumber) REFERENCES PrReviewPullRequests (Repository, Number) ON DELETE CASCADE
                );
                """);
            // One unfinished pass per PR, enforced by the database rather than by every caller.
            SqliteSchema.Execute(db, transaction, """
                CREATE UNIQUE INDEX IF NOT EXISTS idx_pr_reviews_one_in_progress
                    ON PrReviews (Repository, PrNumber) WHERE Status = 'in_progress';
                """);
            SqliteSchema.Execute(db, transaction, """
                CREATE INDEX IF NOT EXISTS idx_pr_reviews_pr ON PrReviews (Repository, PrNumber, Status);
                """);
            SqliteSchema.Execute(db, transaction, """
                CREATE INDEX IF NOT EXISTS idx_pr_review_pull_requests_updated
                    ON PrReviewPullRequests (Repository, UpdatedUTC);
                """);
        });
        EnsureAdoptionSchema(connection);
        SqliteMigrationRunner.StampGeneration(connection, Generation);
    }

    private async Task<PrReviewWriteResult> WriteAsync(string repository, CancellationToken ct,
        Func<SqliteConnection, SqliteTransaction, string, PrReviewWriteResult> write)
    {
        await using var db = await OpenAsync(repository, ct);
        using var tx = db.BeginTransaction(deferred: false);
        var result = write(db, tx, _time.GetUtcNow().UtcDateTime.ToString("O"));
        if (result.Outcome is PrReviewWriteOutcome.Saved or PrReviewWriteOutcome.Created)
            await tx.CommitAsync(ct);
        return result;
    }

    private static PrReviewWriteResult UpsertPullRequest(SqliteConnection db, SqliteTransaction tx, string repository,
        int number, PrReviewPullRequestFields fields, string now)
    {
        using var probe = Command(db, tx,
            "SELECT 1 FROM PrReviewPullRequests WHERE Repository = $repository AND Number = $number;",
            ("$repository", repository), ("$number", number));
        var exists = probe.ExecuteScalar() is not null;
        var opened = fields.OpenedUtc?.ToUniversalTime().ToString("O");
        if (exists)
        {
            Execute(db, tx, """
                UPDATE PrReviewPullRequests SET
                    Title = COALESCE($title, Title), Author = COALESCE($author, Author),
                    OpenedUTC = COALESCE($opened, OpenedUTC), Summary = COALESCE($summary, Summary), UpdatedUTC = $now
                WHERE Repository = $repository AND Number = $number;
                """, ("$title", fields.Title), ("$author", fields.Author), ("$opened", opened),
                ("$summary", fields.Summary), ("$now", now), ("$repository", repository), ("$number", number));
            return new PrReviewWriteResult(PrReviewWriteOutcome.Saved);
        }

        var missing = fields.Title is null ? "title" : fields.Author is null ? "author" : opened is null ? "openedAt" : null;
        if (missing is not null)
            return new PrReviewWriteResult(PrReviewWriteOutcome.MissingField, MissingField: missing);

        Execute(db, tx, """
            INSERT INTO PrReviewPullRequests (Repository, Number, Title, Author, OpenedUTC, Summary, CreatedUTC, UpdatedUTC)
            VALUES ($repository, $number, $title, $author, $opened, $summary, $now, $now);
            """, ("$repository", repository), ("$number", number), ("$title", fields.Title), ("$author", fields.Author),
            ("$opened", opened), ("$summary", fields.Summary ?? ""), ("$now", now));
        return new PrReviewWriteResult(PrReviewWriteOutcome.Created);
    }

    private static PrReviewWriteResult? RequirePendingReview(SqliteConnection db, SqliteTransaction tx, string repository,
        int number, out PrReview? pending)
    {
        pending = ReadPendingReview(db, tx, repository, number);
        if (pending is not null) return null;
        return new PrReviewWriteResult(ReadPullRequest(db, tx, repository, number) is null
            ? PrReviewWriteOutcome.NotTracked
            : PrReviewWriteOutcome.NoPendingReview);
    }

    /// <summary>Stamps the PR's last write, replacing its summary when one is supplied.</summary>
    private static void TouchPullRequest(SqliteConnection db, SqliteTransaction tx, string repository, int number,
        string? summary, string now) =>
        Execute(db, tx, """
            UPDATE PrReviewPullRequests SET Summary = COALESCE($summary, Summary), UpdatedUTC = $now
            WHERE Repository = $repository AND Number = $number;
            """, ("$summary", summary), ("$now", now), ("$repository", repository), ("$number", number));

    private static PrReviewPullRequest? ReadPullRequest(SqliteConnection db, SqliteTransaction? tx, string repository, int number)
    {
        using var command = Command(db, tx, PullRequestProjection + " WHERE p.Repository = $repository AND p.Number = $number;",
            ("$repository", repository), ("$number", number));
        using var reader = command.ExecuteReader();
        return reader.Read() ? MapPullRequest(reader) : null;
    }

    private static PrReview? ReadPendingReview(SqliteConnection db, SqliteTransaction tx, string repository, int number)
    {
        using var command = Command(db, tx, $"""
            SELECT {ReviewColumns} FROM PrReviews
            WHERE Repository = $repository AND PrNumber = $number AND Status = 'in_progress';
            """, ("$repository", repository), ("$number", number));
        using var reader = command.ExecuteReader();
        return reader.Read() ? MapReview(reader) : null;
    }

    /// <summary>Review ids are global; scoping the read to the repository keeps another project's review unreachable.</summary>
    private static PrReview? ReadReview(SqliteConnection db, SqliteTransaction tx, string repository, long id)
    {
        using var command = Command(db, tx, $"SELECT {ReviewColumns} FROM PrReviews WHERE Id = $id AND Repository = $repository;",
            ("$id", id), ("$repository", repository));
        using var reader = command.ExecuteReader();
        return reader.Read() ? MapReview(reader) : null;
    }

    private static PrReviewPullRequest MapPullRequest(SqliteDataReader reader)
    {
        var hasPending = reader.GetInt64(6) != 0;
        var completed = reader.GetInt32(8);
        var blocking = reader.GetInt64(9) != 0;
        var githubState = reader.GetString(12);
        return new PrReviewPullRequest(
            reader.GetString(0),
            reader.GetInt32(1),
            reader.GetString(2),
            reader.GetString(3),
            ParseUtc(reader.GetString(4)),
            reader.GetString(5),
            PrTrackerStatus.Derive(hasPending, completed, blocking),
            PrGitHubState.IsRecordable(githubState) ? githubState : PrGitHubState.Unknown,
            reader.IsDBNull(13) ? null : ParseUtc(reader.GetString(13)),
            reader.IsDBNull(14) ? null : ParseUtc(reader.GetString(14)),
            reader.GetInt32(7),
            completed,
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.IsDBNull(11) ? null : ParseUtc(reader.GetString(11)),
            ParseUtc(reader.GetString(15)),
            ParseUtc(reader.GetString(16)));
    }

    private static PrReview MapReview(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        reader.GetInt32(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.GetInt64(6) != 0,
        reader.GetString(7),
        ParseUtc(reader.GetString(8)),
        reader.IsDBNull(9) ? null : ParseUtc(reader.GetString(9)),
        ParseUtc(reader.GetString(10)));

    private static DateTime ParseUtc(string value) =>
        DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static object? Flag(bool? value) => value is null ? null : value.Value ? 1 : 0;

    private static SqliteCommand Command(SqliteConnection db, SqliteTransaction? tx, string sql,
        params (string Name, object? Value)[] parameters)
    {
        var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private static void Execute(SqliteConnection db, SqliteTransaction tx, string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var command = Command(db, tx, sql, parameters);
        command.ExecuteNonQuery();
    }

    private async Task<SqliteConnection> OpenAsync(string repository, CancellationToken ct)
    {
        var connection = await SqliteConnectionFactory.OpenAsync(connectionString, ct);
        try
        {
            if (!_schemaReady)
            {
                EnsureSchema(connection);
                _schemaReady = true;
            }
            if (legacyConnectionString is not null)
                ReconcileLegacy(connection, legacyConnectionString, repository, ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
