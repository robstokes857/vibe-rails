using System.Diagnostics;
using Microsoft.Data.Sqlite;
using VibeRails.DB;
using VibeRails.DTOs;
using Xunit;

namespace Tests.DB;

public sealed class SessionRecordingLimitsTests
{
    [Theory]
    [InlineData("legacy", SessionRecordingKind.Raw)]
    [InlineData("legacy", SessionRecordingKind.Replay)]
    [InlineData("terminal", SessionRecordingKind.TerminalReplay)]
    [InlineData("input", SessionRecordingKind.Raw)]
    public async Task OversizedRowIsRejectedBeforeMaterialization(string table, SessionRecordingKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection($"Data Source=recording-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        await connection.OpenAsync(ct);
        var repository = new Repository(connection.ConnectionString);
        await repository.CreateSessionAsync("session", "codex", null, "C:/project", 123);
        await using var insert = connection.CreateCommand();
        insert.CommandText = table switch
        {
            "legacy" => "INSERT INTO SessionLogs(SessionId, Timestamp, Content, IsError) VALUES ('session', $time, zeroblob($size), 0)",
            "terminal" => "INSERT INTO TerminalSessionLogs(SessionId, Sequence, IsAlternateScreen, Data, Cols, Rows, Timestamp) VALUES ('session', 1, 0, zeroblob($size), 80, 24, $time)",
            _ => "INSERT INTO UserInputs(SessionId, Sequence, InputText, TimestampUTC) VALUES ('session', 1, CAST(zeroblob($size) AS TEXT), $time)"
        };
        insert.Parameters.AddWithValue("$size", SessionRecordingData.MaximumBytes + 1);
        insert.Parameters.AddWithValue("$time", DateTime.UtcNow.ToString("O"));
        await insert.ExecuteNonQueryAsync(ct);

        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAsync<SessionRecordingTooLargeException>(() => repository.GetSessionRecordingAsync("session", kind, ct));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"Recording rejection took {sw.Elapsed}");
    }

    [Fact]
    public async Task EmptyChunksStillConsumeTheRowBudget()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection($"Data Source=recording-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        await connection.OpenAsync(ct);
        var repository = new Repository(connection.ConnectionString);
        await repository.CreateSessionAsync("session", "codex", null, "C:/project", 123);
        await using var insert = connection.CreateCommand();
        insert.CommandText = """
            WITH RECURSIVE numbers(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM numbers WHERE x < $count)
            INSERT INTO SessionLogs(SessionId, Timestamp, Content, IsError)
            SELECT 'session', $time, zeroblob(0), 0 FROM numbers
            """;
        insert.Parameters.AddWithValue("$count", SessionRecordingData.MaximumRows + 1);
        insert.Parameters.AddWithValue("$time", DateTime.UtcNow.ToString("O"));
        await insert.ExecuteNonQueryAsync(ct);
        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAsync<SessionRecordingTooLargeException>(() => repository.GetSessionRecordingAsync("session", SessionRecordingKind.Raw, ct));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"Row-bound rejection took {sw.Elapsed}");
    }

    [Fact]
    public async Task ByteBudgetIsSharedAcrossChunksAndTablesAndAllowsTheBoundary()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection($"Data Source=recording-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        await connection.OpenAsync(ct);
        var repository = new Repository(connection.ConnectionString);
        await repository.CreateSessionAsync("session", "codex", null, "C:/project", 123);
        await repository.LogSessionOutputAsync("session", new byte[SessionRecordingData.MaximumBytes]);
        var boundary = await repository.GetSessionRecordingAsync("session", SessionRecordingKind.Raw, ct);
        Assert.Equal(SessionRecordingData.MaximumBytes, Assert.Single(boundary!.Chunks).Content.Length);
        await repository.InsertTerminalSessionLogAsync("session", 1, [1], false, 80, 24);
        await Assert.ThrowsAsync<SessionRecordingTooLargeException>(() => repository.GetSessionRecordingAsync("session", SessionRecordingKind.TerminalReplay, ct));
        await using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO UserInputs(SessionId, Sequence, InputText, TimestampUTC) VALUES ('session', 1, 'x', $time)";
        insert.Parameters.AddWithValue("$time", DateTime.UtcNow.ToString("O"));
        await insert.ExecuteNonQueryAsync(ct);
        await Assert.ThrowsAsync<SessionRecordingTooLargeException>(() => repository.GetSessionRecordingAsync("session", SessionRecordingKind.Raw, ct));
    }
}
