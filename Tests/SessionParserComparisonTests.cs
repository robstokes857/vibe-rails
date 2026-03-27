using System.Text;
using Microsoft.Data.Sqlite;
using VibeRails.DTOs;
using VibeRails.Services;
using Xunit;

namespace Tests;

/// <summary>
/// Compares SessionParseV3 (structured transcript extraction) vs SessionParseV2
/// (raw terminal dump) using the same input so the behavioral difference is clear.
/// </summary>
public class SessionParserComparisonTests(ITestOutputHelper output)
{
    // A session that mixes shell startup noise with real assistant output.
    private static readonly byte[] SampleSession = Encoding.UTF8.GetBytes(
        "PowerShell 7.5.4\r\n" +
        "Loading personal and system profiles took 1234ms.\r\n" +
        "Here is the answer to your question.\r\n");

    [Fact]
    public async Task SessionParseV3_FiltersShellNoiseAndKeepsAssistantContent()
    {
        var parser = new SessionParseV3();
        var chunks = new List<SessionLogChunkRecord>
        {
            new(1, DateTime.UtcNow, SampleSession)
        };

        var result = await parser.ParseAsync(chunks, CancellationToken.None);

        Assert.DoesNotContain("PowerShell 7.5.4", result);
        Assert.Contains("Here is the answer to your question.", result);
    }

    [Fact]
    public async Task SessionParseV2_PreservesAllTerminalOutputIncludingNoise()
    {
        var parser = new SessionParseV2();
        var chunks = new List<SessionLogChunkRecord>
        {
            new(1, DateTime.UtcNow, SampleSession)
        };

        var result = await parser.ParseAsync(chunks, CancellationToken.None);

        Assert.Contains("PowerShell 7.5.4", result);
        Assert.Contains("Here is the answer to your question.", result);
    }

    /// <summary>
    /// Pulls the most recent session with log data from the real DB and shows both parsers' output.
    /// Not a correctness test — run it to visually inspect parser behaviour on real data.
    /// </summary>
    [Fact]
    public async Task RealSession_BothParsers_ShowOutput()
    {
        var dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".vibe_rails", "state.db");

        Assert.SkipWhen(!File.Exists(dbPath), $"Real DB not found at {dbPath}");

        var connectionString = $"Data Source={dbPath};Mode=ReadOnly;Cache=Shared";

        string? sessionId = null;
        List<SessionLogChunkRecord> chunks = [];

        await using (var conn = new SqliteConnection(connectionString))
        {
            await conn.OpenAsync();

            // Find the most recent session that actually has log data.
            await using var sessionCmd = conn.CreateCommand();
            sessionCmd.CommandText = """
                SELECT s.Id
                FROM Sessions s
                WHERE EXISTS (SELECT 1 FROM SessionLogs l WHERE l.SessionId = s.Id)
                ORDER BY s.StartedUTC DESC
                LIMIT 1;
                """;
            sessionId = (string?)await sessionCmd.ExecuteScalarAsync();

            Assert.SkipWhen(sessionId is null, "No sessions with log data found in DB.");

            await using var chunkCmd = conn.CreateCommand();
            chunkCmd.CommandText = """
                SELECT Id, Timestamp, Content
                FROM SessionLogs
                WHERE SessionId = $sessionId
                ORDER BY Id ASC;
                """;
            chunkCmd.Parameters.AddWithValue("$sessionId", sessionId);

            await using var reader = await chunkCmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                chunks.Add(new SessionLogChunkRecord(
                    Id: reader.GetInt64(0),
                    TimestampUtc: DateTime.Parse(reader.GetString(1), null, System.Globalization.DateTimeStyles.RoundtripKind),
                    Content: (byte[])reader.GetValue(2)));
            }
        }

        output.WriteLine($"Session: {sessionId}  ({chunks.Count} chunks)");
        output.WriteLine(new string('=', 60));

        var v3Result = await new SessionParseV3().ParseAsync(chunks, CancellationToken.None);
        output.WriteLine("--- SessionParseV3 (structured transcript) ---");
        output.WriteLine(v3Result);
        output.WriteLine(new string('=', 60));

        var v2Result = await new SessionParseV2().ParseAsync(chunks, CancellationToken.None);
        output.WriteLine("--- SessionParseV2 (raw terminal dump) ---");
        output.WriteLine(v2Result);

        // Not asserting correctness — the test is a visual inspection tool.
        Assert.True(chunks.Count > 0);
    }
}
