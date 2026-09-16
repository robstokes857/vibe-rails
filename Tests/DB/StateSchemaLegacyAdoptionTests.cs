using Microsoft.Data.Sqlite;
using VibeRails.Data.Abstractions;
using VibeRails.DB;
using Xunit;

namespace Tests.DB;

public sealed class StateSchemaLegacyAdoptionTests
{
    [Fact]
    public void ConflictingLegacyEnvironmentNames_FailWithoutMarkingAdopted_ThenRetryAfterCorrection()
    {
        var path = Path.Combine(Path.GetTempPath(), $"viberails-legacy-conflict-{Guid.NewGuid():N}.db");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        try
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = SqlStrings.CreateEnvironmentsTable + ";" + """
                INSERT INTO Environments(CustomName,LLM,CreatedUTC,LastUsedUTC)
                VALUES('Work',1,'2026-01-01','2026-01-01'),('work',1,'2026-01-01','2026-01-01');
                """;
            command.ExecuteNonQuery();

            // Old versions swallowed this UNIQUE failure. Adoption must now remain retryable,
            // rather than claiming the full schema was installed or discarding either row.
            var failure = Assert.Throws<StorageException>(() => new Repository(connectionString));
            Assert.False(failure.IsTransient);
            // Strict adoption is only actionable if the error names the rows to fix and says the
            // retry is automatic. Without this the operator sees a bare UNIQUE violation and a
            // dashboard that 500s every request.
            Assert.Contains("Work", failure.Message, StringComparison.Ordinal);
            Assert.Contains("work", failure.Message, StringComparison.Ordinal);
            Assert.Contains("retries automatically", failure.Message, StringComparison.Ordinal);
            Assert.Equal(19, Assert.IsType<SqliteException>(failure.InnerException).SqliteErrorCode);
            command.CommandText = "SELECT COUNT(*) FROM Environments;";
            Assert.Equal(2L, command.ExecuteScalar());
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('SchemaMigrations','SessionLogs');";
            Assert.Equal(0L, command.ExecuteScalar());

            command.CommandText = "UPDATE Environments SET CustomName='Renamed' WHERE CustomName='work' COLLATE BINARY;";
            command.ExecuteNonQuery();
            _ = new Repository(connectionString);
            command.CommandText = "SELECT COUNT(*) FROM SchemaMigrations WHERE Component='state';";
            Assert.Equal(3L, command.ExecuteScalar());
            command.CommandText = "SELECT COUNT(*) FROM Environments;";
            Assert.Equal(2L, command.ExecuteScalar());
        }
        finally
        {
            foreach (var file in new[] { path, path + "-wal", path + "-shm" }) File.Delete(file);
        }
    }
}
