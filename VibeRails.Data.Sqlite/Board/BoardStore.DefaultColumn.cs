using Microsoft.Data.Sqlite;

namespace VibeRails.Services.Board;

public sealed partial class BoardStore
{
    // Call only after resolving the lane's board and checking it is writable, in the lane's
    // write transaction. One board value makes replacing the previous choice atomic.
    private static async Task SetDefaultColumnAsync(SqliteConnection connection, SqliteTransaction transaction,
        string boardId, string columnId, bool selected, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE Boards SET DefaultColumnId = $default
            WHERE Id = $board AND ($selected = 1 OR DefaultColumnId = $column);
            """;
        command.Parameters.AddWithValue("$default", selected ? columnId : DBNull.Value);
        command.Parameters.AddWithValue("$board", boardId);
        command.Parameters.AddWithValue("$column", columnId);
        command.Parameters.AddWithValue("$selected", selected ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
