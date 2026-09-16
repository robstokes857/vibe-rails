using Microsoft.Data.Sqlite;
using VibeRails.Services;
using VibeRails.Data.Abstractions;
using VibeRails.Data.Sqlite;
using Serilog;
using VibeRails.DTOs;
using System.Text;
using System.Text.Json;

namespace VibeRails.DB
{
    public class Repository : IRepository
    {
        /// <summary>
        /// One schema pass per distinct connection string per process. IRepository is scoped, so
        /// without this every HTTP request opened an extra connection and re-ran the migration
        /// ledger plus the pragma_table_info probes behind it -- pure latency and lock-contention
        /// surface, because StateDatabaseSchema.Ensure has nothing left to do after the first
        /// successful pass.
        ///
        /// Membership is recorded only after a clean pass. A migration that throws partway must
        /// leave the database open to another attempt rather than be marked done: see
        /// StateSchemaLegacyAdoptionTests, which constructs a Repository over the same connection
        /// string a second time after correcting the rows that blocked adoption.
        /// </summary>
        private static readonly HashSet<string> _initializedDatabases = new(StringComparer.Ordinal);
        private static readonly object _initLock = new();

        private readonly string _connectionString;
        private readonly ILogger<Repository>? _logger;
        private readonly IProxyExchangeArchiveReader? _proxyArchiveReader;

        public Repository(string connectionString, ILogger<Repository>? logger = null,
            IProxyExchangeArchiveReader? proxyArchiveReader = null)
        {
            _connectionString = connectionString;
            _logger = logger;
            _proxyArchiveReader = proxyArchiveReader;
            InitializeDatabase();
        }

        public void InitializeDatabase()
        {
            lock (_initLock)
            {
                if (_initializedDatabases.Contains(_connectionString))
                    return;
                SqliteStorageErrors.Execute(() => StateDatabaseSchema.Ensure(_connectionString, _logger));
                _initializedDatabases.Add(_connectionString);
            }
        }

        private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
        {
            try { return await SqliteConnectionFactory.OpenAsync(_connectionString, cancellationToken); }
            catch (SqliteException ex) { throw SqliteStorageErrors.Translate(ex); }
        }

        private Task<SqliteConnection> OpenSessionExportConnectionAsync(CancellationToken cancellationToken) =>
            SqliteConnectionFactory.OpenAsync(_connectionString, cancellationToken, readOnly: true);

        #region LLM_Environment CRUD (Global)

        public async Task<LLM_Environment?> GetEnvironmentByIdAsync(int id, CancellationToken cancellationToken = default)
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);

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
            await using var connection = await OpenConnectionAsync(cancellationToken);

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
            await using var connection = await OpenConnectionAsync(cancellationToken);

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
            await using var connection = await OpenConnectionAsync(cancellationToken);

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
            await using var connection = await OpenConnectionAsync(cancellationToken);

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
            await using var connection = await OpenConnectionAsync(cancellationToken);

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
            await using var connection = await OpenConnectionAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.TouchEnvironmentLastUsed;
            cmd.Parameters.AddWithValue("$id", environmentId);
            cmd.Parameters.AddWithValue("$lastUsedUTC", DateTime.UtcNow.ToString("O"));

            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        public async Task DeleteEnvironmentAsync(int id, CancellationToken cancellationToken = default)
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.DeleteEnvironment;
            cmd.Parameters.AddWithValue("$id", id);

            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        #endregion

        #region Sandbox CRUD (Project-Scoped)

        public async Task<Sandbox> SaveSandboxAsync(Sandbox sandbox, CancellationToken cancellationToken = default)
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);

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

            await using var connection = await OpenConnectionAsync(cancellationToken);

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
            await using var connection = await OpenConnectionAsync(cancellationToken);

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
            await using var connection = await OpenConnectionAsync(cancellationToken);

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

            await using var connection = await OpenConnectionAsync(cancellationToken);

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
            await using var connection = await OpenConnectionAsync(cancellationToken);

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

            await using var connection = await OpenConnectionAsync(cancellationToken);

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
            await using var connection = await OpenConnectionAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.DeleteSandbox;
            cmd.Parameters.AddWithValue("$id", id);

            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        #endregion

        #region EnvironmentStep CRUD

        public async Task<List<EnvironmentStep>> GetStepsForEnvironmentAsync(
            int environmentId,
            CancellationToken cancellationToken = default)
        {
            var steps = new List<EnvironmentStep>();

            await using var connection = await OpenConnectionAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.SelectStepsByEnvironmentId;
            cmd.Parameters.AddWithValue("$environmentId", environmentId);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                steps.Add(ReadEnvironmentStep(reader));
            }

            return steps;
        }

        /// <summary>
        /// One query for every listed environment's steps, indexed by owner — the list endpoint
        /// renders a step count per row and must not pay an N+1 for it.
        /// </summary>
        public async Task<Dictionary<int, List<EnvironmentStep>>> GetStepsForEnvironmentsAsync(
            IReadOnlyList<int> environmentIds,
            CancellationToken cancellationToken = default)
        {
            var result = new Dictionary<int, List<EnvironmentStep>>();
            if (environmentIds.Count == 0)
            {
                return result;
            }

            await using var connection = await OpenConnectionAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();

            // Parameterized IN list rather than an interpolated one. These ids come from rows we
            // just read, but the habit is what keeps the next caller safe.
            var placeholders = new string[environmentIds.Count];
            for (var i = 0; i < environmentIds.Count; i++)
            {
                placeholders[i] = $"$id{i}";
                cmd.Parameters.AddWithValue($"$id{i}", environmentIds[i]);
            }

            cmd.CommandText = string.Format(
                SqlStrings.SelectStepsByEnvironmentIdsTemplate,
                string.Join(", ", placeholders));

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var step = ReadEnvironmentStep(reader);
                if (!result.TryGetValue(step.EnvironmentId, out var list))
                {
                    list = [];
                    result[step.EnvironmentId] = list;
                }

                list.Add(step);
            }

            return result;
        }

        public async Task<List<EnvironmentStep>> GetEnabledStepsAsync(
            int environmentId,
            EnvironmentStepPhase phase,
            CancellationToken cancellationToken = default)
        {
            var steps = new List<EnvironmentStep>();

            await using var connection = await OpenConnectionAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.SelectEnabledStepsByEnvironmentIdAndPhase;
            cmd.Parameters.AddWithValue("$environmentId", environmentId);
            cmd.Parameters.AddWithValue("$phase", (int)phase);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                steps.Add(ReadEnvironmentStep(reader));
            }

            return steps;
        }

        /// <summary>
        /// Resolves one step by its GUID, scoped to the environment being launched — a
        /// {{step:&lt;id&gt;}} prompt reference must not be able to reach another environment's
        /// commands. Null means the step was deleted (or never belonged to this environment).
        /// </summary>
        public async Task<EnvironmentStep?> GetStepByIdAsync(
            int environmentId,
            string stepId,
            CancellationToken cancellationToken = default)
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.SelectStepByIdAndEnvironmentId;
            cmd.Parameters.AddWithValue("$id", stepId);
            cmd.Parameters.AddWithValue("$environmentId", environmentId);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? ReadEnvironmentStep(reader) : null;
        }

        /// <summary>
        /// Cheap "is this phase worth wiring up at all" probe. The launch path uses it to decide
        /// whether to remember a session's post-step context, so it runs on every session created.
        /// </summary>
        public async Task<bool> HasEnabledStepsAsync(
            int environmentId,
            EnvironmentStepPhase phase,
            CancellationToken cancellationToken = default)
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.CountStepsByEnvironmentIdAndPhase;
            cmd.Parameters.AddWithValue("$environmentId", environmentId);
            cmd.Parameters.AddWithValue("$phase", (int)phase);

            var count = await cmd.ExecuteScalarAsync(cancellationToken);
            return Convert.ToInt64(count) > 0;
        }

        /// <summary>
        /// Replaces an environment's entire step list in one transaction, stamping Position from
        /// array order. Same shape as JobStore.ReplaceTriggersAsync: BEGIN IMMEDIATE, delete, then
        /// re-insert, so a concurrent editor cannot interleave with a partially applied list.
        /// </summary>
        public async Task ReplaceStepsAsync(
            int environmentId,
            IReadOnlyList<EnvironmentStep> steps,
            CancellationToken cancellationToken = default)
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);

            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
                System.Data.IsolationLevel.Serializable,
                cancellationToken);

            await using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = SqlStrings.DeleteStepsByEnvironmentId;
                delete.Parameters.AddWithValue("$environmentId", environmentId);
                await delete.ExecuteNonQueryAsync(cancellationToken);
            }

            // The delete above cleared this environment's rows, so any id still in the table
            // belongs to a different environment — and Id is the table-wide primary key, so
            // reusing one would fail the insert and 500 the whole save. Read inside the
            // transaction: at Serializable nobody else can add rows between here and the commit.
            var takenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using (var existing = connection.CreateCommand())
            {
                existing.Transaction = transaction;
                existing.CommandText = SqlStrings.SelectAllStepIds;
                await using var reader = await existing.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    takenIds.Add(reader.GetString(0));
                }
            }

            // Position is per (EnvironmentId, Phase), so each phase counts from zero independently.
            var positions = new Dictionary<EnvironmentStepPhase, int>();
            var now = DateTime.UtcNow.ToString("O");

            foreach (var step in steps)
            {
                positions.TryGetValue(step.Phase, out var position);
                positions[step.Phase] = position + 1;

                await using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = SqlStrings.InsertEnvironmentStep;
                // Ids are client-generated GUIDs preserved across replaces; {{step:<id>}} prompt
                // references depend on that stability. A blank one (older client, hand-built
                // request) gets a fresh GUID here rather than failing the whole save.
                var id = Guid.TryParse(step.Id, out var stepId) ? stepId.ToString() : Guid.NewGuid().ToString();
                // A collision only happens through a hand-built request or a copy-pasted export,
                // and the id is bookkeeping the client normally handles invisibly. Same call as
                // the route makes for a malformed id: regenerate rather than reject the save. The
                // cost is that any {{step:<id>}} written against the old id stops resolving, which
                // beats refusing to save at all.
                if (!takenIds.Add(id))
                {
                    var replacement = Guid.NewGuid().ToString();
                    Log.Warning(
                        "[Steps] Step id {StepId} already belongs to another environment — saving step " +
                        "{StepName} under {Replacement} instead",
                        id, step.Name ?? "", replacement);
                    id = replacement;
                    takenIds.Add(id);
                }
                insert.Parameters.AddWithValue("$id", id);
                insert.Parameters.AddWithValue("$environmentId", environmentId);
                insert.Parameters.AddWithValue("$phase", (int)step.Phase);
                insert.Parameters.AddWithValue("$position", position);
                insert.Parameters.AddWithValue("$name", step.Name ?? "");
                insert.Parameters.AddWithValue("$command", step.Command ?? "");
                insert.Parameters.AddWithValue("$startMinimized", step.StartMinimized ? 1 : 0);
                insert.Parameters.AddWithValue("$timeoutSeconds", step.TimeoutSeconds);
                insert.Parameters.AddWithValue("$enabled", step.Enabled ? 1 : 0);
                // A replace rewrites every row, so CreatedUTC is preserved when the caller carried
                // it over from the previous read and stamped fresh otherwise.
                insert.Parameters.AddWithValue(
                    "$createdUTC",
                    step.CreatedUTC == default ? now : step.CreatedUTC.ToString("O"));
                insert.Parameters.AddWithValue("$updatedUTC", now);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }

        #endregion

        #region Agent Metadata

        public async Task<string?> GetAgentCustomNameAsync(string path, CancellationToken cancellationToken = default)
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);

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
            await using var connection = await OpenConnectionAsync(cancellationToken);

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
            await using var connection = await OpenConnectionAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.SelectProjectCacheByKey;
            cmd.Parameters.AddWithValue("$projectPath", NormalizeWorkingDirectory(projectPath));
            cmd.Parameters.AddWithValue("$key", key);

            var result = await cmd.ExecuteScalarAsync(cancellationToken);
            return result == null || result == DBNull.Value ? null : Convert.ToString(result);
        }

        public async Task SetProjectCacheValueAsync(string projectPath, string key, string value, CancellationToken cancellationToken = default)
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);

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

            await using var connection = await OpenConnectionAsync(cancellationToken);

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
            await using var connection = await OpenConnectionAsync(cancellationToken);

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
            await using var connection = await OpenConnectionAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.SelectGlobalCacheByKey;
            cmd.Parameters.AddWithValue("$key", key);

            var result = await cmd.ExecuteScalarAsync(cancellationToken);
            return result == null || result == DBNull.Value ? null : Convert.ToString(result);
        }

        public async Task SetGlobalCacheValueAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);

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

            await using var connection = await OpenConnectionAsync(cancellationToken);

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
            await using var connection = await OpenConnectionAsync(cancellationToken);

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
            await using var connection = await OpenConnectionAsync(cancellationToken);
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
            await using var connection = await OpenConnectionAsync(cancellationToken);

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
            await using var connection = await OpenConnectionAsync(cancellationToken);

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

            await using var connection = await OpenConnectionAsync(cancellationToken);

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

            await using var connection = await OpenConnectionAsync(cancellationToken);

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
            await using var connection = await OpenConnectionAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.DeleteChatSummary;
            cmd.Parameters.AddWithValue("$id", id);

            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        public async Task DeleteChatSummaryBySessionAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);

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

            await using var connection = await OpenConnectionAsync(cancellationToken);

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

        // Positional, like every other mapper here: always APPEND to the SELECT lists in
        // SqlStrings, never insert into the middle.
        private static EnvironmentStep ReadEnvironmentStep(SqliteDataReader reader)
        {
            return new EnvironmentStep
            {
                Id = reader.GetString(0),
                EnvironmentId = reader.GetInt32(1),
                Phase = (EnvironmentStepPhase)reader.GetInt32(2),
                Position = reader.GetInt32(3),
                Name = reader.GetString(4),
                Command = reader.GetString(5),
                StartMinimized = reader.GetBoolean(6),
                TimeoutSeconds = reader.GetInt32(7),
                Enabled = reader.GetBoolean(8),
                CreatedUTC = DateTime.Parse(reader.GetString(9), null, System.Globalization.DateTimeStyles.RoundtripKind),
                UpdatedUTC = DateTime.Parse(reader.GetString(10), null, System.Globalization.DateTimeStyles.RoundtripKind)
            };
        }

        #endregion

        #region Session Lifecycle

        public async Task CreateSessionAsync(string sessionId, string cli, string? envName, string workDir, int ownerPid, string? jobRunId = null)
        {
            await using var connection = await OpenConnectionAsync();

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

            await using var connection = await OpenConnectionAsync(cancellationToken);

            var projectDisplayName = await GetLatestProjectDisplayNameByWorkingDirectoryAsync(connection, normalizedPath, cancellationToken);
            return !string.IsNullOrWhiteSpace(projectDisplayName)
                ? projectDisplayName
                : GetProjectDisplayNameFromPath(normalizedPath);
        }

        public async Task<bool> UpdateLatestProjectDisplayNameAsync(string path, string projectDisplayName, CancellationToken cancellationToken = default)
        {
            var normalizedPath = NormalizeWorkingDirectory(path);

            await using var connection = await OpenConnectionAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.UpdateLatestProjectDisplayNameByWorkingDirectory;
            cmd.Parameters.AddWithValue("$workingDirectory", normalizedPath);
            cmd.Parameters.AddWithValue("$projectDisplayName", projectDisplayName.Trim());

            var rowsAffected = await cmd.ExecuteNonQueryAsync(cancellationToken);
            return rowsAffected > 0;
        }

        public async Task<(string? Cli, string? DisplayName)> GetSessionDisplayInfoAsync(string sessionId)
        {
            await using var connection = await OpenConnectionAsync();

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
            await using var connection = await OpenConnectionAsync();

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.SetParentSessionId;
            cmd.Parameters.AddWithValue("$id", sessionId);
            cmd.Parameters.AddWithValue("$parentSessionId", parentSessionId);
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task SetSessionDisplayNameAsync(string sessionId, string displayName)
        {
            await using var connection = await OpenConnectionAsync();

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.SetSessionDisplayName;
            cmd.Parameters.AddWithValue("$id", sessionId);
            cmd.Parameters.AddWithValue("$displayName", displayName);
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task LogSessionOutputAsync(string sessionId, byte[] content, bool isError = false)
        {
            await using var connection = await OpenConnectionAsync();

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
            await using var connection = await OpenConnectionAsync();

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

            await using var connection = await OpenConnectionAsync(cancellationToken);

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
            await using var connection = await OpenConnectionAsync();

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
            await using var connection = await OpenConnectionAsync(cancellationToken);

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
            await using var connection = await OpenConnectionAsync(cancellationToken);

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

            await using var connection = await OpenConnectionAsync(cancellationToken);

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
            await using var connection = await OpenConnectionAsync(cancellationToken);

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

            await using var connection = await OpenConnectionAsync(cancellationToken);

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

        public async Task<UnexportedSessionRef?> GetOldestUnexportedSessionAsync(
            DateTime endedBeforeUtc,
            DateTime nowUtc,
            CancellationToken cancellationToken)
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.SelectOldestUnexportedSession;
            cmd.Parameters.AddWithValue("$endedBeforeUtc", endedBeforeUtc.ToUniversalTime().ToString("O"));
            cmd.Parameters.AddWithValue("$nowUtc", nowUtc.ToUniversalTime().ToString("O"));
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return null;

            // Carrying the attempt count out with the id lets a failure be recorded as one write
            // instead of a read-modify-write race between two root backends.
            return new UnexportedSessionRef(
                reader.GetString(0),
                reader.IsDBNull(1) ? 0 : reader.GetInt32(1));
        }

        public async Task<bool> DeferSessionExportAsync(
            string sessionId,
            DateTime nextAttemptUtc,
            CancellationToken cancellationToken)
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.DeferSessionExport;
            cmd.Parameters.AddWithValue("$sessionId", sessionId);
            cmd.Parameters.AddWithValue("$nextAttemptUtc", nextAttemptUtc.ToUniversalTime().ToString("O"));
            return await cmd.ExecuteNonQueryAsync(cancellationToken) == 1;
        }

        public async Task<SessionDataExportDescriptor?> WriteSessionExportAsync(
            string sessionId,
            Stream destination,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(destination);

            await using var proxy = _proxyArchiveReader is null
                ? new PreparedProxyArchive("unavailable")
                : await _proxyArchiveReader.PrepareAsync(sessionId, cancellationToken);

            await using var connection =
                await OpenSessionExportConnectionAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await using var transaction = connection.BeginTransaction(deferred: true);

            string id;
            string cli;
            string? environmentName;
            string workingDirectory;
            string projectDisplayName;
            string startedUtc;
            string endedUtc;
            int? exitCode;
            string? parentSessionId;
            string? sessionDisplayName;
            string? jobRunId;

            await using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = SqlStrings.SelectSessionForExport;
                cmd.Parameters.AddWithValue("$sessionId", sessionId);
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                {
                    await transaction.CommitAsync(cancellationToken);
                    return null;
                }

                id = reader.GetString(0);
                cli = reader.GetString(1);
                environmentName = reader.IsDBNull(2) ? null : reader.GetString(2);
                workingDirectory = reader.GetString(3);
                projectDisplayName = reader.GetString(4);
                startedUtc = reader.GetString(5);
                endedUtc = reader.GetString(6);
                exitCode = reader.IsDBNull(7) ? null : reader.GetInt32(7);
                parentSessionId = NullIfEmpty(reader.IsDBNull(8) ? null : reader.GetString(8));
                sessionDisplayName = NullIfEmpty(reader.IsDBNull(9) ? null : reader.GetString(9));
                jobRunId = NullIfEmpty(reader.IsDBNull(10) ? null : reader.GetString(10));
            }

            if (!Guid.TryParse(id, out var sourceId) || sourceId == Guid.Empty)
                throw new InvalidDataException($"Session '{id}' does not have a non-empty GUID id.");

            await using var writer = new Utf8JsonWriter(destination);
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 2);
            writer.WriteString("kind", "session");
            writer.WriteString("sourceId", sourceId);

            writer.WritePropertyName("session");
            writer.WriteStartObject();
            writer.WriteString("id", id);
            writer.WriteString("cli", cli);
            WriteNullableString(writer, "environmentName", environmentName);
            writer.WriteString("workingDirectory", workingDirectory);
            writer.WriteString("projectDisplayName", projectDisplayName);
            writer.WriteString("startedUtc", startedUtc);
            writer.WriteString("endedUtc", endedUtc);
            if (exitCode is null) writer.WriteNull("exitCode"); else writer.WriteNumber("exitCode", exitCode.Value);
            WriteNullableString(writer, "parentSessionId", parentSessionId);
            WriteNullableString(writer, "sessionDisplayName", sessionDisplayName);
            WriteNullableString(writer, "jobRunId", jobRunId);
            writer.WriteEndObject();

            // userInputs are written before the log BLOBs so the server can copy them into SQL
            // without scanning SessionLogs / TerminalSessionLogs. Historical envelopes still have
            // the arrays the other way around; the capture parser handles both orders.
            writer.WritePropertyName("userInputs");
            writer.WriteStartArray();
            await using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = SqlStrings.SelectUserInputsWithFileChangesForExport;
                cmd.Parameters.AddWithValue("$sessionId", sessionId);
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                long? openInputId = null;
                while (await reader.ReadAsync(cancellationToken))
                {
                    var inputId = reader.GetInt64(0);
                    if (openInputId != inputId)
                    {
                        if (openInputId is not null)
                        {
                            writer.WriteEndArray();
                            writer.WriteEndObject();
                        }

                        writer.WriteStartObject();
                        writer.WriteNumber("id", inputId);
                        writer.WriteNumber("sequence", reader.GetInt32(1));
                        writer.WriteString("inputText", reader.GetString(2));
                        WriteNullableString(writer, "gitCommitHash", reader.IsDBNull(3) ? null : reader.GetString(3));
                        writer.WriteString("timestampUtc", reader.GetString(4));
                        writer.WritePropertyName("fileChanges");
                        writer.WriteStartArray();
                        openInputId = inputId;
                    }

                    if (!reader.IsDBNull(5))
                    {
                        writer.WriteStartObject();
                        writer.WriteNumber("id", reader.GetInt64(5));
                        if (reader.IsDBNull(6)) writer.WriteNull("previousInputId"); else writer.WriteNumber("previousInputId", reader.GetInt64(6));
                        writer.WriteString("filePath", reader.GetString(7));
                        writer.WriteString("changeType", reader.GetString(8));
                        if (reader.IsDBNull(9)) writer.WriteNull("linesAdded"); else writer.WriteNumber("linesAdded", reader.GetInt32(9));
                        if (reader.IsDBNull(10)) writer.WriteNull("linesDeleted"); else writer.WriteNumber("linesDeleted", reader.GetInt32(10));
                        WriteNullableString(writer, "diffContent", reader.IsDBNull(11) ? null : reader.GetString(11));
                        writer.WriteEndObject();
                    }

                    await FlushIfPendingAsync(writer, cancellationToken);
                }

                if (openInputId is not null)
                {
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }
            }
            writer.WriteEndArray();

            writer.WritePropertyName("sessionLogs");
            writer.WriteStartArray();
            await using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = SqlStrings.SelectSessionLogsForExport;
                cmd.Parameters.AddWithValue("$sessionId", sessionId);
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("id", reader.GetInt64(0));
                    writer.WriteString("timestampUtc", reader.GetString(1));
                    writer.WriteBase64String("rawBytes", (byte[])reader[2]);
                    writer.WriteBoolean("isError", reader.GetInt32(3) != 0);
                    writer.WriteEndObject();
                    await FlushIfPendingAsync(writer, cancellationToken);
                }
            }
            writer.WriteEndArray();

            writer.WritePropertyName("terminalSessionLogs");
            writer.WriteStartArray();
            await using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = SqlStrings.SelectTerminalSessionLogsForExport;
                cmd.Parameters.AddWithValue("$sessionId", sessionId);
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("id", reader.GetInt64(0));
                    writer.WriteNumber("sequence", reader.GetInt32(1));
                    writer.WriteBoolean("isAlternateScreen", reader.GetInt32(2) != 0);
                    writer.WriteBase64String("rawBytes", (byte[])reader[3]);
                    writer.WriteNumber("cols", reader.GetInt32(4));
                    writer.WriteNumber("rows", reader.GetInt32(5));
                    writer.WriteString("timestampUtc", reader.GetString(6));
                    writer.WriteEndObject();
                    await FlushIfPendingAsync(writer, cancellationToken);
                }
            }
            writer.WriteEndArray();

            await using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = SqlStrings.SelectSessionTranscriptForExport;
                cmd.Parameters.AddWithValue("$sessionId", sessionId);
                var transcript = await cmd.ExecuteScalarAsync(cancellationToken) as string;
                WriteNullableString(writer, "transcript", transcript);
            }

            writer.WritePropertyName("summary");
            await using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = SqlStrings.SelectSessionSummaryForExport;
                cmd.Parameters.AddWithValue("$sessionId", sessionId);
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    writer.WriteStartObject();
                    writer.WriteString("summaryText", reader.GetString(0));
                    writer.WriteString("dateUtc", reader.GetString(1));
                    writer.WriteEndObject();
                }
                else
                {
                    writer.WriteNullValue();
                }
            }

            writer.WritePropertyName("proxyCoverage");
            writer.WriteStartObject();
            writer.WriteString("status", proxy.Status);
            if (proxy.Count is long count) writer.WriteNumber("count", count);
            if (proxy.SnapshotUtc is DateTime snapshotUtc) writer.WriteString("snapshotUtc", snapshotUtc);
            writer.WriteEndObject();
            if (proxy.Content is null)
            {
                writer.WriteEndObject();
                await writer.FlushAsync(cancellationToken);
            }
            else
            {
                // The optional array is already fully serialized in a private staging file.
                // Flush ordinary fields, append the complete array and final brace directly,
                // and make no further writer calls; this keeps large bodies out of memory.
                await writer.FlushAsync(cancellationToken);
                await destination.WriteAsync(",\"proxyExchanges\":"u8.ToArray(), cancellationToken);
                await proxy.Content.CopyToAsync(destination, 64 * 1024, cancellationToken);
                await destination.WriteAsync("}"u8.ToArray(), cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return new SessionDataExportDescriptor(2, "session", sourceId, proxy.Status);
        }

        public async Task<bool> SessionAwaitsExportAsync(
            string sessionId,
            CancellationToken cancellationToken)
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.SelectSessionAwaitsExport;
            cmd.Parameters.AddWithValue("$sessionId", sessionId);
            return await cmd.ExecuteScalarAsync(cancellationToken) is not null;
        }

        public async Task<bool> MarkSessionExportedAsync(
            string sessionId,
            DateTime exportedUtc,
            string? proxyCoverage,
            CancellationToken cancellationToken)
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.MarkSessionExported;
            cmd.Parameters.AddWithValue("$sessionId", sessionId);
            cmd.Parameters.AddWithValue("$exportedUtc", exportedUtc.ToUniversalTime().ToString("O"));
            // Null is the safe value: retention requires positive proof and prunes nothing without it.
            cmd.Parameters.AddWithValue("$proxyCoverage", (object?)proxyCoverage ?? DBNull.Value);
            return await cmd.ExecuteNonQueryAsync(cancellationToken) == 1;
        }

        // Utf8JsonWriter over a raw Stream does NOT auto-flush — it accumulates into an internal
        // ArrayBufferWriter until Flush is called. Flushing only at the end would materialise the
        // entire uncompressed envelope (including base64 of every log BLOB, a 4/3 expansion) as one
        // contiguous array with a hard Int32 ceiling, before the compressor sees a single byte.
        // Flushing on a byte threshold is what JsonSerializer.SerializeAsync itself does; the JSON
        // produced is byte-identical either way.
        private const int ExportFlushThresholdBytes = 64 * 1024;

        private static Task FlushIfPendingAsync(Utf8JsonWriter writer, CancellationToken cancellationToken)
            => writer.BytesPending >= ExportFlushThresholdBytes
                ? writer.FlushAsync(cancellationToken)
                : Task.CompletedTask;

        private static void WriteNullableString(Utf8JsonWriter writer, string name, string? value)
        {
            if (value is null) writer.WriteNull(name); else writer.WriteString(name, value);
        }

        private static string? NullIfEmpty(string? value)
            => string.IsNullOrEmpty(value) ? null : value;

        public async Task<List<SessionLogChunkRecord>> GetSessionLogChunksAsync(string sessionId, CancellationToken cancellationToken)
        {
            var chunks = new List<SessionLogChunkRecord>();

            await using var connection = await OpenConnectionAsync(cancellationToken);

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

            await using var connection = await OpenConnectionAsync(cancellationToken);

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
            await using var connection = await OpenConnectionAsync(cancellationToken);
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
                // CancellationToken.None: if the failure above WAS the cancellation, passing the
                // cancelled token here makes RollbackAsync throw too, which both masks the
                // original exception and leaves the transaction open. Matches the other sites.
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
        }

        public async Task<List<OpenSessionCleanupCandidate>> GetOpenSessionCleanupCandidatesAsync(DateTime trackedCutoff, DateTime untrackedCutoff, CancellationToken cancellationToken)
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.SelectOpenSessionCleanupCandidates;
            cmd.Parameters.AddWithValue("$trackedCutoff", trackedCutoff.ToUniversalTime().ToString("O"));
            cmd.Parameters.AddWithValue("$untrackedCutoff", untrackedCutoff.ToUniversalTime().ToString("O"));

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
            await using var connection = await OpenConnectionAsync(cancellationToken);

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
            await using var connection = await OpenConnectionAsync(cancellationToken);

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
            await using var connection = await OpenConnectionAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.UpdateSessionDisplayName;
            cmd.Parameters.AddWithValue("$sessionId", sessionId);
            cmd.Parameters.AddWithValue("$sessionDisplayName", sessionDisplayName);

            var rowsAffected = await cmd.ExecuteNonQueryAsync(cancellationToken);
            return rowsAffected > 0;
        }

        public async Task<bool> DeleteChatHistorySessionAsync(string sessionId, CancellationToken cancellationToken)
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
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
                    // Compare against the statement itself, not its text. Substring-sniffing for
                    // "DELETE FROM Sessions" silently matches any future sibling statement that
                    // mentions the table (a sub-select, a differently-scoped cleanup), and the
                    // method's whole return value is "did the parent row go".
                    if (!deletedSession && sql == SqlStrings.DeleteSession_Session)
                    {
                        deletedSession = rowsAffected > 0;
                    }
                }

                await transaction.CommitAsync(cancellationToken);
                return deletedSession;
            }
            catch
            {
                // CancellationToken.None: if the failure above WAS the cancellation, passing the
                // cancelled token here makes RollbackAsync throw too, which both masks the
                // original exception and leaves the transaction open. Matches the other sites.
                await transaction.RollbackAsync(CancellationToken.None);
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
            await using var connection = await OpenConnectionAsync();

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
            await using var connection = await OpenConnectionAsync();
            long userInputId;
            // The raw-input trigger records pending search work in the same transaction. Capture
            // survives an indexing failure; maintenance can finish it after a restart.
            await using (var transaction = connection.BeginTransaction(deferred: false))
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = SqlStrings.InsertUserInput;
                command.Parameters.AddWithValue("$sessionId", sessionId);
                command.Parameters.AddWithValue("$sequence", sequence);
                command.Parameters.AddWithValue("$inputText", inputText);
                command.Parameters.AddWithValue("$gitCommitHash", (object?)gitCommitHash ?? DBNull.Value);
                command.Parameters.AddWithValue("$timestampUTC", DateTime.UtcNow.ToString("O"));
                userInputId = (long)(await command.ExecuteScalarAsync())!;
                await transaction.CommitAsync();
            }
            try
            {
                using var transaction = connection.BeginTransaction(deferred: false);
                SearchIndexWriter.Synchronize(connection, transaction, userInputId, inputText);
                transaction.Commit();
            }
            catch (SqliteException ex)
            {
                _logger?.LogWarning(ex, "Search indexing deferred for input {InputId}; raw input and repair marker are saved.", userInputId);
            }
            return userInputId;
        }

        // NOTE: new code should use ReplaceFileChangesAsync — this append-only
        // method is no longer called from the live capture path. Kept for
        // interface back-compat; cleanup pass can remove it later.
        public async Task InsertFileChangesAsync(long userInputId, long? previousInputId, List<FileChangeInfo> changes)
        {
            if (changes.Count == 0) return;

            await using var connection = await OpenConnectionAsync();

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
            await using var connection = await OpenConnectionAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.SelectSessionWorkingDirectory;
            cmd.Parameters.AddWithValue("$sessionId", sessionId);
            var result = await cmd.ExecuteScalarAsync(cancellationToken);
            return result as string;
        }

        public async Task ReplaceFileChangesAsync(long userInputId, List<FileChangeInfo> changes, CancellationToken cancellationToken = default)
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);

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
                // CancellationToken.None: if the failure above WAS the cancellation, passing the
                // cancelled token here makes RollbackAsync throw too, which both masks the
                // original exception and leaves the transaction open. Matches the other sites.
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
        }

        public async Task<UserInputRecord?> GetUserInputByIdAsync(long userInputId, CancellationToken cancellationToken = default)
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);

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
            await using var connection = await OpenConnectionAsync(cancellationToken);

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

            await using var connection = await OpenConnectionAsync(cancellationToken);

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

            await using var connection = await OpenConnectionAsync(cancellationToken);

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
            await using var connection = await OpenConnectionAsync(cancellationToken);

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
            // OpenConnectionAsync translates only the OPEN. Execution can raise SQLITE_BUSY just as
            // easily, and an untranslated SqliteException reaches callers that reasonably only
            // catch StorageException -- SessionAggregateEmbeddingBackfillJob then counts a purely
            // transient lock as a permanent failure, and after three of them the session is
            // excluded from aggregate embeddings for good.
            try
            {
                await using var connection = await OpenConnectionAsync(cancellationToken);

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
            catch (SqliteException ex)
            {
                throw SqliteStorageErrors.Translate(ex);
            }
        }

        public async Task MarkSessionsAggregateEmbeddedAsync(IReadOnlyCollection<string> sessionIds, DateTime utcNow, CancellationToken cancellationToken)
        {
            if (sessionIds.Count == 0) return;

            await using var connection = await OpenConnectionAsync(cancellationToken);

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

            await using var connection = await OpenConnectionAsync(cancellationToken);

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
            await using var connection = await OpenConnectionAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlStrings.SelectTextForInputIdOrRaw;
            cmd.Parameters.AddWithValue("$inputId", inputId);
            cmd.Parameters.AddWithValue("$maxChars", maxChars ?? int.MaxValue);

            var result = await cmd.ExecuteScalarAsync(cancellationToken);
            return result as string ?? "";
        }

        public async Task<string> GetFirstInputTextForSessionOrRawAsync(string sessionId, int? maxChars = null, CancellationToken cancellationToken = default)
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);

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
