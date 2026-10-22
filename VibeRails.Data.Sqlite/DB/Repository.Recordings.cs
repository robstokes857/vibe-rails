using Microsoft.Data.Sqlite;
using System.Globalization;
using VibeRails.DTOs;

namespace VibeRails.DB;

public partial class Repository
{
    /// <inheritdoc />
    public async Task<SessionRecordingData?> GetSessionRecordingAsync(
        string sessionId, SessionRecordingKind kind, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        // Deferred read transaction: the cap and contents share a snapshot while active writers
        // continue in WAL mode. Never hold this transaction open while sending to a slow client.
        using var transaction = connection.BeginTransaction(deferred: true);
        using var exists = connection.CreateCommand();
        exists.Transaction = transaction;
        exists.CommandText = "SELECT 1 FROM Sessions WHERE Id = $sessionId";
        exists.Parameters.AddWithValue("$sessionId", sessionId);
        if (await exists.ExecuteScalarAsync(cancellationToken) is null) return null;

        var result = new SessionRecordingData([], [], []);
        long bytes = 0;
        var rows = 0;
        void Charge(long length)
        {
            if (++rows > SessionRecordingData.MaximumRows || length > SessionRecordingData.MaximumBytes - bytes)
                throw new SessionRecordingTooLargeException();
            bytes += length;
        }

        await ReadAsync("SELECT Id, Timestamp, Content, IsError, length(Content) FROM SessionLogs WHERE SessionId = $sessionId", reader =>
        {
            Charge(reader.GetInt64(4));
            result.Chunks.Add(new(reader.GetInt64(0), ReadDate(reader, 1), (byte[])reader[2], reader.GetBoolean(3)));
        });
        result.Chunks.Sort((a, b) => a.Id.CompareTo(b.Id));

        if (kind == SessionRecordingKind.Raw)
        {
            await ReadAsync("""
                SELECT Id, Sequence, InputText, GitCommitHash, TimestampUTC,
                       length(CAST(InputText AS BLOB)) + coalesce(length(CAST(GitCommitHash AS BLOB)), 0)
                FROM UserInputs WHERE SessionId = $sessionId
                """, reader =>
            {
                Charge(reader.GetInt64(5));
                result.Inputs.Add(new(reader.GetInt64(0), sessionId, reader.GetInt32(1), reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3), ReadDate(reader, 4)));
            });
            result.Inputs.Sort((a, b) => a.Sequence.CompareTo(b.Sequence));
        }
        if (kind == SessionRecordingKind.TerminalReplay)
        {
            await ReadAsync("""
                SELECT Id, Sequence, IsAlternateScreen, Data, Cols, Rows, Timestamp, length(Data)
                FROM TerminalSessionLogs WHERE SessionId = $sessionId
                """, reader =>
            {
                Charge(reader.GetInt64(7));
                result.TerminalLogs.Add(new(reader.GetInt64(0), sessionId, reader.GetInt32(1), reader.GetBoolean(2),
                    (byte[])reader[3], reader.GetInt32(4), reader.GetInt32(5), ReadDate(reader, 6)));
            });
            result.TerminalLogs.Sort((a, b) => a.Sequence.CompareTo(b.Sequence));
        }
        return result;

        async Task ReadAsync(string sql, Action<SqliteDataReader> add)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.Parameters.AddWithValue("$sessionId", sessionId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                add(reader);
            }
        }
    }

    private static DateTime ReadDate(SqliteDataReader reader, int ordinal) =>
        DateTime.Parse(reader.GetString(ordinal), null, DateTimeStyles.RoundtripKind);
}
