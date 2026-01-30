using Microsoft.Data.Sqlite;
using VibeRails.DTOs;
using VibeRails.Interfaces;
using VibeRails.Utils;

namespace VibeRails.Services
{

    // Note: SQLite handles concurrency via WAL mode and built-in locking.
    // Safe for multi-thread and multi-process access.
    public class DbService : IDbService
    {
        private readonly string _connectionString;

        public DbService()
        {
            _connectionString = $"Data Source={Configs.GetStatePath()};Mode=ReadWriteCreate;Cache=Shared";
        }

        public void InitializeDatabase()
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            // Enable WAL mode for better concurrent access
            using (var walCmd = connection.CreateCommand())
            {
                walCmd.CommandText = "PRAGMA journal_mode=WAL;";
                walCmd.ExecuteNonQuery();
            }

            // Execute each CREATE TABLE separately to ensure all tables are created
            var statements = new[]
            {
                """
                CREATE TABLE IF NOT EXISTS Sessions (
                    Id TEXT PRIMARY KEY,
                    Cli TEXT NOT NULL,
                    EnvironmentName TEXT,
                    WorkingDirectory TEXT NOT NULL,
                    StartedUTC TEXT NOT NULL,
                    EndedUTC TEXT,
                    ExitCode INTEGER
                )
                """,
                "CREATE INDEX IF NOT EXISTS idx_sessions_started ON Sessions(StartedUTC DESC)",
                """
                CREATE TABLE IF NOT EXISTS SessionLogs (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    SessionId TEXT NOT NULL,
                    Timestamp TEXT NOT NULL,
                    Content TEXT NOT NULL,
                    IsError INTEGER NOT NULL DEFAULT 0,
                    FOREIGN KEY (SessionId) REFERENCES Sessions(Id)
                )
                """,
                "CREATE INDEX IF NOT EXISTS idx_session_logs_session ON SessionLogs(SessionId)",
                // User Input tracking tables
                """
                CREATE TABLE IF NOT EXISTS UserInputs (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    SessionId TEXT NOT NULL,
                    Sequence INTEGER NOT NULL,
                    InputText TEXT NOT NULL,
                    GitCommitHash TEXT,
                    TimestampUTC TEXT NOT NULL,
                    FOREIGN KEY (SessionId) REFERENCES Sessions(Id)
                )
                """,
                "CREATE INDEX IF NOT EXISTS idx_user_inputs_session ON UserInputs(SessionId)",
                "CREATE INDEX IF NOT EXISTS idx_user_inputs_session_seq ON UserInputs(SessionId, Sequence)",
                """
                CREATE TABLE IF NOT EXISTS InputFileChanges (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    UserInputId INTEGER NOT NULL,
                    PreviousInputId INTEGER,
                    FilePath TEXT NOT NULL,
                    ChangeType TEXT NOT NULL,
                    LinesAdded INTEGER,
                    LinesDeleted INTEGER,
                    DiffContent TEXT,
                    FOREIGN KEY (UserInputId) REFERENCES UserInputs(Id),
                    FOREIGN KEY (PreviousInputId) REFERENCES UserInputs(Id)
                )
                """,
                "CREATE INDEX IF NOT EXISTS idx_input_file_changes_input ON InputFileChanges(UserInputId)",
                "CREATE INDEX IF NOT EXISTS idx_input_file_changes_filepath ON InputFileChanges(FilePath)"
            };

            foreach (var sql in statements)
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
        }



        // Session logging methods for LMBootstrap

        public async Task CreateSessionAsync(string sessionId, string cli, string? envName, string workDir)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO Sessions (Id, Cli, EnvironmentName, WorkingDirectory, StartedUTC)
                VALUES ($id, $cli, $envName, $workDir, $startedUTC);
                """;

            cmd.Parameters.AddWithValue("$id", sessionId);
            cmd.Parameters.AddWithValue("$cli", cli);
            cmd.Parameters.AddWithValue("$envName", envName ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$workDir", workDir);
            cmd.Parameters.AddWithValue("$startedUTC", DateTime.UtcNow.ToString("O"));

            await cmd.ExecuteNonQueryAsync();
        }

        public async Task LogSessionOutputAsync(string sessionId, string content, bool isError = false)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO SessionLogs (SessionId, Timestamp, Content, IsError)
                VALUES ($sessionId, $timestamp, $content, $isError);
                """;

            cmd.Parameters.AddWithValue("$sessionId", sessionId);
            cmd.Parameters.AddWithValue("$timestamp", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$content", content);
            cmd.Parameters.AddWithValue("$isError", isError ? 1 : 0);

            await cmd.ExecuteNonQueryAsync();
        }

        public async Task CompleteSessionAsync(string sessionId, int exitCode)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                UPDATE Sessions
                SET EndedUTC = $endedUTC, ExitCode = $exitCode
                WHERE Id = $id;
                """;

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
            sessionCmd.CommandText = """
                SELECT Id, Cli, EnvironmentName, WorkingDirectory, StartedUTC, EndedUTC, ExitCode
                FROM Sessions
                WHERE Id = $id;
                """;
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
            logsCmd.CommandText = """
                SELECT Id, SessionId, Timestamp, Content, IsError
                FROM SessionLogs
                WHERE SessionId = $sessionId
                ORDER BY Id ASC;
                """;
            logsCmd.Parameters.AddWithValue("$sessionId", sessionId);

            await using (var reader = await logsCmd.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    logs.Add(new SessionLogResponse(
                        Id: reader.GetInt64(0),
                        SessionId: reader.GetString(1),
                        Timestamp: DateTime.Parse(reader.GetString(2), null, System.Globalization.DateTimeStyles.RoundtripKind),
                        Content: reader.GetString(3),
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
            cmd.CommandText = """
                SELECT Id, Cli, EnvironmentName, WorkingDirectory, StartedUTC, EndedUTC, ExitCode
                FROM Sessions
                ORDER BY StartedUTC DESC
                LIMIT $limit;
                """;
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

        // User input tracking methods

        public async Task<UserInputRecord?> GetLastUserInputAsync(string sessionId)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT Id, SessionId, Sequence, InputText, GitCommitHash, TimestampUTC
                FROM UserInputs
                WHERE SessionId = $sessionId
                ORDER BY Sequence DESC
                LIMIT 1;
                """;
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
            cmd.CommandText = """
                INSERT INTO UserInputs (SessionId, Sequence, InputText, GitCommitHash, TimestampUTC)
                VALUES ($sessionId, $sequence, $inputText, $gitCommitHash, $timestampUTC)
                RETURNING Id;
                """;
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
                    cmd.CommandText = """
                        INSERT INTO InputFileChanges (UserInputId, PreviousInputId, FilePath, ChangeType, LinesAdded, LinesDeleted, DiffContent)
                        VALUES ($userInputId, $previousInputId, $filePath, $changeType, $linesAdded, $linesDeleted, $diffContent);
                        """;
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
            }
            catch (Exception ex)
            {
                // Log but don't fail the user's session
                Console.Error.WriteLine($"[VibeRails] Error recording user input: {ex.Message}");
            }
        }
    }
}
