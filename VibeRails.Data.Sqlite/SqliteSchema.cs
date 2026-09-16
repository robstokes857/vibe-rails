using Microsoft.Data.Sqlite;

namespace VibeRails.Data.Sqlite;

/// <summary>Guarded adoption helpers for schemas created before the migration ledger existed.</summary>
internal static class SqliteSchema
{
    internal static bool HasColumn(SqliteConnection connection, SqliteTransaction? transaction, string table, string column)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM pragma_table_info($table) WHERE name=$column COLLATE NOCASE;";
        command.Parameters.AddWithValue("$table", table);
        command.Parameters.AddWithValue("$column", column);
        return command.ExecuteScalar() is not null;
    }

    internal static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    internal static bool AdoptStatement(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        // Only application-owned historical SQL reaches this helper. Explicit column inspection
        // replaces catching SQLITE_ERROR, which also hides syntax and missing-table failures.
        var words = sql.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length >= 6 && words[0].Equals("ALTER", StringComparison.OrdinalIgnoreCase)
            && words[1].Equals("TABLE", StringComparison.OrdinalIgnoreCase)
            && words[4].Equals("COLUMN", StringComparison.OrdinalIgnoreCase))
        {
            var exists = HasColumn(connection, transaction, words[2], words[5].TrimEnd(';'));
            if ((words[3].Equals("ADD", StringComparison.OrdinalIgnoreCase) && exists)
                || (words[3].Equals("DROP", StringComparison.OrdinalIgnoreCase) && !exists))
                return false;
        }
        Execute(connection, transaction, sql);
        return true;
    }
}
