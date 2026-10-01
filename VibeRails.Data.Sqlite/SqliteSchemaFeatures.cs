using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;

namespace VibeRails.Data.Sqlite;

/// <summary>
/// Remembers which optional tables and columns one database file has been seen to contain, so a
/// read path that must tolerate an older or partially initialised file (a stdio MCP host opening
/// a state.db that has never held Automations) pays for each probe once instead of on every call.
///
/// Only presence is remembered. The schema policy never drops a table or column, so a feature
/// that exists keeps existing for the life of the process; a feature that is missing can appear
/// at any moment when another process migrates the file, so a negative answer is asked again
/// next time. Keep one instance per store, not a static per path: tests reuse paths with
/// different schemas, and a singleton store already gives production a process-wide memo.
/// </summary>
internal sealed class SqliteSchemaFeatures
{
    private readonly ConcurrentDictionary<string, bool> _present = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Probes the column through <see cref="SqliteSchema.HasColumn"/> until it is first seen.</summary>
    internal bool HasColumn(SqliteConnection connection, string table, string column)
    {
        var key = "column:" + table + "." + column;
        if (_present.ContainsKey(key))
            return true;
        if (!SqliteSchema.HasColumn(connection, null, table, column))
            return false;
        _present[key] = true;
        return true;
    }

    /// <summary>Probes <c>sqlite_master</c> for the table until it is first seen.</summary>
    internal async Task<bool> HasTableAsync(SqliteConnection connection, string table, CancellationToken cancellationToken)
    {
        var key = "table:" + table;
        if (_present.ContainsKey(key))
            return true;
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $table LIMIT 1;";
        command.Parameters.AddWithValue("$table", table);
        if (await command.ExecuteScalarAsync(cancellationToken) is null)
            return false;
        _present[key] = true;
        return true;
    }
}
