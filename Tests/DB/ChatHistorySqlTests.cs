using Microsoft.Data.Sqlite;
using VibeRails.DB;
using Xunit;

namespace Tests.DB;

public class ChatHistorySqlTests
{
    [Fact]
    public async Task SelectChatHistoryBySessionId_UsesCleanedPreviewWhenAvailable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        await InitializeSchemaAsync(connection, cancellationToken);

        var sessionId = Guid.NewGuid().ToString();
        var startedUtc = DateTime.UtcNow.ToString("O");
        await InsertSessionAsync(connection, sessionId, startedUtc, cancellationToken);

        var userInputId = await InsertUserInputAsync(connection, sessionId, "raw preview", cancellationToken);
        var cleanedId = await InsertCleanedInputAsync(connection, sessionId, userInputId, "cleaned preview", cancellationToken);
        await LinkCleanedInputAsync(connection, userInputId, cleanedId, cancellationToken);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = SqlStrings.SelectChatHistoryBySessionId;
        cmd.Parameters.AddWithValue("$sessionId", sessionId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));
        Assert.Equal("cleaned preview", reader.GetString(12));
    }

    [Fact]
    public async Task SelectChatHistoryBase_FallsBackToRawPreviewWhenInputIsUncleaned()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        await InitializeSchemaAsync(connection, cancellationToken);

        var sessionId = Guid.NewGuid().ToString();
        var startedUtc = DateTime.UtcNow.ToString("O");
        await InsertSessionAsync(connection, sessionId, startedUtc, cancellationToken);
        await InsertUserInputAsync(connection, sessionId, "raw preview", cancellationToken);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT *
            FROM (
            """ + SqlStrings.SelectChatHistoryBase + """
            )
            ORDER BY StartedUTC DESC
            LIMIT $limit OFFSET $offset;
            """;
        cmd.Parameters.AddWithValue("$limit", 10);
        cmd.Parameters.AddWithValue("$offset", 0);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));
        Assert.Equal("raw preview", reader.GetString(12));
    }

    private static async Task InitializeSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using (var foreignKeys = connection.CreateCommand())
        {
            foreignKeys.CommandText = SqlStrings.PragmaForeignKeys;
            await foreignKeys.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var sql in SqlStrings.InitStatements)
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var migration in SqlStrings.MigrationStatements)
        {
            try
            {
                await using var cmd = connection.CreateCommand();
                cmd.CommandText = migration;
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (SqliteException)
            {
                // Migrations are safe to re-run; ignore already-applied errors.
            }
        }
    }

    private static async Task InsertSessionAsync(SqliteConnection connection, string sessionId, string startedUtc, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = SqlStrings.InsertSession;
        cmd.Parameters.AddWithValue("$id", sessionId);
        cmd.Parameters.AddWithValue("$cli", "Codex");
        cmd.Parameters.AddWithValue("$envName", DBNull.Value);
        cmd.Parameters.AddWithValue("$workDir", "C:\\source\\VibeControl2");
        cmd.Parameters.AddWithValue("$projectDisplayName", "VibeControl2");
        cmd.Parameters.AddWithValue("$startedUTC", startedUtc);
        cmd.Parameters.AddWithValue("$ownerPid", 1234);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<long> InsertUserInputAsync(SqliteConnection connection, string sessionId, string inputText, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = SqlStrings.InsertUserInput;
        cmd.Parameters.AddWithValue("$sessionId", sessionId);
        cmd.Parameters.AddWithValue("$sequence", 1);
        cmd.Parameters.AddWithValue("$inputText", inputText);
        cmd.Parameters.AddWithValue("$gitCommitHash", DBNull.Value);
        cmd.Parameters.AddWithValue("$timestampUTC", DateTime.UtcNow.ToString("O"));
        return (long)(await cmd.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task<long> InsertCleanedInputAsync(SqliteConnection connection, string sessionId, long userInputId, string cleanedText, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = SqlStrings.InsertCleanedUserInputAndLink;
        cmd.Parameters.AddWithValue("$sessionId", sessionId);
        cmd.Parameters.AddWithValue("$userInputId", userInputId);
        cmd.Parameters.AddWithValue("$cleanedText", cleanedText);
        cmd.Parameters.AddWithValue("$createdUTC", DateTime.UtcNow.ToString("O"));
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task LinkCleanedInputAsync(SqliteConnection connection, long userInputId, long cleanedId, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = SqlStrings.UpdateUserInputCleanedId;
        cmd.Parameters.AddWithValue("$cleanedId", cleanedId);
        cmd.Parameters.AddWithValue("$userInputId", userInputId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
