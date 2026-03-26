using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Serilog;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Interfaces;
using VibeRails.Services.Bert;
using VibeRails.Utils;

namespace VibeRails.Services
{

    // Note: SQLite handles concurrency via WAL mode and built-in locking.
    // Safe for multi-thread and multi-process access.
    public class DbService : IDbService
    {
        private readonly string _connectionString;
        private readonly IBertInputCaptureService? _bertInputCaptureService;
        private readonly ILogger<DbService>? _logger;

        public DbService(IBertInputCaptureService? bertInputCaptureService = null, ILogger<DbService>? logger = null)
        {
            _connectionString = $"Data Source={ParserConfigs.GetStatePath()};Mode=ReadWriteCreate;Cache=Shared";
            _bertInputCaptureService = bertInputCaptureService;
            _logger = logger;
        }

        public void InitializeDatabase()
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using (var walCmd = connection.CreateCommand())
            {
                walCmd.CommandText = SqlStrings.PragmaWal;
                walCmd.ExecuteNonQuery();
            }

            foreach (var sql in SqlStrings.InitStatements)
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }

            foreach (var migration in SqlStrings.MigrationStatements)
            {
                try
                {
                    using var cmd = connection.CreateCommand();
                    cmd.CommandText = migration;
                    cmd.ExecuteNonQuery();

                    if (migration.Contains("Processed", StringComparison.Ordinal))
                    {
                        using var seedCmd = connection.CreateCommand();
                        seedCmd.CommandText = SqlStrings.SeedProcessedColumn;
                        seedCmd.ExecuteNonQuery();
                    }
                }
                catch (SqliteException ex) when (ex.Message.Contains("duplicate column name"))
                {
                    // Already migrated
                }
            }
        }



        // Session logging methods for terminal sessions

        public async Task CreateSessionAsync(string sessionId, string cli, string? envName, string workDir)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.InsertSession;

            cmd.Parameters.AddWithValue("$id", sessionId);
            cmd.Parameters.AddWithValue("$cli", cli);
            cmd.Parameters.AddWithValue("$envName", envName ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$workDir", workDir);
            cmd.Parameters.AddWithValue("$startedUTC", DateTime.UtcNow.ToString("O"));

            await cmd.ExecuteNonQueryAsync();
        }

        public async Task LogSessionOutputAsync(string sessionId, byte[] content, bool isError = false)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.InsertSessionLog;

            cmd.Parameters.AddWithValue("$sessionId", sessionId);
            cmd.Parameters.AddWithValue("$timestamp", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.Add(new SqliteParameter("$content", SqliteType.Blob) { Value = content });
            cmd.Parameters.AddWithValue("$isError", isError ? 1 : 0);

            await cmd.ExecuteNonQueryAsync();
        }

        public async Task<List<string>> GetOpenSessionIdsAsync(DateTime olderThan, CancellationToken cancellationToken)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.SelectOpenSessionIds;
            cmd.Parameters.AddWithValue("$cutoff", olderThan.ToString("O"));

            var ids = new List<string>();
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                ids.Add(reader.GetString(0));

            return ids;
        }

        public async Task CompleteSessionAsync(string sessionId, int exitCode)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.UpdateSessionEnd;

            cmd.Parameters.AddWithValue("$id", sessionId);
            cmd.Parameters.AddWithValue("$endedUTC", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$exitCode", exitCode);

            await cmd.ExecuteNonQueryAsync();
        }

        public async Task<SessionWithLogsResponse?> GetSessionWithLogsAsync(string sessionId, CancellationToken cancellationToken)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            // Get session
            await using var sessionCmd = connection.CreateCommand();
            sessionCmd.CommandText = SqlStrings.SelectSessionById;
            sessionCmd.Parameters.AddWithValue("$id", sessionId);

            SessionResponse? session = null;
            await using (var reader = await sessionCmd.ExecuteReaderAsync(cancellationToken))
            {
                if (await reader.ReadAsync(cancellationToken))
                {
                    session = new SessionResponse(
                        Id: reader.GetString(0),
                        Cli: reader.GetString(1),
                        EnvironmentName: reader.IsDBNull(2) ? null : reader.GetString(2),
                        WorkingDirectory: reader.GetString(3),
                        StartedUTC: DateTime.Parse(reader.GetString(4), null, System.Globalization.DateTimeStyles.RoundtripKind),
                        EndedUTC: reader.IsDBNull(5) ? null : DateTime.Parse(reader.GetString(5), null, System.Globalization.DateTimeStyles.RoundtripKind),
                        ExitCode: reader.IsDBNull(6) ? null : reader.GetInt32(6)
                    );
                }
            }

            if (session == null)
                return null;

            // Get logs
            var logs = new List<SessionLogResponse>();
            await using var logsCmd = connection.CreateCommand();
            logsCmd.CommandText = SqlStrings.SelectSessionLogsBySession;
            logsCmd.Parameters.AddWithValue("$sessionId", sessionId);

            await using (var reader = await logsCmd.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    logs.Add(new SessionLogResponse(
                        Id: reader.GetInt64(0),
                        SessionId: reader.GetString(1),
                        Timestamp: DateTime.Parse(reader.GetString(2), null, System.Globalization.DateTimeStyles.RoundtripKind),
                        Content: Convert.ToBase64String((byte[])reader.GetValue(3)),
                        IsError: reader.GetInt32(4) == 1
                    ));
                }
            }

            return new SessionWithLogsResponse(session, logs);
        }

        public async Task<List<SessionResponse>> GetRecentSessionsAsync(int limit, CancellationToken cancellationToken)
        {
            var sessions = new List<SessionResponse>();

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.SelectRecentSessions;
            cmd.Parameters.AddWithValue("$limit", limit);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                sessions.Add(new SessionResponse(
                    Id: reader.GetString(0),
                    Cli: reader.GetString(1),
                    EnvironmentName: reader.IsDBNull(2) ? null : reader.GetString(2),
                    WorkingDirectory: reader.GetString(3),
                    StartedUTC: DateTime.Parse(reader.GetString(4), null, System.Globalization.DateTimeStyles.RoundtripKind),
                    EndedUTC: reader.IsDBNull(5) ? null : DateTime.Parse(reader.GetString(5), null, System.Globalization.DateTimeStyles.RoundtripKind),
                    ExitCode: reader.IsDBNull(6) ? null : reader.GetInt32(6)
                ));
            }

            return sessions;
        }

        public async Task<SessionOutputDetailResponse?> GetSessionOutputAsync(string sessionId, CancellationToken cancellationToken)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.SelectSessionOutput;
            cmd.Parameters.AddWithValue("$sessionId", sessionId);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return null;

            return new SessionOutputDetailResponse(
                SessionId: reader.GetString(0),
                Cli: reader.GetString(1),
                EnvironmentName: reader.IsDBNull(2) ? null : reader.GetString(2),
                WorkingDirectory: reader.GetString(3),
                StartedUTC: DateTime.Parse(reader.GetString(4), null, System.Globalization.DateTimeStyles.RoundtripKind),
                EndedUTC: reader.IsDBNull(5) ? null : DateTime.Parse(reader.GetString(5), null, System.Globalization.DateTimeStyles.RoundtripKind),
                Processed: reader.GetInt32(6) == 1,
                Text: reader.GetString(7)
            );
        }

        public async Task<List<string>> GetEndedUnprocessedSessionIdsAsync(int limit, CancellationToken cancellationToken)
        {
            var sessionIds = new List<string>();

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.SelectEndedUnprocessedSessions;
            cmd.Parameters.AddWithValue("$limit", limit);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                sessionIds.Add(reader.GetString(0));
            }

            return sessionIds;
        }

        public async Task<List<SessionLogChunkRecord>> GetSessionLogChunksAsync(string sessionId, CancellationToken cancellationToken)
        {
            var chunks = new List<SessionLogChunkRecord>();

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.SelectSessionLogChunks;
            cmd.Parameters.AddWithValue("$sessionId", sessionId);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                chunks.Add(new SessionLogChunkRecord(
                    Id: reader.GetInt64(0),
                    TimestampUtc: DateTime.Parse(reader.GetString(1), null, System.Globalization.DateTimeStyles.RoundtripKind),
                    Content: (byte[])reader.GetValue(2)));
            }

            return chunks;
        }

        public async Task<List<UserInputRecord>> GetUserInputsForSessionAsync(string sessionId, CancellationToken cancellationToken)
        {
            var inputs = new List<UserInputRecord>();

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.SelectUserInputsBySession;
            cmd.Parameters.AddWithValue("$sessionId", sessionId);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                inputs.Add(new UserInputRecord(
                    Id: reader.GetInt64(0),
                    SessionId: reader.GetString(1),
                    Sequence: reader.GetInt32(2),
                    InputText: reader.GetString(3),
                    GitCommitHash: reader.IsDBNull(4) ? null : reader.GetString(4),
                    TimestampUTC: DateTime.Parse(reader.GetString(5), null, System.Globalization.DateTimeStyles.RoundtripKind)
                ));
            }

            return inputs;
        }

        public async Task SaveSessionOutputAndMarkProcessedAsync(string sessionId, string text, CancellationToken cancellationToken)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

            try
            {
                await using (var outputCmd = connection.CreateCommand())
                {
                    outputCmd.Transaction = transaction;
                    outputCmd.CommandText = SqlStrings.UpsertSessionOutput;
                    outputCmd.Parameters.AddWithValue("$sessionId", sessionId);
                    outputCmd.Parameters.AddWithValue("$text", text);
                    await outputCmd.ExecuteNonQueryAsync(cancellationToken);
                }

                await using (var sessionCmd = connection.CreateCommand())
                {
                    sessionCmd.Transaction = transaction;
                    sessionCmd.CommandText = SqlStrings.UpdateSessionProcessed;
                    sessionCmd.Parameters.AddWithValue("$sessionId", sessionId);
                    await sessionCmd.ExecuteNonQueryAsync(cancellationToken);
                }

                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
                throw;
            }
        }

        public async Task<List<ChatHistoryItem>> GetChatHistoryPageAsync(int limit, int offset, CancellationToken cancellationToken)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            var items = new List<ChatHistoryItem>();
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.SelectChatHistoryPage;
            cmd.Parameters.AddWithValue("$limit", limit);
            cmd.Parameters.AddWithValue("$offset", offset);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(new ChatHistoryItem(
                    Id: reader.GetString(0),
                    Cli: reader.GetString(1),
                    EnvironmentName: reader.IsDBNull(2) ? null : reader.GetString(2),
                    WorkingDirectory: reader.GetString(3),
                    StartedUTC: DateTime.Parse(reader.GetString(4), null, System.Globalization.DateTimeStyles.RoundtripKind),
                    EndedUTC: reader.IsDBNull(5) ? null : DateTime.Parse(reader.GetString(5), null, System.Globalization.DateTimeStyles.RoundtripKind),
                    ExitCode: reader.IsDBNull(6) ? null : reader.GetInt32(6),
                    ParentSessionId: reader.IsDBNull(7) ? null : reader.GetString(7),
                    SessionDisplayName: reader.IsDBNull(8) ? null : reader.GetString(8),
                    Sequence: reader.IsDBNull(9) ? null : reader.GetInt32(9),
                    InputText: reader.IsDBNull(10) ? null : reader.GetString(10)
                ));
            }

            return items;
        }

        public async Task<bool> UpdateChatHistorySessionNameAsync(string sessionId, string sessionDisplayName, CancellationToken cancellationToken)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.UpdateSessionDisplayName;
            cmd.Parameters.AddWithValue("$sessionId", sessionId);
            cmd.Parameters.AddWithValue("$sessionDisplayName", sessionDisplayName);

            var rowsAffected = await cmd.ExecuteNonQueryAsync(cancellationToken);
            return rowsAffected > 0;
        }

        public async Task<bool> DeleteChatHistorySessionAsync(string sessionId, CancellationToken cancellationToken)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

            try
            {
                var deletedSession = false;
                foreach (var sql in SqlStrings.DeleteSessionCommands)
                {
                    await using var cmd = connection.CreateCommand();
                    cmd.Transaction = transaction;
                    cmd.CommandText = sql;
                    cmd.Parameters.AddWithValue("$sessionId", sessionId);

                    var rowsAffected = await cmd.ExecuteNonQueryAsync(cancellationToken);
                    if (!deletedSession && sql.Contains("DELETE FROM Sessions", StringComparison.Ordinal))
                    {
                        deletedSession = rowsAffected > 0;
                    }
                }

                await transaction.CommitAsync(cancellationToken);
                return deletedSession;
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
                throw;
            }
        }

        // User input tracking methods

        public async Task<UserInputRecord?> GetLastUserInputAsync(string sessionId)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.SelectLastUserInput;
            cmd.Parameters.AddWithValue("$sessionId", sessionId);

            await using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return new UserInputRecord(
                    Id: reader.GetInt64(0),
                    SessionId: reader.GetString(1),
                    Sequence: reader.GetInt32(2),
                    InputText: reader.GetString(3),
                    GitCommitHash: reader.IsDBNull(4) ? null : reader.GetString(4),
                    TimestampUTC: DateTime.Parse(reader.GetString(5), null, System.Globalization.DateTimeStyles.RoundtripKind)
                );
            }
            return null;
        }

        public async Task<long> InsertUserInputAsync(string sessionId, int sequence, string inputText, string? gitCommitHash)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.InsertUserInput;
            cmd.Parameters.AddWithValue("$sessionId", sessionId);
            cmd.Parameters.AddWithValue("$sequence", sequence);
            cmd.Parameters.AddWithValue("$inputText", inputText);
            cmd.Parameters.AddWithValue("$gitCommitHash", gitCommitHash ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$timestampUTC", DateTime.UtcNow.ToString("O"));

            var result = await cmd.ExecuteScalarAsync();
            return (long)result!;
        }

        public async Task InsertFileChangesAsync(long userInputId, long? previousInputId, List<FileChangeInfo> changes)
        {
            if (changes.Count == 0) return;

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            await using var transaction = await connection.BeginTransactionAsync();
            try
            {
                foreach (var change in changes)
                {
                    await using var cmd = connection.CreateCommand();
                    cmd.Transaction = (SqliteTransaction)transaction;
                    cmd.CommandText = SqlStrings.InsertFileChange;
                    cmd.Parameters.AddWithValue("$userInputId", userInputId);
                    cmd.Parameters.AddWithValue("$previousInputId", previousInputId ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("$filePath", change.FilePath);
                    cmd.Parameters.AddWithValue("$changeType", change.ChangeType);
                    cmd.Parameters.AddWithValue("$linesAdded", change.LinesAdded ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("$linesDeleted", change.LinesDeleted ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("$diffContent", change.DiffContent ?? (object)DBNull.Value);
                    await cmd.ExecuteNonQueryAsync();
                }
                await transaction.CommitAsync();
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }

        public async Task RecordUserInputAsync(string sessionId, string inputText, IGitService gitService, CancellationToken cancellationToken = default)
        {
            try
            {
                // Get current commit hash
                var currentCommitHash = await gitService.GetCurrentCommitHashAsync(cancellationToken);

                // Get last input for this session
                var lastInput = await GetLastUserInputAsync(sessionId);
                var sequence = (lastInput?.Sequence ?? 0) + 1;

                // Calculate file changes if there was a previous input
                var fileChanges = new List<FileChangeInfo>();
                if (lastInput != null && !string.IsNullOrEmpty(lastInput.GitCommitHash))
                {
                    fileChanges = await gitService.GetFileChangesSinceAsync(lastInput.GitCommitHash, cancellationToken);
                }

                // Insert the user input
                var userInputId = await InsertUserInputAsync(sessionId, sequence, inputText, currentCommitHash);

                // Insert file changes
                if (fileChanges.Count > 0)
                {
                    await InsertFileChangesAsync(userInputId, lastInput?.Id, fileChanges);
                }

                if (_bertInputCaptureService != null)
                {
                    await _bertInputCaptureService.CaptureAsync(
                        sessionId,
                        userInputId,
                        inputText,
                        currentCommitHash,
                        fileChanges,
                        cancellationToken);
                }
            }
            catch (Exception ex)
            {
                // Log but don't fail the user's session
                if (_logger != null)
                {
                    _logger.LogWarning(ex, "[VibeRails] Error recording user input for session {SessionId}", sessionId);
                }
                else
                {
                    Log.Warning(ex, "[VibeRails] Error recording user input for session {SessionId}", sessionId);
                }
            }
        }   
    }
}

