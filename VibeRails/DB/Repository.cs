using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Serilog;
using VibeRails.DTOs;
using VibeRails.Interfaces;
using VibeRails.Services;
using VibeRails.Services.Bert;

namespace VibeRails.DB
{
    public class Repository : IRepository
    {
        private static bool _initialized;
        private static readonly object _initLock = new();
        private readonly string _connectionString;
        private readonly IBertInputCaptureService? _bertInputCaptureService;
        private readonly ILogger<Repository>? _logger;

        public Repository(string connectionString, IBertInputCaptureService? bertInputCaptureService = null, ILogger<Repository>? logger = null)
        {
            _connectionString = connectionString;
            _bertInputCaptureService = bertInputCaptureService;
            _logger = logger;
            EnsureInitialized();
        }

        public void InitializeDatabase() => EnsureInitialized();

        private void EnsureInitialized()
        {
            if (_initialized) return;

            lock (_initLock)
            {
                if (_initialized) return;

                using var connection = new SqliteConnection(_connectionString);
                connection.Open();

                using (var walCmd = connection.CreateCommand())
                {
                    walCmd.CommandText = SqlStrings.PragmaWal;
                    walCmd.ExecuteNonQuery();
                }

                using (var fkCmd = connection.CreateCommand())
                {
                    fkCmd.CommandText = SqlStrings.PragmaForeignKeys;
                    fkCmd.ExecuteNonQuery();
                }

                foreach (var sql in SqlStrings.InitStatements)
                {
                    using var cmd = connection.CreateCommand();
                    cmd.CommandText = sql;
                    cmd.ExecuteNonQuery();
                }

                // Run migrations (these are safe to re-run)
                foreach (var migration in SqlStrings.MigrationStatements)
                {
                    try
                    {
                        using var cmd = connection.CreateCommand();
                        cmd.CommandText = migration;
                        cmd.ExecuteNonQuery();

                        if (ReferenceEquals(migration, SqlStrings.AddProcessedColumn))
                        {
                            using var seedCmd = connection.CreateCommand();
                            seedCmd.CommandText = SqlStrings.SeedProcessedColumn;
                            seedCmd.ExecuteNonQuery();
                        }
                    }
                    catch (SqliteException)
                    {
                        // Ignore errors from migrations (e.g., column already exists)
                    }
                }

                _initialized = true;
            }
        }

        #region LLM_Environment CRUD (Global)

        public async Task<LLM_Environment?> GetEnvironmentByNameAndLlmAsync(string name, LLM llm, CancellationToken cancellationToken = default)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.SelectEnvironmentByNameAndLlm;
            cmd.Parameters.AddWithValue("$customName", name);
            cmd.Parameters.AddWithValue("$llm", (int)llm);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                return ReadEnvironment(reader);
            }

            return null;
        }

        public async Task<LLM_Environment?> FindEnvironmentByNameAsync(string name, CancellationToken cancellationToken = default)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.SelectEnvironmentByName;
            cmd.Parameters.AddWithValue("$customName", name);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                return ReadEnvironment(reader);
            }

            return null;
        }

        public async Task<LLM_Environment> GetOrCreateEnvironmentAsync(string name, LLM llm, CancellationToken cancellationToken = default)
        {
            var existing = await GetEnvironmentByNameAndLlmAsync(name, llm, cancellationToken);
            if (existing != null)
            {
                existing.LastUsedUTC = DateTime.UtcNow;
                await UpdateEnvironmentAsync(existing, cancellationToken);
                return existing;
            }

            var environment = new LLM_Environment
            {
                LLM = llm,
                CustomName = name,
                Path = "",
                CreatedUTC = DateTime.UtcNow,
                LastUsedUTC = DateTime.UtcNow
            };

            return await SaveEnvironmentAsync(environment, cancellationToken);
        }

        public async Task<List<LLM_Environment>> GetAllEnvironmentsAsync(CancellationToken cancellationToken = default)
        {
            return await QueryEnvironmentsAsync(SqlStrings.SelectAllEnvironments, cancellationToken);
        }

        public async Task<List<LLM_Environment>> GetCustomEnvironmentsAsync(CancellationToken cancellationToken = default)
        {
            return await QueryEnvironmentsAsync(SqlStrings.SelectCustomEnvironments, cancellationToken);
        }

        public async Task<LLM_Environment> SaveEnvironmentAsync(LLM_Environment environment, CancellationToken cancellationToken = default)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.InsertEnvironment;

            cmd.Parameters.AddWithValue("$customName", environment.CustomName);
            cmd.Parameters.AddWithValue("$llm", (int)environment.LLM);
            cmd.Parameters.AddWithValue("$path", environment.Path);
            cmd.Parameters.AddWithValue("$customArgs", environment.CustomArgs);
            cmd.Parameters.AddWithValue("$customPrompt", environment.CustomPrompt);
            cmd.Parameters.AddWithValue("$createdUTC", environment.CreatedUTC.ToString("O"));
            cmd.Parameters.AddWithValue("$lastUsedUTC", environment.LastUsedUTC.ToString("O"));

            var result = await cmd.ExecuteScalarAsync(cancellationToken);
            environment.Id = Convert.ToInt32(result);
            return environment;
        }

        public async Task UpdateEnvironmentAsync(LLM_Environment environment, CancellationToken cancellationToken = default)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.UpdateEnvironment;

            cmd.Parameters.AddWithValue("$id", environment.Id);
            cmd.Parameters.AddWithValue("$customName", environment.CustomName);
            cmd.Parameters.AddWithValue("$llm", (int)environment.LLM);
            cmd.Parameters.AddWithValue("$path", environment.Path);
            cmd.Parameters.AddWithValue("$customArgs", environment.CustomArgs);
            cmd.Parameters.AddWithValue("$customPrompt", environment.CustomPrompt);
            cmd.Parameters.AddWithValue("$lastUsedUTC", environment.LastUsedUTC.ToString("O"));

            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        public async Task DeleteEnvironmentAsync(int id, CancellationToken cancellationToken = default)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.DeleteEnvironment;
            cmd.Parameters.AddWithValue("$id", id);

            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        #endregion

        #region Sandbox CRUD (Project-Scoped)

        public async Task<Sandbox> SaveSandboxAsync(Sandbox sandbox, CancellationToken cancellationToken = default)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.InsertSandbox;
            cmd.Parameters.AddWithValue("$name", sandbox.Name);
            cmd.Parameters.AddWithValue("$path", sandbox.Path);
            cmd.Parameters.AddWithValue("$projectPath", sandbox.ProjectPath);
            cmd.Parameters.AddWithValue("$branch", sandbox.Branch);
            cmd.Parameters.AddWithValue("$commitHash", (object?)sandbox.CommitHash ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$remoteUrl", (object?)sandbox.RemoteUrl ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$sourceBranch", (object?)sandbox.SourceBranch ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$createdUTC", sandbox.CreatedUTC.ToString("O"));

            var result = await cmd.ExecuteScalarAsync(cancellationToken);
            sandbox.Id = Convert.ToInt32(result);
            return sandbox;
        }

        public async Task<List<Sandbox>> GetSandboxesByProjectAsync(string projectPath, CancellationToken cancellationToken = default)
        {
            var sandboxes = new List<Sandbox>();

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.SelectSandboxesByProject;
            cmd.Parameters.AddWithValue("$projectPath", projectPath);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                sandboxes.Add(ReadSandbox(reader));
            }

            return sandboxes;
        }

        public async Task<Sandbox?> GetSandboxByIdAsync(int id, CancellationToken cancellationToken = default)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.SelectSandboxById;
            cmd.Parameters.AddWithValue("$id", id);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                return ReadSandbox(reader);
            }

            return null;
        }

        public async Task<Sandbox?> GetSandboxByNameAndProjectAsync(string name, string projectPath, CancellationToken cancellationToken = default)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.SelectSandboxByNameAndProject;
            cmd.Parameters.AddWithValue("$name", name);
            cmd.Parameters.AddWithValue("$projectPath", projectPath);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                return ReadSandbox(reader);
            }

            return null;
        }

        public async Task DeleteSandboxAsync(int id, CancellationToken cancellationToken = default)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.DeleteSandbox;
            cmd.Parameters.AddWithValue("$id", id);

            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        #endregion

        #region Agent Metadata

        public async Task<string?> GetAgentCustomNameAsync(string path, CancellationToken cancellationToken = default)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.SelectAgentMetadataByPath;
            cmd.Parameters.AddWithValue("$path", Path.GetFullPath(path));

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                return reader.GetString(2); // CustomName is at index 2
            }

            return null;
        }

        public async Task SetAgentCustomNameAsync(string path, string customName, CancellationToken cancellationToken = default)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.UpsertAgentMetadata;
            cmd.Parameters.AddWithValue("$path", Path.GetFullPath(path));
            cmd.Parameters.AddWithValue("$customName", customName);

            await cmd.ExecuteScalarAsync(cancellationToken);
        }

        #endregion

        #region Project Metadata

        public async Task<string?> GetProjectCustomNameAsync(string path, CancellationToken cancellationToken = default)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.SelectProjectMetadataByPath;
            cmd.Parameters.AddWithValue("$path", Path.GetFullPath(path));

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                return reader.GetString(2); // CustomName is at index 2
            }

            return null;
        }

        public async Task SetProjectCustomNameAsync(string path, string customName, CancellationToken cancellationToken = default)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.UpsertProjectMetadata;
            cmd.Parameters.AddWithValue("$path", Path.GetFullPath(path));
            cmd.Parameters.AddWithValue("$customName", customName);

            await cmd.ExecuteScalarAsync(cancellationToken);
        }

        #endregion

        #region ChatSummary CRUD

        public async Task<ChatSummary> SaveChatSummaryAsync(ChatSummary chatSummary, CancellationToken cancellationToken = default)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.UpsertChatSummary;
            cmd.Parameters.AddWithValue("$sessionId", chatSummary.SessionId);
            cmd.Parameters.AddWithValue("$summaryText", chatSummary.SummaryText);
            cmd.Parameters.AddWithValue("$date", chatSummary.Date.ToString("O"));

            var result = await cmd.ExecuteScalarAsync(cancellationToken);
            chatSummary.Id = Convert.ToInt32(result);
            return chatSummary;
        }

        public async Task<ChatSummary?> GetChatSummaryByIdAsync(int id, CancellationToken cancellationToken = default)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.SelectChatSummaryById;
            cmd.Parameters.AddWithValue("$id", id);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                return ReadChatSummary(reader);
            }

            return null;
        }

        public async Task<List<ChatSummary>> GetChatSummariesBySessionAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            var summaries = new List<ChatSummary>();

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.SelectChatSummariesBySession;
            cmd.Parameters.AddWithValue("$sessionId", sessionId);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                summaries.Add(ReadChatSummary(reader));
            }

            return summaries;
        }

        public async Task<List<ChatSummary>> GetAllChatSummariesAsync(CancellationToken cancellationToken = default)
        {
            var summaries = new List<ChatSummary>();

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.SelectAllChatSummaries;

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                summaries.Add(ReadChatSummary(reader));
            }

            return summaries;
        }

        public async Task DeleteChatSummaryAsync(int id, CancellationToken cancellationToken = default)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.DeleteChatSummary;
            cmd.Parameters.AddWithValue("$id", id);

            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        public async Task DeleteChatSummaryBySessionAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.DeleteChatSummaryBySession;
            cmd.Parameters.AddWithValue("$sessionId", sessionId);

            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        #endregion

        #region Private Helpers

        private async Task<List<LLM_Environment>> QueryEnvironmentsAsync(string sql, CancellationToken cancellationToken)
        {
            var environments = new List<LLM_Environment>();

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                environments.Add(ReadEnvironment(reader));
            }

            return environments;
        }

        private static LLM_Environment ReadEnvironment(SqliteDataReader reader)
        {
            return new LLM_Environment
            {
                Id = reader.GetInt32(0),
                CustomName = reader.GetString(1),
                LLM = (LLM)reader.GetInt32(2),
                Path = reader.GetString(3),
                CustomArgs = reader.GetString(4),
                CustomPrompt = reader.GetString(5),
                CreatedUTC = DateTime.Parse(reader.GetString(6), null, System.Globalization.DateTimeStyles.RoundtripKind),
                LastUsedUTC = DateTime.Parse(reader.GetString(7), null, System.Globalization.DateTimeStyles.RoundtripKind)
            };
        }

        private static ChatSummary ReadChatSummary(SqliteDataReader reader)
        {
            return new ChatSummary
            {
                Id = reader.GetInt32(0),
                SessionId = reader.GetString(1),
                SummaryText = reader.GetString(2),
                Date = DateTime.Parse(reader.GetString(3), null, System.Globalization.DateTimeStyles.RoundtripKind)
            };
        }

        private static Sandbox ReadSandbox(SqliteDataReader reader)
        {
            return new Sandbox
            {
                Id = reader.GetInt32(0),
                Name = reader.GetString(1),
                Path = reader.GetString(2),
                ProjectPath = reader.GetString(3),
                Branch = reader.GetString(4),
                CommitHash = reader.IsDBNull(5) ? null : reader.GetString(5),
                RemoteUrl = reader.IsDBNull(6) ? null : reader.GetString(6),
                SourceBranch = reader.IsDBNull(7) ? null : reader.GetString(7),
                CreatedUTC = DateTime.Parse(reader.GetString(8), null, System.Globalization.DateTimeStyles.RoundtripKind)
            };
        }

        #endregion

        #region Session Lifecycle

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

        public async Task<(string? Cli, string? DisplayName)> GetSessionDisplayInfoAsync(string sessionId)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.SelectSessionDisplayInfo;
            cmd.Parameters.AddWithValue("$id", sessionId);

            await using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                return (null, null);

            return (
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1)
            );
        }

        public async Task SetParentSessionIdAsync(string sessionId, string parentSessionId)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.SetParentSessionId;
            cmd.Parameters.AddWithValue("$id", sessionId);
            cmd.Parameters.AddWithValue("$parentSessionId", parentSessionId);
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task SetSessionDisplayNameAsync(string sessionId, string displayName)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.SetSessionDisplayName;
            cmd.Parameters.AddWithValue("$id", sessionId);
            cmd.Parameters.AddWithValue("$displayName", displayName);
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

        #endregion

        #region Session Retrieval

        public async Task<SessionWithLogsResponse?> GetSessionWithLogsAsync(string sessionId, CancellationToken cancellationToken)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

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

        #endregion

        #region Chat History

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

        #endregion

        #region User Input Tracking

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
                var currentCommitHash = await gitService.GetCurrentCommitHashAsync(cancellationToken);

                var lastInput = await GetLastUserInputAsync(sessionId);
                var sequence = (lastInput?.Sequence ?? 0) + 1;

                var fileChanges = new List<FileChangeInfo>();
                if (lastInput != null && !string.IsNullOrEmpty(lastInput.GitCommitHash))
                {
                    fileChanges = await gitService.GetFileChangesSinceAsync(lastInput.GitCommitHash, cancellationToken);
                }

                var userInputId = await InsertUserInputAsync(sessionId, sequence, inputText, currentCommitHash);

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

        #endregion
    }
}
