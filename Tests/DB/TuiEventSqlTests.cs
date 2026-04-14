using Microsoft.Data.Sqlite;
using VibeRails.DB;
using Xunit;

namespace Tests.DB;

public class TuiEventSqlTests
{
    [Fact]
    public async Task InitStatements_CreateTuiEventTable_AndInsertStatementPersistsRows()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using (var foreignKeys = connection.CreateCommand())
        {
            foreignKeys.CommandText = SqlStrings.PragmaForeignKeys;
            await foreignKeys.ExecuteNonQueryAsync();
        }

        foreach (var sql in SqlStrings.InitStatements)
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var tableCheck = connection.CreateCommand())
        {
            tableCheck.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'TUI_Event';";
            var tableName = await tableCheck.ExecuteScalarAsync();
            Assert.Equal("TUI_Event", tableName);
        }

        var sessionId = Guid.NewGuid().ToString();
        var timestampUtc = DateTimeOffset.UtcNow.ToString("O");

        await using (var insertSession = connection.CreateCommand())
        {
            insertSession.CommandText = SqlStrings.InsertSession;
            insertSession.Parameters.AddWithValue("$id", sessionId);
            insertSession.Parameters.AddWithValue("$cli", "Codex");
            insertSession.Parameters.AddWithValue("$envName", DBNull.Value);
            insertSession.Parameters.AddWithValue("$workDir", "C:\\source\\VibeControl2");
            insertSession.Parameters.AddWithValue("$projectDisplayName", "VibeControl2");
            insertSession.Parameters.AddWithValue("$startedUTC", timestampUtc);
            insertSession.Parameters.AddWithValue("$ownerPid", 1234);
            await insertSession.ExecuteNonQueryAsync();
        }

        await using (var insertEvent = connection.CreateCommand())
        {
            insertEvent.CommandText = SqlStrings.InsertTuiEvent;
            insertEvent.Parameters.AddWithValue("$sessionId", sessionId);
            insertEvent.Parameters.AddWithValue("$timestampUTC", timestampUtc);
            insertEvent.Parameters.AddWithValue("$triggerString", "\x1B[A");
            insertEvent.Parameters.AddWithValue("$eventType", "UpArrow");
            await insertEvent.ExecuteNonQueryAsync();
        }

        await using var readEvent = connection.CreateCommand();
        readEvent.CommandText = """
            SELECT SessionId, TimestampUTC, TriggerString, EventType
            FROM TUI_Event
            WHERE SessionId = $sessionId;
            """;
        readEvent.Parameters.AddWithValue("$sessionId", sessionId);

        await using var reader = await readEvent.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(sessionId, reader.GetString(0));
        Assert.Equal(timestampUtc, reader.GetString(1));
        Assert.Equal("\x1B[A", reader.GetString(2));
        Assert.Equal("UpArrow", reader.GetString(3));
    }
}
