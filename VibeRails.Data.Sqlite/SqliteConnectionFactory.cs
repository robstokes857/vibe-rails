using Microsoft.Data.Sqlite;

namespace VibeRails.Data.Sqlite;

/// <summary>Connection policy shared by every SQLite store. Connections are owned by the caller.</summary>
internal static class SqliteConnectionFactory
{
    internal const int BusyTimeoutMilliseconds = 5000;

    internal static SqliteConnection Create(string connectionString, bool readOnly = false, bool foreignKeys = true)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString);
        // Named shared-memory databases need shared cache to refer to the same database.
        // File databases use WAL; shared-cache table locks defeat its reader/writer concurrency.
        var inMemory = builder.Mode == SqliteOpenMode.Memory || builder.DataSource == ":memory:";
        if (!inMemory)
            builder.Cache = SqliteCacheMode.Private;
        if (readOnly && !inMemory)
            builder.Mode = SqliteOpenMode.ReadOnly;
        builder.ForeignKeys = foreignKeys;
        builder.DefaultTimeout = BusyTimeoutMilliseconds / 1000;
        return new SqliteConnection(builder.ToString());
    }

    internal static SqliteConnection Open(string connectionString, bool readOnly = false, bool foreignKeys = true)
    {
        var connection = Create(connectionString, readOnly, foreignKeys);
        try
        {
            connection.Open();
            Configure(connection);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    internal static async Task<SqliteConnection> OpenAsync(
        string connectionString, CancellationToken cancellationToken = default,
        bool readOnly = false, bool foreignKeys = true)
    {
        var connection = Create(connectionString, readOnly, foreignKeys);
        try
        {
            await connection.OpenAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Configure(connection);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Restores WAL if the file was left in another journal mode. journal_mode is a persistent
    /// property of the database, not of the connection, so an external tool -- the vacuum script,
    /// a bare sqlite3 session -- can flip it and nothing puts it back: the migration runner only
    /// sets WAL when it actually has a migration to apply. Read first, so the ordinary case takes
    /// no write lock. Must be called outside a transaction; journal_mode cannot change inside one.
    /// </summary>
    internal static void EnsureWalMode(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        if (command.ExecuteScalar() is string mode && mode.Equals("wal", StringComparison.OrdinalIgnoreCase))
            return;
        command.CommandText = "PRAGMA journal_mode=WAL;";
        command.ExecuteScalar();
    }

    internal static void Configure(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=5000; PRAGMA journal_size_limit=67108864;";
        command.ExecuteNonQuery();
    }
}
