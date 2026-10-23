using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using VibeRails.Data.Sqlite;

namespace VibeRails.DB;

public sealed partial class PrReviewStore
{
    private const string PrColumns = "Repository, Number, Title, Author, OpenedUTC, Summary, GitHubState, " +
        "GitHubStateUTC, GitHubCheckedUTC, CreatedUTC, UpdatedUTC";

    private sealed record LegacySnapshot(object[]? PullRequest, List<object[]> Reviews);

    private static void EnsureAdoptionSchema(SqliteConnection connection)
    {
        SqliteMigrationRunner.Apply(connection, "pr-review-adoption", 1, MigrationKind.Additive, (db, tx) =>
            SqliteSchema.Execute(db, tx, """
                CREATE TABLE IF NOT EXISTS PrReviewLegacyAdoptions (
                    Repository TEXT NOT NULL COLLATE NOCASE,
                    Number INTEGER NOT NULL,
                    SourceHash TEXT NOT NULL,
                    TargetHash TEXT NOT NULL,
                    PRIMARY KEY (Repository, Number)
                );
                CREATE TABLE IF NOT EXISTS PrReviewLegacyIds (
                    LegacyId INTEGER PRIMARY KEY,
                    ReviewId INTEGER NOT NULL UNIQUE,
                    Repository TEXT NOT NULL COLLATE NOCASE,
                    Number INTEGER NOT NULL
                );
                """));
        SqliteMigrationRunner.Apply(connection, "pr-review-adoption", 2, MigrationKind.Additive, (db, tx) =>
        {
            SqliteSchema.AdoptStatement(db, tx, "ALTER TABLE PrReviewLegacyAdoptions ADD COLUMN SourceContentHash TEXT");
            SqliteSchema.AdoptStatement(db, tx, "ALTER TABLE PrReviewLegacyAdoptions ADD COLUMN TargetContentHash TEXT");
            SqliteSchema.AdoptStatement(db, tx, "ALTER TABLE PrReviewLegacyAdoptions ADD COLUMN ConflictSourceHash TEXT");
            SqliteSchema.Execute(db, tx, """
                CREATE TABLE IF NOT EXISTS PrReviewLegacyConflicts (
                    Repository TEXT NOT NULL COLLATE NOCASE,
                    Number INTEGER NOT NULL,
                    SourceHash TEXT NOT NULL,
                    TargetHash TEXT NOT NULL,
                    SourceSnapshot BLOB NOT NULL,
                    TargetSnapshot BLOB NOT NULL,
                    PRIMARY KEY (Repository, Number, SourceHash, TargetHash)
                );
                """);
        });
    }

    // Never ATTACH state.db to the writer: BEGIN IMMEDIATE would also lock its writer.
    // Acquire the target lock BEFORE the source snapshot, so competing importers cannot apply
    // an older snapshot after a newer one. Data, mappings and fingerprints commit together.
    private static void ReconcileLegacy(SqliteConnection target, string legacyConnection, string repository,
        CancellationToken ct)
    {
        var sourcePath = new SqliteConnectionStringBuilder(legacyConnection).DataSource;
        try { _ = File.GetAttributes(sourcePath); }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }

        var previousTimeout = target.DefaultTimeout;
        try
        {
            target.DefaultTimeout = SqliteMigrationRunner.MigrationLockTimeoutSeconds;
            using var write = target.BeginTransaction(deferred: false);
            using var source = SqliteConnectionFactory.Open(legacyConnection, readOnly: true);
            using var read = source.BeginTransaction(deferred: true);
            SqliteMigrationRunner.RequireGenerationAtMost(source, StateDatabaseSchema.Generation, "state.db");
            using (var probe = Command(source, read,
                "SELECT 1 FROM sqlite_schema WHERE type='table' AND name='PrReviewPullRequests';"))
                if (probe.ExecuteScalar() is null) return; // An older host may create it later.

            var numbers = new SortedSet<int>();
            foreach (var (db, tx, table) in new[]
            {
                (source, read, "PrReviewPullRequests"), (target, write, "PrReviewLegacyAdoptions")
            })
            {
                using var command = Command(db, tx, $"SELECT Number FROM {table} WHERE Repository=$repo;", ("$repo", repository));
                using var reader = command.ExecuteReader();
                while (reader.Read()) numbers.Add(reader.GetInt32(0));
            }

            foreach (var number in numbers)
            {
                ct.ThrowIfCancellationRequested();
                var legacy = ReadSnapshot(source, read, repository, number);
                var sourceHash = Fingerprint(legacy, contentOnly: true);
                string? previousSource = null, previousTarget = null, previousFullSource = null, previousFullTarget = null;
                using (var receipt = Command(target, write, """
                    SELECT SourceContentHash, TargetContentHash, SourceHash, TargetHash
                    FROM PrReviewLegacyAdoptions WHERE Repository=$repo AND Number=$number;
                    """, ("$repo", repository), ("$number", number)))
                using (var reader = receipt.ExecuteReader())
                    if (reader.Read())
                    {
                        previousSource = reader.IsDBNull(0) ? null : reader.GetString(0);
                        previousTarget = reader.IsDBNull(1) ? null : reader.GetString(1);
                        previousFullSource = reader.GetString(2); previousFullTarget = reader.GetString(3);
                    }

                var current = ReadSnapshot(target, write, repository, number);
                var targetHash = Fingerprint(current, contentOnly: true);
                // Upgrade earlier adoption receipts without treating unchanged histories as conflicts.
                if (previousSource is null && previousFullSource == Fingerprint(legacy)) previousSource = sourceHash;
                if (previousTarget is null && previousFullTarget == Fingerprint(current)) previousTarget = targetHash;
                MergeLifecycle(target, write, repository, number, legacy, current);
                if (previousFullSource is not null && (previousSource is null || previousTarget is null)
                    && Fingerprint(MapLegacyIds(target, write, legacy), contentOnly: true) == targetHash)
                {
                    RecordAdoption(target, write, repository, number, legacy);
                    continue;
                }
                if (sourceHash == previousSource)
                {
                    Execute(target, write, """
                        UPDATE PrReviewLegacyAdoptions SET SourceContentHash=$source,
                            TargetContentHash=COALESCE(TargetContentHash,$target), ConflictSourceHash=NULL
                        WHERE Repository=$repo AND Number=$number;
                        """, ("$source", sourceHash), ("$target", previousTarget), ("$repo", repository), ("$number", number));
                    continue;
                }
                if (previousTarget is null ? current.PullRequest is not null : previousTarget != targetHash)
                {
                    // Preserve both exact snapshots and continue serving the current history.
                    // Conflicts are visible/resolvable on Reviews, including a locally deleted PR.
                    ArchiveConflict(target, write, repository, number, sourceHash, targetHash, legacy, current);
                    Execute(target, write, """
                        INSERT INTO PrReviewLegacyAdoptions (Repository,Number,SourceHash,TargetHash,ConflictSourceHash)
                        VALUES ($repo,$number,'','',$source)
                        ON CONFLICT (Repository,Number) DO UPDATE SET ConflictSourceHash=$source;
                        """, ("$repo", repository), ("$number", number), ("$source", sourceHash));
                    continue;
                }

                CopySnapshot(target, write, repository, number, WithLatestLifecycle(legacy, current));
                RecordAdoption(target, write, repository, number, legacy);
            }
            ct.ThrowIfCancellationRequested();
            write.Commit();
        }
        finally { target.DefaultTimeout = previousTimeout; }
    }

    private static LegacySnapshot ReadSnapshot(SqliteConnection db, SqliteTransaction tx, string repository, int number)
    {
        var parameters = new (string, object?)[] { ("$repo", repository), ("$number", number) };
        var prs = ReadRows(db, tx, $"SELECT {PrColumns} FROM PrReviewPullRequests WHERE Repository=$repo AND Number=$number;", parameters);
        var reviews = ReadRows(db, tx, $"SELECT {ReviewColumns} FROM PrReviews WHERE Repository=$repo AND PrNumber=$number ORDER BY Id;", parameters);
        return new LegacySnapshot(prs.SingleOrDefault(), reviews);
    }

    private static List<object[]> ReadRows(SqliteConnection db, SqliteTransaction tx, string sql,
        params (string, object?)[] parameters)
    {
        using var command = Command(db, tx, sql, parameters);
        using var reader = command.ExecuteReader();
        var rows = new List<object[]>();
        while (reader.Read())
        {
            var row = new object[reader.FieldCount];
            reader.GetValues(row);
            rows.Add(row);
        }
        return rows;
    }

    private static string Fingerprint(LegacySnapshot snapshot, bool contentOnly = false) =>
        Convert.ToHexString(SHA256.HashData(SerializeSnapshot(snapshot, contentOnly)));

    private static byte[] SerializeSnapshot(LegacySnapshot snapshot, bool contentOnly = false)
    {
        // Length-prefixed, typed values distinguish NULL, empty text and text containing separators.
        // Use raw stored values so timestamps and draft contents are adopted without conversion.
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(snapshot.PullRequest is not null);
            foreach (var row in (snapshot.PullRequest is null ? [] : new[] { snapshot.PullRequest }).Concat(snapshot.Reviews))
            {
                writer.Write(row.Length);
                for (var i = 0; i < row.Length; i++)
                {
                    // Lifecycle polling and last-write bookkeeping are not authored review edits.
                    var value = contentOnly && (i == 10 || (ReferenceEquals(row, snapshot.PullRequest) && i is 6 or 7 or 8))
                        ? DBNull.Value : row[i];
                    writer.Write(value is DBNull ? "null" : value.GetType().Name);
                    writer.Write(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "");
                }
            }
        }
        return bytes.ToArray();
    }

    private static void CopySnapshot(SqliteConnection db, SqliteTransaction tx, string repository, int number, LegacySnapshot source)
    {
        Execute(db, tx, "DELETE FROM PrReviewPullRequests WHERE Repository=$repo AND Number=$number;",
            ("$repo", repository), ("$number", number));
        if (source.PullRequest is null) return;
        InsertRow(db, tx, "PrReviewPullRequests", PrColumns, source.PullRequest);
        foreach (var legacyRow in source.Reviews)
        {
            var row = (object[])legacyRow.Clone();
            var legacyId = (long)row[0];
            var mapped = false;
            using var mapping = Command(db, tx,
                "SELECT ReviewId, Repository, Number FROM PrReviewLegacyIds WHERE LegacyId=$id;", ("$id", legacyId));
            using (var reader = mapping.ExecuteReader())
            {
                if (reader.Read())
                {
                    if (!string.Equals(reader.GetString(1), repository, StringComparison.OrdinalIgnoreCase) || reader.GetInt32(2) != number)
                        throw new PrReviewAdoptionConflictException(number);
                    row[0] = reader.GetInt64(0);
                    mapped = true;
                }
            }
            if (!mapped)
            {
                // Keep existing ids where possible, including gaps left by old deletions.
                // Later local/legacy AUTOINCREMENT sequences are independent, so collisions
                // allocate a new id and the permanent map routes subsequent legacy edits.
                using var occupied = Command(db, tx, """
                    SELECT 1 FROM PrReviews WHERE Id=$id
                    UNION ALL SELECT 1 FROM PrReviewLegacyIds WHERE ReviewId=$id
                    UNION ALL SELECT 1 FROM PrReviewDeliveries WHERE ReviewId=$id;
                    """, ("$id", legacyId));
                if (occupied.ExecuteScalar() is not null) row[0] = DBNull.Value;
            }
            InsertRow(db, tx, "PrReviews", ReviewColumns, row);
            if (mapped) continue;
            Execute(db, tx, """
                INSERT INTO PrReviewLegacyIds (LegacyId, ReviewId, Repository, Number)
                VALUES ($id, last_insert_rowid(), $repo, $number);
                """, ("$id", legacyId), ("$repo", repository), ("$number", number));
        }
    }

    private static void InsertRow(SqliteConnection db, SqliteTransaction tx, string table, string columns, object[] row)
    {
        var parameters = row.Select((value, index) => ("$v" + index, (object?)value)).ToArray();
        Execute(db, tx, $"INSERT INTO {table} ({columns}) VALUES ({string.Join(",", parameters.Select(p => p.Item1))});", parameters);
    }
}
