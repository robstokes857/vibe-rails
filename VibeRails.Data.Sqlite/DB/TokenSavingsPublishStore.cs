using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using VibeRails.Data.Sqlite;

namespace VibeRails.DB;

/// <summary>One local upload checkpoint and durable pending batch, shared by all root processes.</summary>
public sealed class TokenSavingsPublishStore(string connectionString) : ITokenSavingsPublishStore
{
    /// <summary>Matches the hosted v2 per-report limit.</summary>
    public const long MaximumReportTokens = 100_000_000;

    internal static void EnsureSchema(SqliteConnection connection)
    {
        TokenSavingsStore.EnsureSchema(connection);
        SqliteMigrationRunner.Apply(connection, "token-savings-publish", 1, MigrationKind.Additive,
            (db, transaction) => SqliteSchema.Execute(db, transaction, """
                CREATE TABLE IF NOT EXISTS TokenSavingsPublishState (
                    Id INTEGER PRIMARY KEY CHECK (Id = 1),
                    ComputerId TEXT NOT NULL,
                    AcknowledgedTokens INTEGER NOT NULL,
                    PendingReportId TEXT,
                    PendingComputerName TEXT,
                    PendingTokens INTEGER,
                    PendingThroughTokens INTEGER
                );
                """));
    }

    /// <inheritdoc />
    public async Task<TokenSavingsPendingReport?> PrepareAsync(
        string computerName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(computerName);
        using var connection = await SqliteConnectionFactory.OpenAsync(connectionString, cancellationToken);
        EnsureSchema(connection);
        cancellationToken.ThrowIfCancellationRequested();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var state = connection.CreateCommand();
        state.Transaction = transaction;
        state.CommandText = """
            SELECT ComputerId, AcknowledgedTokens, PendingReportId, PendingComputerName, PendingTokens
            FROM TokenSavingsPublishState WHERE Id = 1;
            """;
        string? computerId = null;
        long acknowledged = 0;
        using (var reader = state.ExecuteReader())
        {
            if (reader.Read())
            {
                computerId = reader.GetString(0);
                acknowledged = reader.GetInt64(1);
                if (!reader.IsDBNull(2))
                {
                    var pending = new TokenSavingsPendingReport(
                        reader.GetString(2), computerId, reader.GetString(3), reader.GetInt64(4));
                    reader.Close();
                    transaction.Commit();
                    return pending;
                }
            }
        }

        // Read the persisted counter directly: an in-memory snapshot can be stale after a
        // failed refresh or include best-effort increments that never reached the database.
        using var totals = connection.CreateCommand();
        totals.Transaction = transaction;
        totals.CommandText = SqlStrings.SelectTokenSavingsTotals;
        long current;
        using (var reader = totals.ExecuteReader())
        {
            reader.Read();
            current = Math.Max(0, (reader.GetInt64(0) - reader.GetInt64(1)) / 4);
        }

        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        if (computerId is null)
        {
            update.CommandText = """
                INSERT INTO TokenSavingsPublishState (Id, ComputerId, AcknowledgedTokens)
                VALUES (1, $computerId, $current);
                """;
            update.Parameters.AddWithValue("$computerId", NewId("cmp_"));
            update.Parameters.AddWithValue("$current", current);
            update.ExecuteNonQuery();
            transaction.Commit();
            return null;
        }

        if (current < acknowledged)
        {
            // A reset/restored/decreased tally establishes a new local baseline. It must not
            // replay the old lifetime value or wait for that old value to be reached again.
            update.CommandText = "UPDATE TokenSavingsPublishState SET AcknowledgedTokens = $current WHERE Id = 1;";
            update.Parameters.AddWithValue("$current", current);
            update.ExecuteNonQuery();
            transaction.Commit();
            return null;
        }
        if (current == acknowledged)
        {
            transaction.Commit();
            return null;
        }

        var amount = Math.Min(MaximumReportTokens, current - acknowledged);
        var report = new TokenSavingsPendingReport(NewId("tsr_"), computerId, computerName, amount);
        update.CommandText = """
            UPDATE TokenSavingsPublishState
            SET PendingReportId = $reportId, PendingComputerName = $name,
                PendingTokens = $amount, PendingThroughTokens = $through
            WHERE Id = 1;
            """;
        update.Parameters.AddWithValue("$reportId", report.ReportId);
        update.Parameters.AddWithValue("$name", report.ComputerName);
        update.Parameters.AddWithValue("$amount", amount);
        update.Parameters.AddWithValue("$through", acknowledged + amount);
        update.ExecuteNonQuery();
        transaction.Commit();
        return report;
    }

    /// <inheritdoc />
    public async Task AcknowledgeAsync(string reportId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportId);
        using var connection = await SqliteConnectionFactory.OpenAsync(connectionString, cancellationToken);
        EnsureSchema(connection);
        cancellationToken.ThrowIfCancellationRequested();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE TokenSavingsPublishState
            SET AcknowledgedTokens = PendingThroughTokens, PendingReportId = NULL,
                PendingComputerName = NULL, PendingTokens = NULL, PendingThroughTokens = NULL
            WHERE Id = 1 AND PendingReportId = $reportId;
            """;
        command.Parameters.AddWithValue("$reportId", reportId);
        command.ExecuteNonQuery();
    }

    private static string NewId(string prefix) => prefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
}
