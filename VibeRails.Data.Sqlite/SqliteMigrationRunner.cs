using System.Globalization;
using Microsoft.Data.Sqlite;
using Serilog;
using VibeRails.Data.Abstractions;

namespace VibeRails.Data.Sqlite;

/// <summary>
/// Applies each component migration once per database, including across independent vb processes.
/// The schema changes and their completion record share one SQLite write transaction.
/// </summary>
internal static class SqliteMigrationRunner
{
    internal const int MigrationLockTimeoutSeconds = 60;

    internal static bool Apply(SqliteConnection connection, string component, int version,
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
            Log.Information("[Database] Applying migration {Component}/{Version} to {Database}; waiting up to {WaitSeconds}s for database locks.",
                component, version, connection.DataSource, lockTimeoutSeconds);
            var applied = ApplyPending(connection, component, version, migration);
            Log.Information("[Database] Migration {Component}/{Version} {Result} for {Database}.",
                component, version, applied ? "completed" : "completed in another process", connection.DataSource);
            return applied;
        }
        catch (SqliteException ex) when (IsLockContention(ex))
        {
            throw new StorageException(
                $"Database migration '{component}/{version}' could not obtain a database lock within {lockTimeoutSeconds} seconds for '{connection.DataSource}'. " +
                "Close other VibeRails instances that use this database and retry.", true, ex);
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

    private static bool ApplyPending(SqliteConnection connection, string component, int version,
        Action<SqliteConnection, SqliteTransaction> migration)
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
        if (IsApplied(connection, transaction, component, version))
        {
            transaction.Commit();
            return false;
        }

        migration(connection, transaction);
        using (var record = connection.CreateCommand())
        {
            record.Transaction = transaction;
            record.CommandText = "INSERT INTO SchemaMigrations(Component, Version, AppliedUTC) VALUES ($component, $version, $utc);";
            record.Parameters.AddWithValue("$component", component);
            record.Parameters.AddWithValue("$version", version);
            record.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
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
