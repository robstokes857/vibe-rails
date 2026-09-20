using System.Globalization;
using Microsoft.Data.Sqlite;
using Serilog;
using VibeRails.Data.Abstractions;

namespace VibeRails.Data.Sqlite;

/// <summary>One row of the SchemaMigrations ledger.</summary>
public sealed record SchemaMigrationReceipt(string Component, int Version, string AppliedUtc, string? AppliedBy);

/// <summary>
/// Applies each component migration once per database, including across independent vb processes.
/// The schema changes and their completion record share one SQLite write transaction.
///
/// All pending migrations run automatically. Breaking changes to existing files receive a
/// consistent backup under the same writer lock as the migration. Databases newer than this
/// build understands are still refused to protect against incompatible downgrades.
/// </summary>
internal static class SqliteMigrationRunner
{
    // PRODUCT CONTRACT: Opening a new version must just work.
    // NEVER gate schema initialization behind a CLI command, opt-in flag, acknowledgement,
    // process-count check, or manual upgrade instructions. The former manual gate broke the
    // Board in 1.10.16 and surfaced as a misleading CORS error during service resolution.
    // Prefer additive schema changes: different app versions can run on the same machine.
    // Feature removal means stopping current reads/writes; retain unused tables and columns.
    // Unused schema is accepted debt. Destructive cleanup, historical-data conversion, and
    // backfills require an explicit request; removing a feature alone is not such a request.
    // Required schema setup is automatic; preserve older-version compatibility where possible.
    internal const int MigrationLockTimeoutSeconds = 60;

    internal static bool Apply(SqliteConnection connection, string component, int version, MigrationKind kind,
        Action<SqliteConnection, SqliteTransaction> migration,
        int lockTimeoutSeconds = MigrationLockTimeoutSeconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(component);
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(lockTimeoutSeconds, 1);
        try
        {
            if (IsApplied(connection, null, component, version))
                return false;
        }
        catch (SqliteException ex) when (IsLockContention(ex))
        {
            // A legacy database in rollback-journal mode can block even this metadata read.
            // Repeat only the read under the migration wait policy; never retry a callback.
        }

        var previousTimeout = connection.DefaultTimeout;
        var previousBusyTimeout = ReadBusyTimeout(connection);
        try
        {
            // Startup adoption may overlap older vb processes that already own the writer lock.
            // Only pending migrations receive this longer wait; ordinary operations retain 5s.
            connection.DefaultTimeout = lockTimeoutSeconds;
            SetBusyTimeout(connection, checked(lockTimeoutSeconds * 1000));

            // First contact with a file decides whether it pre-dates this process. It has to happen
            // before any migration creates tables, or a fresh file would look like an adopted one.
            var existing = SchemaUpgradePolicy.IsExistingDatabase(connection);
            Log.Information("[Database] Applying {Kind} migration {Component}/{Version} to {Database}; waiting up to {WaitSeconds}s for database locks.",
                kind, component, version, connection.DataSource, lockTimeoutSeconds);
            var applied = ApplyPending(connection, component, version, migration,
                backup: kind == MigrationKind.Breaking && existing && SchemaUpgradePolicy.BackupBeforeBreaking);
            Log.Information("[Database] Migration {Component}/{Version} {Result} for {Database}.",
                component, version, applied ? "completed" : "completed in another process", connection.DataSource);
            return applied;
        }
        catch (SqliteException ex) when (IsLockContention(ex))
        {
            throw new StorageException(
                $"Database migration '{component}/{version}' could not obtain a database lock within {lockTimeoutSeconds} seconds for '{connection.DataSource}'. " +
                "The database is busy; the upgrade will retry on the next initialization. No migration changes were committed.", true, ex);
        }
        catch (SqliteException ex)
        {
            throw new StorageException(
                $"Database migration '{component}/{version}' failed for '{connection.DataSource}': {ex.Message}"
                + DescribeBlockingRows(connection, ex), false, ex);
        }
        finally
        {
            // Connections can be used again by the store and may return to a pool.
            connection.DefaultTimeout = previousTimeout;
            SetBusyTimeout(connection, previousBusyTimeout);
        }
    }

    /// <summary>
    /// Refuses to open a database written by a build newer than this one. The generation is bumped
    /// only by breaking changes, so additive upgrades from newer builds are still readable here.
    /// </summary>
    internal static void RequireGenerationAtMost(SqliteConnection connection, int generation, string label)
    {
        var current = ReadGeneration(connection);
        if (current <= generation)
            return;
        throw new StorageException(
            $"'{connection.DataSource}' was upgraded by a newer VibeRails: its {label} schema is generation {current} and this build understands generation {generation}. " +
            "This build will not open it. Install the VibeRails version that upgraded it (or newer), or restore the copy taken before that upgrade from the 'backups' folder beside the database.",
            false, new InvalidOperationException("database schema is newer than this build"));
    }

    /// <summary>Records the generation this build's schema has reached. Never lowers it.</summary>
    internal static void StampGeneration(SqliteConnection connection, int generation)
    {
        if (ReadGeneration(connection) >= generation)
            return;
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version=" + generation.ToString(CultureInfo.InvariantCulture) + ";";
        command.ExecuteNonQuery();
    }

    internal static long ReadGeneration(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    internal static IReadOnlyList<SchemaMigrationReceipt> ReadReceipts(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_schema WHERE type='table' AND name='SchemaMigrations';";
        if (command.ExecuteScalar() is null)
            return [];
        // The provenance column arrives with the first migration a build that knows it applies; a
        // ledger written entirely by older builds does not have it yet.
        var appliedBy = SqliteSchema.HasColumn(connection, null, "SchemaMigrations", "AppliedBy") ? "AppliedBy" : "NULL";
        command.CommandText = $"SELECT Component, Version, AppliedUTC, {appliedBy} FROM SchemaMigrations ORDER BY AppliedUTC, Component, Version;";
        var receipts = new List<SchemaMigrationReceipt>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            receipts.Add(new SchemaMigrationReceipt(reader.GetString(0), reader.GetInt32(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        return receipts;
    }

    private static bool ApplyPending(SqliteConnection connection, string component, int version,
        Action<SqliteConnection, SqliteTransaction> migration, bool backup)
    {
        // journal_mode cannot change inside a transaction. This is done only when there is work,
        // rather than on every connection or every ordinary process startup.
        using (var wal = connection.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode=WAL;";
            wal.ExecuteScalar();
        }

        // BEGIN IMMEDIATE serializes schema writers. Recheck after acquiring the lock:
        // another process may have completed this version while this connection waited.
        using var transaction = connection.BeginTransaction(deferred: false);
        if (IsApplied(connection, transaction, component, version))
        {
            transaction.Commit();
            return false;
        }

        if (backup)
        {
            // SQLite cannot back up a connection with an active write transaction. A separate
            // reader sees the last committed state while our writer lock prevents any competing
            // writer from changing it. Back up before even changing the migration ledger.
            using var source = SqliteConnectionFactory.Open(connection.ConnectionString, readOnly: true);
            var backupPath = SqliteDatabaseBackup.Create(source, $"{component}-{version}");
            Log.Information("[Database] Backed up {Database} to {Backup} before migration {Component}/{Version}.",
                connection.DataSource, backupPath, component, version);
        }

        using (var create = connection.CreateCommand())
        {
            create.Transaction = transaction;
            create.CommandText = """
                CREATE TABLE IF NOT EXISTS SchemaMigrations (
                    Component TEXT NOT NULL,
                    Version INTEGER NOT NULL CHECK (Version > 0),
                    AppliedUTC TEXT NOT NULL,
                    PRIMARY KEY (Component, Version)
                );
                """;
            create.ExecuteNonQuery();
        }
        // Which build applied each step. Without it a database cannot tell you which code
        // understands it -- the exact question that was unanswerable on 2026-09-16.
        SqliteSchema.AdoptStatement(connection, transaction, "ALTER TABLE SchemaMigrations ADD COLUMN AppliedBy TEXT");
        migration(connection, transaction);
        using (var record = connection.CreateCommand())
        {
            record.Transaction = transaction;
            record.CommandText = "INSERT INTO SchemaMigrations(Component, Version, AppliedUTC, AppliedBy) VALUES ($component, $version, $utc, $by);";
            record.Parameters.AddWithValue("$component", component);
            record.Parameters.AddWithValue("$version", version);
            record.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            record.Parameters.AddWithValue("$by", SchemaUpgradePolicy.AppliedBy);
            record.ExecuteNonQuery();
        }
        transaction.Commit();
        return true;
    }

    /// <summary>
    /// Adoption stays strict on purpose: a silently half-installed schema is worse than a loud
    /// failure, and swallowing these is exactly how the old initializer left databases in shapes
    /// nobody could reproduce. Strict is only defensible if the operator is told WHICH rows to fix,
    /// so a constraint failure carries the offending values and the remedy.
    ///
    /// Best effort by construction: the probe runs after the migration transaction has already
    /// rolled back, and any failure inside it is swallowed so diagnostics can never replace the
    /// real migration error with a different exception.
    /// </summary>
    private static string DescribeBlockingRows(SqliteConnection connection, SqliteException failure)
    {
        const int SqliteConstraint = 19;
        if (failure.SqliteErrorCode != SqliteConstraint)
            return string.Empty;
        try
        {
            using var command = connection.CreateCommand();
            // The known offender: environment names that differ only by case block the NOCASE
            // unique index added during state adoption.
            command.CommandText = """
                SELECT group_concat(conflict, ' | ') FROM (
                    SELECT group_concat(CustomName, ' / ') AS conflict
                    FROM Environments
                    GROUP BY CustomName COLLATE NOCASE, LLM
                    HAVING COUNT(*) > 1
                );
                """;
            if (command.ExecuteScalar() is string rows && !string.IsNullOrWhiteSpace(rows))
                return " Blocking rows -- Environments entries whose names differ only by case, for the same LLM: "
                    + rows
                    + ". Rename all but one in each group so the names differ by more than case, then restart:"
                    + " the migration has recorded no completion receipt and retries automatically.";
        }
        catch (SqliteException)
        {
            // The table may not exist on this database, or it may be unreadable. Either way the
            // caller still gets the original, accurate migration failure.
        }
        return string.Empty;
    }

    private static bool IsLockContention(SqliteException exception) => exception.SqliteErrorCode is 5 or 6;

    private static int ReadBusyTimeout(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout;";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void SetBusyTimeout(SqliteConnection connection, int milliseconds)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=" + milliseconds.ToString(CultureInfo.InvariantCulture) + ";";
        command.ExecuteNonQuery();
    }

    private static bool IsApplied(SqliteConnection connection, SqliteTransaction? transaction, string component, int version)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM sqlite_schema WHERE type='table' AND name='SchemaMigrations';";
        if (command.ExecuteScalar() is null)
            return false;
        command.CommandText = "SELECT 1 FROM SchemaMigrations WHERE Component=$component AND Version=$version;";
        command.Parameters.AddWithValue("$component", component);
        command.Parameters.AddWithValue("$version", version);
        return command.ExecuteScalar() is not null;
    }
}
