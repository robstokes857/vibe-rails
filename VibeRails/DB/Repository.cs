using Microsoft.Data.Sqlite;
using VibeRails.DTOs;
using VibeRails.Services;

namespace VibeRails.DB
{
    public class Repository : IRepository
    {
        private static bool _initialized;
        private static readonly object _initLock = new();
        private readonly string _connectionString;

        public Repository(string connectionString)
        {
            _connectionString = connectionString;
            EnsureInitialized();
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
                foreach (var sql in SqlStrings.MigrationStatements)
                {
                    try
                    {
                        using var cmd = connection.CreateCommand();
                        cmd.CommandText = sql;
                        cmd.ExecuteNonQuery();
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

        #endregion
    }
}
