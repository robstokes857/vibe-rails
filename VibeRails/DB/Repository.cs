using Microsoft.Data.Sqlite;
using Serilog;
using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.BertBaseClasses;
using System.Text;

namespace VibeRails.DB
{
    public class Repository : IRepository
    {
        private static bool _initialized;
        private static readonly object _initLock = new();
        private readonly string _connectionString;
        private readonly IGitDiffCaptureService? _gitDiffCaptureService;
        private readonly ILogger<Repository>? _logger;

        public Repository(
            string connectionString,
            IGitDiffCaptureService? gitDiffCaptureService = null,
            ILogger<Repository>? logger = null)
        {
            _connectionString = connectionString;
            _gitDiffCaptureService = gitDiffCaptureService;
            _logger = logger;
            EnsureInitialized();
        }

        public void InitializeDatabase() => EnsureInitialized();

        private static bool IsBenignMigrationError(SqliteException ex)
        {
            // SQLite error messages we expect on re-run: "duplicate column name",
            // "table X already exists", "index X already exists". Anything else
            // (e.g. "no such module: fts5", syntax errors, malformed schema) is
            // a real failure that should be logged.
            var message = ex.Message ?? string.Empty;
            return message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase)
                || message.Contains("already exists", StringComparison.OrdinalIgnoreCase);
        }

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
                    catch (SqliteException ex) when (IsBenignMigrationError(ex))
                    {
                        // Expected re-run noise: column/table/index already exists.
                    }
                    catch (SqliteException ex)
                    {
                        // Real migration failure (e.g., CREATE VIRTUAL TABLE on a build
                        // without FTS5). Don't crash startup, but surface it so a silent
                        // degradation (search falls back to LIKE on a missing FTS table)
                        // is at least visible in the log.
                        _logger?.LogWarning(ex,
                            "Migration failed and will be skipped: {Sql}",
                            migration.Length > 200 ? migration[..200] + "…" : migration);
                    }
                }

                MaybeRebuildUserInputsFts(connection);
                BackfillUserInputsFts(connection);

                _initialized = true;
            }
        }

        // Bump when the FTS pre-filter rules change in a way that means previously
        // indexed text might now be wrong. Currently 1: corresponds to the switch
        // from legacy raw-text triggers to InputEtlFilter-driven writes.
        private const int CurrentFtsSchemaVersion = 1;

        /// <summary>
        /// Pre-1 installs populated UserInputs_fts via the legacy auto-insert /
        /// auto-update triggers, which copied raw InputText into the FTS index
        /// before secret filtering existed. Migrations now drop those triggers,
        /// but the rows they already wrote are still searchable.
        ///
        /// On any state.db where PRAGMA user_version &lt; CurrentFtsSchemaVersion,
        /// we drop UserInputs_fts entirely and let BackfillUserInputsFts repopulate
        /// it through InputEtlFilter. Fresh installs hit this path once and the
        /// FTS table is empty, so the drop-and-recreate is a no-op.
        /// </summary>
        private void MaybeRebuildUserInputsFts(SqliteConnection connection)
        {
            int currentVersion;
            using (var readVersion = connection.CreateCommand())
            {
                readVersion.CommandText = "PRAGMA user_version;";
                currentVersion = Convert.ToInt32(readVersion.ExecuteScalar() ?? 0);
            }
            if (currentVersion >= CurrentFtsSchemaVersion)
                return;

            using (var ftsCheck = connection.CreateCommand())
            {
                ftsCheck.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='UserInputs_fts' LIMIT 1;";
                if (ftsCheck.ExecuteScalar() is null)
                    return; // No FTS table to rebuild; CreateUserInputsFts ran fresh.
            }

            _logger?.LogInformation(
                "Rebuilding UserInputs_fts to apply InputEtlFilter to historical rows (schema {From} -> {To}).",
                currentVersion, CurrentFtsSchemaVersion);

            using (var drop = connection.CreateCommand())
            {
                drop.CommandText = "DROP TABLE UserInputs_fts;";
                drop.ExecuteNonQuery();
            }
            using (var recreate = connection.CreateCommand())
            {
                recreate.CommandText = SqlStrings.CreateUserInputsFts;
                recreate.ExecuteNonQuery();
            }

            // user_version is bumped AFTER BackfillUserInputsFts succeeds (below)
            // so a crash mid-backfill leaves the marker low and we retry next start.
        }

        /// <summary>
        /// Code-driven backfill for the UserInputs_fts lexical index. Runs once per
        /// process during EnsureInitialized after triggers have been dropped/refreshed.
        ///
        /// Why this is in code rather than a single INSERT…SELECT migration:
        ///   - External-content FTS5 tables proxy rowids from the content table, so
        ///     "WHERE Id NOT IN (SELECT rowid FROM UserInputs_fts)" always reports
        ///     zero missing rows even when the FTS index is empty. The authoritative
        ///     "is this rowid indexed?" lookup is against UserInputs_fts_docsize.
        ///   - We need to run InputEtlFilter.Process on each row so historical rows
        ///     containing API keys / passwords are never re-indexed into FTS.
        /// </summary>
        private void BackfillUserInputsFts(SqliteConnection connection)
        {
            // UserInputs_fts_docsize is created automatically with the virtual table,
            // but be defensive about brand-new schemas where the FTS table itself
            // doesn't exist yet (older test fixtures, partial migrations).
            using (var tableCheck = connection.CreateCommand())
            {
                tableCheck.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='UserInputs_fts_docsize' LIMIT 1;";
                if (tableCheck.ExecuteScalar() is null)
                    return;
            }

            var pending = new List<(long Id, string InputText)>();
            using (var readCmd = connection.CreateCommand())
            {
                readCmd.CommandText = SqlStrings.SelectUserInputsMissingFromFts;
                using var reader = readCmd.ExecuteReader();
                while (reader.Read())
                {
                    pending.Add((reader.GetInt64(0), reader.GetString(1)));
                }
            }
            if (pending.Count == 0) return;

            // Batch the writes so a 50k-row backfill doesn't sit in one transaction
            // and block startup behind a multi-second commit on cold disk.
            const int BatchSize = 500;
            int written = 0;
            for (int start = 0; start < pending.Count; start += BatchSize)
            {
                using var transaction = connection.BeginTransaction();
                int end = Math.Min(start + BatchSize, pending.Count);
                for (int i = start; i < end; i++)
                {
                    var (id, inputText) = pending[i];
                    var safe = Services.UserInOut.InputEtlFilter.Process(inputText);
                    if (string.IsNullOrWhiteSpace(safe))
                        continue;

                    using var insert = connection.CreateCommand();
                    insert.Transaction = transaction;
                    insert.CommandText = SqlStrings.InsertUserInputsFtsRow;
                    insert.Parameters.AddWithValue("$rowid", id);
                    insert.Parameters.AddWithValue("$inputText", safe);
                    insert.ExecuteNonQuery();
                    written++;
                }
                transaction.Commit();
            }

            if (written > 0)
            {
                _logger?.LogInformation(
                    "Backfilled {Count} rows into UserInputs_fts (of {Pending} pending).",
                    written, pending.Count);
            }

            // Mark FTS schema as current only after a successful backfill pass so
            // a crash mid-backfill leaves the marker low and we re-attempt next
            // startup. PRAGMA user_version is idempotent and free.
            using (var bump = connection.CreateCommand())
            {
                bump.CommandText = $"PRAGMA user_version = {CurrentFtsSchemaVersion};";
                bump.ExecuteNonQuery();
            }
        }

        #region LLM_Environment CRUD (Global)

        public async Task<LLM_Environment?> GetEnvironmentByIdAsync(int id, CancellationToken cancellationToken = default)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.SelectEnvironmentById;
            cmd.Parameters.AddWithValue("$id", id);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                return ReadEnvironment(reader);
            }

            return null;
        }

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

        public async Task<LLM_Environment?> FindEnvironmentByNameIgnoreCaseAsync(string name, CancellationToken cancellationToken = default)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.SelectEnvironmentByNameNoCase;
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
            cmd.Parameters.AddWithValue("$hidden", environment.Hidden ? 1 : 0);
            cmd.Parameters.AddWithValue("$automationWorker", environment.AutomationWorker ? 1 : 0);
            cmd.Parameters.AddWithValue("$workspaceMode", (int)environment.WorkspaceMode);
            cmd.Parameters.AddWithValue("$projectPath", (object?)environment.ProjectPath ?? DBNull.Value);

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
            cmd.Parameters.AddWithValue("$hidden", environment.Hidden ? 1 : 0);
            cmd.Parameters.AddWithValue("$automationWorker", environment.AutomationWorker ? 1 : 0);
            cmd.Parameters.AddWithValue("$workspaceMode", (int)environment.WorkspaceMode);
            cmd.Parameters.AddWithValue("$projectPath", (object?)environment.ProjectPath ?? DBNull.Value);

            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        public async Task TouchEnvironmentLastUsedAsync(int environmentId, CancellationToken cancellationToken = default)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.TouchEnvironmentLastUsed;
            cmd.Parameters.AddWithValue("$id", environmentId);
            cmd.Parameters.AddWithValue("$lastUsedUTC", DateTime.UtcNow.ToString("O"));

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
            cmd.Parameters.AddWithValue("$environmentId", (object?)sandbox.EnvironmentId ?? DBNull.Value);

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

        public async Task<List<Sandbox>> GetSandboxesByEnvironmentIdAsync(int environmentId, CancellationToken cancellationToken = default)
        {
            var sandboxes = new List<Sandbox>();

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.SelectSandboxesByEnvironmentId;
            cmd.Parameters.AddWithValue("$environmentId", environmentId);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                sandboxes.Add(ReadSandbox(reader));
            }

            return sandboxes;
        }

        public async Task OrphanSandboxesForEnvironmentAsync(int environmentId, CancellationToken cancellationToken = default)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.OrphanSandboxesByEnvironmentId;
            cmd.Parameters.AddWithValue("$environmentId", environmentId);

            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        public async Task<bool> HasOpenSessionUnderDirectoryAsync(string directory, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(directory))
                return false;

            var normalized = Path.TrimEndingDirectorySeparator(directory);

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.CountOpenSessionsUnderDirectory;
            cmd.Parameters.AddWithValue("$directory", normalized);
            // The prefix carries a trailing separator so it matches only paths *inside* this
            // directory. Without it, "…/nightly" would also match the sibling "…/nightly-2".
            //
            // Escape LIKE metacharacters afterwards: a real path may legitimately contain '%'
            // or '_', and an unescaped '_' matches any single character. Backslash goes first
            // since it is the ESCAPE character itself.
            var prefix = normalized + Path.DirectorySeparatorChar;
            var escaped = prefix
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("%", "\\%", StringComparison.Ordinal)
                .Replace("_", "\\_", StringComparison.Ordinal);
            cmd.Parameters.AddWithValue("$directoryPrefix", $"{escaped}%");

            var result = await cmd.ExecuteScalarAsync(cancellationToken);
            return Convert.ToInt64(result) > 0;
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

        #region ProjectCache

        public async Task<string?> GetProjectCacheValueAsync(string projectPath, string key, CancellationToken cancellationToken = default)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.SelectProjectCacheByKey;
            cmd.Parameters.AddWithValue("$projectPath", NormalizeWorkingDirectory(projectPath));
            cmd.Parameters.AddWithValue("$key", key);

            var result = await cmd.ExecuteScalarAsync(cancellationToken);
            return result == null || result == DBNull.Value ? null : Convert.ToString(result);
        }

        public async Task SetProjectCacheValueAsync(string projectPath, string key, string value, CancellationToken cancellationToken = default)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.UpsertProjectCache;
            cmd.Parameters.AddWithValue("$projectPath", NormalizeWorkingDirectory(projectPath));
            cmd.Parameters.AddWithValue("$key", key);
            cmd.Parameters.AddWithValue("$value", value);
            cmd.Parameters.AddWithValue("$updatedUTC", DateTime.UtcNow.ToString("O"));

            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        public async Task<Dictionary<string, string>> GetAllProjectCacheAsync(string projectPath, CancellationToken cancellationToken = default)
        {
            var result = new Dictionary<string, string>();

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.SelectAllProjectCache;
            cmd.Parameters.AddWithValue("$projectPath", NormalizeWorkingDirectory(projectPath));

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                result[reader.GetString(0)] = reader.GetString(1);
            }

            return result;
        }

        public async Task RemoveProjectCacheValueAsync(string projectPath, string key, CancellationToken cancellationToken = default)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.DeleteProjectCacheByKey;
            cmd.Parameters.AddWithValue("$projectPath", NormalizeWorkingDirectory(projectPath));
            cmd.Parameters.AddWithValue("$key", key);

            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        #endregion

        #region GlobalCache

        public async Task<string?> GetGlobalCacheValueAsync(string key, CancellationToken cancellationToken = default)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.SelectGlobalCacheByKey;
            cmd.Parameters.AddWithValue("$key", key);

            var result = await cmd.ExecuteScalarAsync(cancellationToken);
            return result == null || result == DBNull.Value ? null : Convert.ToString(result);
        }

        public async Task SetGlobalCacheValueAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.UpsertGlobalCache;
            cmd.Parameters.AddWithValue("$key", key);
            cmd.Parameters.AddWithValue("$value", value);
            cmd.Parameters.AddWithValue("$updatedUTC", DateTime.UtcNow.ToString("O"));

            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        public async Task<Dictionary<string, string>> GetAllGlobalCacheAsync(CancellationToken cancellationToken = default)
        {
            var result = new Dictionary<string, string>();

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.SelectAllGlobalCache;

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                result[reader.GetString(0)] = reader.GetString(1);
            }

            return result;
        }

        public async Task RemoveGlobalCacheValueAsync(string key, CancellationToken cancellationToken = default)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.DeleteGlobalCacheByKey;
            cmd.Parameters.AddWithValue("$key", key);

            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        public async Task SaveLlmPickerStateAsync(
            string cacheKey,
            string? preferenceJson,
            IReadOnlyDictionary<int, bool> environmentHidden,
            CancellationToken cancellationToken = default)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction =
                (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

            try
            {
                await using (var preferenceCommand = connection.CreateCommand())
                {
                    preferenceCommand.Transaction = transaction;
                    preferenceCommand.CommandText = preferenceJson == null
                        ? SqlStrings.DeleteGlobalCacheByKey
                        : SqlStrings.UpsertGlobalCache;
                    preferenceCommand.Parameters.AddWithValue("$key", cacheKey);
                    if (preferenceJson != null)
                    {
                        preferenceCommand.Parameters.AddWithValue("$value", preferenceJson);
                        preferenceCommand.Parameters.AddWithValue(
                            "$updatedUTC",
                            DateTime.UtcNow.ToString("O"));
                    }

                    await preferenceCommand.ExecuteNonQueryAsync(cancellationToken);
                }

                await using (var environmentCommand = connection.CreateCommand())
                {
                    environmentCommand.Transaction = transaction;
                    environmentCommand.CommandText = SqlStrings.UpdateEnvironmentHidden;
                    var idParameter = environmentCommand.Parameters.Add("$id", SqliteType.Integer);
                    var hiddenParameter = environmentCommand.Parameters.Add("$hidden", SqliteType.Integer);

                    foreach (var (environmentId, hidden) in environmentHidden)
                    {
                        idParameter.Value = environmentId;
                        hiddenParameter.Value = hidden ? 1 : 0;
                        await environmentCommand.ExecuteNonQueryAsync(cancellationToken);
                    }
                }

                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
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
                LastUsedUTC = DateTime.Parse(reader.GetString(7), null, System.Globalization.DateTimeStyles.RoundtripKind),
                Hidden = reader.GetBoolean(8),
                AutomationWorker = reader.GetBoolean(9),
                WorkspaceMode = (EnvironmentWorkspaceMode)reader.GetInt32(10),
                ProjectPath = reader.IsDBNull(11) ? null : reader.GetString(11)
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
                CreatedUTC = DateTime.Parse(reader.GetString(8), null, System.Globalization.DateTimeStyles.RoundtripKind),
                EnvironmentId = reader.IsDBNull(9) ? null : reader.GetInt32(9)
            };
        }

        #endregion

        #region Session Lifecycle

        public async Task CreateSessionAsync(string sessionId, string cli, string? envName, string workDir, int ownerPid, string? jobRunId = null)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            var normalizedWorkDir = NormalizeWorkingDirectory(workDir);
            var projectDisplayName = await GetLatestProjectDisplayNameByWorkingDirectoryAsync(connection, normalizedWorkDir)
                ?? GetProjectDisplayNameFromPath(normalizedWorkDir);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.InsertSession;

            cmd.Parameters.AddWithValue("$id", sessionId);
            cmd.Parameters.AddWithValue("$cli", cli);
            cmd.Parameters.AddWithValue("$envName", envName ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$workDir", normalizedWorkDir);
            cmd.Parameters.AddWithValue("$projectDisplayName", projectDisplayName);
            cmd.Parameters.AddWithValue("$startedUTC", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$ownerPid", ownerPid);
            // A non-null JobRunId marks this as an automated Job session: hidden from Chat History
            // (SelectChatHistoryBase filters JobRunId IS NULL) and linked back to its JobRun.
            cmd.Parameters.AddWithValue("$jobRunId", jobRunId ?? (object)DBNull.Value);

            await cmd.ExecuteNonQueryAsync();
        }

        public async Task<string> GetProjectDisplayNameAsync(string path, CancellationToken cancellationToken = default)
        {
            var normalizedPath = NormalizeWorkingDirectory(path);

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            var projectDisplayName = await GetLatestProjectDisplayNameByWorkingDirectoryAsync(connection, normalizedPath, cancellationToken);
            return !string.IsNullOrWhiteSpace(projectDisplayName)
                ? projectDisplayName
                : GetProjectDisplayNameFromPath(normalizedPath);
        }

        public async Task<bool> UpdateLatestProjectDisplayNameAsync(string path, string projectDisplayName, CancellationToken cancellationToken = default)
        {
            var normalizedPath = NormalizeWorkingDirectory(path);

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.UpdateLatestProjectDisplayNameByWorkingDirectory;
            cmd.Parameters.AddWithValue("$workingDirectory", normalizedPath);
            cmd.Parameters.AddWithValue("$projectDisplayName", projectDisplayName.Trim());

            var rowsAffected = await cmd.ExecuteNonQueryAsync(cancellationToken);
            return rowsAffected > 0;
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

        public async Task InsertTerminalSessionLogAsync(string sessionId, int sequence, byte[] data, bool isAlternateScreen, int cols, int rows)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.InsertTerminalSessionLog;

            cmd.Parameters.AddWithValue("$sessionId", sessionId);
            cmd.Parameters.AddWithValue("$sequence", sequence);
            cmd.Parameters.AddWithValue("$isAlternateScreen", isAlternateScreen ? 1 : 0);
            cmd.Parameters.Add(new SqliteParameter("$data", SqliteType.Blob) { Value = data });
            cmd.Parameters.AddWithValue("$cols", cols);
            cmd.Parameters.AddWithValue("$rows", rows);
            cmd.Parameters.AddWithValue("$timestamp", DateTime.UtcNow.ToString("O"));

            await cmd.ExecuteNonQueryAsync();
        }

        public async Task<List<TerminalSessionLogRecord>> GetTerminalSessionLogsAsync(string sessionId, CancellationToken cancellationToken)
        {
            var logs = new List<TerminalSessionLogRecord>();

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.SelectTerminalSessionLogsBySession;
            cmd.Parameters.AddWithValue("$sessionId", sessionId);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                logs.Add(new TerminalSessionLogRecord(
                    Id: reader.GetInt64(0),
                    SessionId: reader.GetString(1),
                    Sequence: reader.GetInt32(2),
                    IsAlternateScreen: reader.GetInt32(3) != 0,
                    Data: (byte[])reader[4],
                    Cols: reader.GetInt32(5),
                    Rows: reader.GetInt32(6),
                    TimestampUtc: DateTime.Parse(reader.GetString(7))));
            }

            return logs;
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

        public async Task<SessionResponse?> GetSessionByIdAsync(string sessionId, CancellationToken cancellationToken)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var sessionCmd = connection.CreateCommand();
            sessionCmd.CommandText = SqlStrings.SelectSessionById;
            sessionCmd.Parameters.AddWithValue("$id", sessionId);

            await using var reader = await sessionCmd.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return null;

            return new SessionResponse(
                Id: reader.GetString(0),
                Cli: reader.GetString(1),
                EnvironmentName: reader.IsDBNull(2) ? null : reader.GetString(2),
                WorkingDirectory: reader.GetString(3),
                StartedUTC: DateTime.Parse(reader.GetString(4), null, System.Globalization.DateTimeStyles.RoundtripKind),
                EndedUTC: reader.IsDBNull(5) ? null : DateTime.Parse(reader.GetString(5), null, System.Globalization.DateTimeStyles.RoundtripKind),
                ExitCode: reader.IsDBNull(6) ? null : reader.GetInt32(6)
            );
        }

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

        public async Task<List<OpenSessionCleanupCandidate>> GetOpenSessionCleanupCandidatesAsync(DateTime trackedCutoff, DateTime untrackedCutoff, CancellationToken cancellationToken)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.SelectOpenSessionCleanupCandidates;
            cmd.Parameters.AddWithValue("$trackedCutoff", trackedCutoff.ToString("O"));
            cmd.Parameters.AddWithValue("$untrackedCutoff", untrackedCutoff.ToString("O"));

            var sessions = new List<OpenSessionCleanupCandidate>();
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                sessions.Add(new OpenSessionCleanupCandidate(
                    SessionId: reader.GetString(0),
                    OwnerPid: reader.IsDBNull(1) ? null : reader.GetInt32(1)));
            }

            return sessions;
        }

        #endregion

        #region Chat History

        public async Task<ChatHistoryItem?> GetChatHistoryItemAsync(string sessionId, CancellationToken cancellationToken)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.SelectChatHistoryBySessionId;
            cmd.Parameters.AddWithValue("$sessionId", sessionId);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            return ReadChatHistoryItem(reader);
        }

        public async Task<List<ChatHistoryItem>> GetChatHistoryPageAsync(int limit, int offset, string? preferredWorkingDirectory, string? sortBy, string? sortDirection, CancellationToken cancellationToken)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            var items = new List<ChatHistoryItem>();
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = BuildChatHistoryPageQuery(preferredWorkingDirectory, sortBy, sortDirection);
            cmd.Parameters.AddWithValue("$limit", limit);
            cmd.Parameters.AddWithValue("$offset", offset);
            if (!string.IsNullOrWhiteSpace(preferredWorkingDirectory))
            {
                cmd.Parameters.AddWithValue("$preferredWorkingDirectory", NormalizeWorkingDirectory(preferredWorkingDirectory));
            }

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(ReadChatHistoryItem(reader));
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

        private static ChatHistoryItem ReadChatHistoryItem(SqliteDataReader reader)
        {
            var projectDisplayName = reader.IsDBNull(4) ? null : reader.GetString(4);
            var parentSessionId = reader.IsDBNull(8) ? null : reader.GetString(8);
            if (string.IsNullOrWhiteSpace(parentSessionId))
            {
                parentSessionId = null;
            }

            return new ChatHistoryItem(
                Id: reader.GetString(0),
                Cli: reader.GetString(1),
                EnvironmentName: reader.IsDBNull(2) ? null : reader.GetString(2),
                WorkingDirectory: reader.GetString(3),
                ProjectDisplayName: string.IsNullOrWhiteSpace(projectDisplayName) ? null : projectDisplayName,
                StartedUTC: DateTime.Parse(reader.GetString(5), null, System.Globalization.DateTimeStyles.RoundtripKind),
                EndedUTC: reader.IsDBNull(6) ? null : DateTime.Parse(reader.GetString(6), null, System.Globalization.DateTimeStyles.RoundtripKind),
                ExitCode: reader.IsDBNull(7) ? null : reader.GetInt32(7),
                ParentSessionId: parentSessionId,
                ParentCli: reader.IsDBNull(9) ? null : reader.GetString(9),
                SessionDisplayName: reader.IsDBNull(10) ? null : reader.GetString(10),
                Sequence: reader.IsDBNull(11) ? null : reader.GetInt32(11),
                InputText: reader.IsDBNull(12) ? null : reader.GetString(12),
                UserInputCount: reader.GetInt32(13),
                DurationSeconds: reader.IsDBNull(14) ? null : reader.GetInt64(14)
            );
        }

        private static string BuildChatHistoryPageQuery(string? preferredWorkingDirectory, string? sortBy, string? sortDirection)
        {
            var sql = new StringBuilder(SqlStrings.SelectChatHistoryBase);
            sql.AppendLine();
            sql.Append("ORDER BY ");
            sql.Append(BuildChatHistoryOrderClause(preferredWorkingDirectory, sortBy, sortDirection));
            sql.AppendLine();
            sql.Append("LIMIT $limit OFFSET $offset;");
            return sql.ToString();
        }

        private static string BuildChatHistoryOrderClause(string? preferredWorkingDirectory, string? sortBy, string? sortDirection)
        {
            var clauses = new List<string>();
            if (!string.IsNullOrWhiteSpace(preferredWorkingDirectory))
            {
                clauses.Add("CASE WHEN s.WorkingDirectory = $preferredWorkingDirectory THEN 0 ELSE 1 END ASC");
            }

            var normalizedSortBy = (sortBy ?? "recent").Trim().ToLowerInvariant();
            var normalizedDirection = string.Equals(sortDirection, "asc", StringComparison.OrdinalIgnoreCase) ? "ASC" : "DESC";
            var ascending = normalizedDirection == "ASC";

            switch (normalizedSortBy)
            {
                case "llm":
                case "cli":
                    clauses.Add($"LOWER(COALESCE(s.Cli, '')) {normalizedDirection}");
                    break;
                case "env":
                case "environment":
                    clauses.Add($"LOWER(COALESCE(s.EnvironmentName, '')) {normalizedDirection}");
                    break;
                case "project":
                    clauses.Add($"LOWER(COALESCE(s.ProjectDisplayName, '')) {normalizedDirection}");
                    break;
                case "name":
                    clauses.Add($"LOWER(COALESCE(NULLIF(s.SessionDisplayName, ''), SUBSTR(u.InputText, 1, 120), '')) {normalizedDirection}");
                    break;
                case "recent":
                default:
                    clauses.Add($"s.StartedUTC {normalizedDirection}");
                    break;
            }

            clauses.Add(ascending && normalizedSortBy == "recent"
                ? "s.Id DESC"
                : "s.StartedUTC DESC");
            clauses.Add("s.Id DESC");

            return string.Join(", ", clauses.Distinct(StringComparer.Ordinal));
        }

        private static async Task<string?> GetLatestProjectDisplayNameByWorkingDirectoryAsync(SqliteConnection connection, string workingDirectory, CancellationToken cancellationToken = default)
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.SelectLatestProjectDisplayNameByWorkingDirectory;
            cmd.Parameters.AddWithValue("$workingDirectory", workingDirectory);

            var scalar = await cmd.ExecuteScalarAsync(cancellationToken);
            var projectDisplayName = scalar == null || scalar == DBNull.Value
                ? null
                : Convert.ToString(scalar);

            return string.IsNullOrWhiteSpace(projectDisplayName) ? null : projectDisplayName;
        }

        private static string NormalizeWorkingDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            var fullPath = Path.GetFullPath(path);
            return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static string GetProjectDisplayNameFromPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return "Unknown Project";
            }

            var fileName = Path.GetFileName(path);
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                return fileName;
            }

            var normalized = path.Replace('\\', '/').TrimEnd('/');
            var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
            return parts.LastOrDefault() ?? "Unknown Project";
        }

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

            // The canonical INSERT runs as its own implicit transaction — never coupled
            // to the best-effort FTS write below. A poisoned FTS statement used to
            // unwind a shared explicit transaction and silently lose the user input;
            // BackfillUserInputsFts is the authoritative reconciliation path, so a
            // failed FTS write here is purely an optimization miss.
            long userInputId;
            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = SqlStrings.InsertUserInput;
                cmd.Parameters.AddWithValue("$sessionId", sessionId);
                cmd.Parameters.AddWithValue("$sequence", sequence);
                cmd.Parameters.AddWithValue("$inputText", inputText);
                cmd.Parameters.AddWithValue("$gitCommitHash", gitCommitHash ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("$timestampUTC", DateTime.UtcNow.ToString("O"));

                var result = await cmd.ExecuteScalarAsync();
                userInputId = (long)result!;
            }

            // FTS index write — only if InputEtlFilter clears the text. Anything that
            // looks like a secret/credential is excluded from the lexical index so it
            // never becomes searchable. UserInputs itself still holds the canonical
            // raw row (transcript replay needs it). If the FTS table is missing or
            // this insert blows up for any reason, BackfillUserInputsFts reconciles
            // on next startup.
            var safe = Services.UserInOut.InputEtlFilter.Process(inputText);
            if (!string.IsNullOrWhiteSpace(safe))
            {
                try
                {
                    await using var ftsCmd = connection.CreateCommand();
                    ftsCmd.CommandText = SqlStrings.InsertUserInputsFtsRow;
                    ftsCmd.Parameters.AddWithValue("$rowid", userInputId);
                    ftsCmd.Parameters.AddWithValue("$inputText", safe);
                    await ftsCmd.ExecuteNonQueryAsync();
                }
                catch (SqliteException ex)
                {
                    _logger?.LogWarning(ex,
                        "FTS index insert for UserInput {UserInputId} failed; canonical row already committed and backfill will reconcile later.",
                        userInputId);
                }
            }

            return userInputId;
        }

        // NOTE: new code should use ReplaceFileChangesAsync — this append-only
        // method is no longer called from the live capture path. Kept for
        // interface back-compat; cleanup pass can remove it later.
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

        public async Task<string?> GetSessionWorkingDirectoryAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.SelectSessionWorkingDirectory;
            cmd.Parameters.AddWithValue("$sessionId", sessionId);
            var result = await cmd.ExecuteScalarAsync(cancellationToken);
            return result as string;
        }

        public async Task ReplaceFileChangesAsync(long userInputId, List<FileChangeInfo> changes, CancellationToken cancellationToken = default)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                await using (var del = connection.CreateCommand())
                {
                    del.Transaction = (SqliteTransaction)transaction;
                    del.CommandText = SqlStrings.DeleteFileChangesForUserInput;
                    del.Parameters.AddWithValue("$userInputId", userInputId);
                    await del.ExecuteNonQueryAsync(cancellationToken);
                }

                foreach (var change in changes)
                {
                    await using var cmd = connection.CreateCommand();
                    cmd.Transaction = (SqliteTransaction)transaction;
                    cmd.CommandText = SqlStrings.InsertFileChange;
                    cmd.Parameters.AddWithValue("$userInputId", userInputId);
                    cmd.Parameters.AddWithValue("$previousInputId", DBNull.Value);
                    cmd.Parameters.AddWithValue("$filePath", change.FilePath);
                    cmd.Parameters.AddWithValue("$changeType", change.ChangeType);
                    cmd.Parameters.AddWithValue("$linesAdded", change.LinesAdded ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("$linesDeleted", change.LinesDeleted ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("$diffContent", change.DiffContent ?? (object)DBNull.Value);
                    await cmd.ExecuteNonQueryAsync(cancellationToken);
                }

                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
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

                var userInputId = await InsertUserInputAsync(sessionId, sequence, inputText, currentCommitHash);

                // Open a git-diff capture window for this input. The idle
                // observer re-runs the diff every time the session goes idle
                // and replaces the stored file changes, until the next user
                // input finalizes the window. The previous input's window
                // (if any) is finalized synchronously inside
                // BeginCaptureWindowAsync so late writes are captured against
                // the correct userInputId.
                if (_gitDiffCaptureService != null)
                {
                    var workingDirectory = await GetSessionWorkingDirectoryAsync(sessionId, cancellationToken);
                    if (!string.IsNullOrWhiteSpace(workingDirectory))
                    {
                        await _gitDiffCaptureService.BeginCaptureWindowAsync(
                            sessionId,
                            userInputId,
                            currentCommitHash,
                            workingDirectory,
                            cancellationToken);
                    }
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

        public async Task<UserInputRecord?> GetUserInputByIdAsync(long userInputId, CancellationToken cancellationToken = default)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.SelectUserInputById;
            cmd.Parameters.AddWithValue("$id", userInputId);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
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

        public async Task<List<UnembeddedUserInputRow>> GetUnembeddedUserInputsAsync(int batchSize, CancellationToken cancellationToken)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.SelectUnembeddedUserInputs;
            cmd.Parameters.AddWithValue("$batchSize", batchSize);

            var rows = new List<UnembeddedUserInputRow>(batchSize);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new UnembeddedUserInputRow(
                    Id: reader.GetInt64(0),
                    SessionId: reader.GetString(1),
                    InputText: reader.GetString(2)
                ));
            }
            return rows;
        }

        public async Task MarkUserInputsBertEmbeddedAsync(IReadOnlyCollection<long> userInputIds, DateTime utcNow, CancellationToken cancellationToken)
        {
            if (userInputIds.Count == 0) return;

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = SqlStrings.MarkUserInputBertEmbedded;
            var idParam = cmd.Parameters.Add("$id", SqliteType.Integer);
            cmd.Parameters.AddWithValue("$now", utcNow.ToString("O"));

            foreach (var id in userInputIds)
            {
                idParam.Value = id;
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }

        public async Task IncrementUserInputBertEmbedFailureCountsAsync(IReadOnlyCollection<long> userInputIds, CancellationToken cancellationToken)
        {
            if (userInputIds.Count == 0) return;

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = SqlStrings.IncrementUserInputBertEmbedFailureCount;
            var idParam = cmd.Parameters.Add("$id", SqliteType.Integer);

            foreach (var id in userInputIds)
            {
                idParam.Value = id;
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }

        public async Task<List<string>> GetUnaggregatedEndedSessionIdsAsync(int batchSize, CancellationToken cancellationToken)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.SelectUnaggregatedEndedSessionIds;
            cmd.Parameters.AddWithValue("$batchSize", batchSize);

            var ids = new List<string>(batchSize);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                ids.Add(reader.GetString(0));
            }
            return ids;
        }

        public async Task<List<string>> GetUserInputTextsForSessionAsync(string sessionId, CancellationToken cancellationToken)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.SelectUserInputTextsForSessionInOrder;
            cmd.Parameters.AddWithValue("$sessionId", sessionId);

            var texts = new List<string>();
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                texts.Add(reader.GetString(0));
            }
            return texts;
        }

        public async Task MarkSessionsAggregateEmbeddedAsync(IReadOnlyCollection<string> sessionIds, DateTime utcNow, CancellationToken cancellationToken)
        {
            if (sessionIds.Count == 0) return;

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = SqlStrings.MarkSessionAggregateEmbedded;
            var idParam = cmd.Parameters.Add("$id", SqliteType.Text);
            cmd.Parameters.AddWithValue("$now", utcNow.ToString("O"));

            foreach (var id in sessionIds)
            {
                idParam.Value = id;
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }

        public async Task IncrementSessionAggregateEmbedFailureCountsAsync(IReadOnlyCollection<string> sessionIds, CancellationToken cancellationToken)
        {
            if (sessionIds.Count == 0) return;

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = SqlStrings.IncrementSessionAggregateEmbedFailureCount;
            var idParam = cmd.Parameters.Add("$id", SqliteType.Text);

            foreach (var id in sessionIds)
            {
                idParam.Value = id;
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }

        public async Task<string> GetTextForInputIdOrRawAsync(long inputId, int? maxChars = null, CancellationToken cancellationToken = default)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.SelectTextForInputIdOrRaw;
            cmd.Parameters.AddWithValue("$inputId", inputId);
            cmd.Parameters.AddWithValue("$maxChars", maxChars ?? int.MaxValue);

            var result = await cmd.ExecuteScalarAsync(cancellationToken);
            return result as string ?? "";
        }

        public async Task<string> GetFirstInputTextForSessionOrRawAsync(string sessionId, int? maxChars = null, CancellationToken cancellationToken = default)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.SelectFirstInputTextForSessionOrRaw;
            cmd.Parameters.AddWithValue("$sessionId", sessionId);
            cmd.Parameters.AddWithValue("$maxChars", maxChars ?? int.MaxValue);

            var result = await cmd.ExecuteScalarAsync(cancellationToken);
            return result as string ?? "";
        }

        #endregion
    }
}
