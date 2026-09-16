using Microsoft.Data.Sqlite;
using VibeRails.Data.Abstractions;

namespace VibeRails.Data.Sqlite;

/// <summary>Retries derived lexical-index writes without rescanning historical input at startup.</summary>
public sealed class SqliteSearchIndexMaintenanceStore(string connectionString) : ISearchIndexMaintenanceStore
{
    public async Task<int> RepairPendingAsync(int maxRows = 100, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRows, 1);
        var batchSize = Math.Min(maxRows, 100);
        try
        {
            await using var connection = await SqliteConnectionFactory.OpenAsync(connectionString, cancellationToken);
            using (var check = connection.CreateCommand())
            {
                check.CommandText = "SELECT 1 FROM UserInputSearchPending LIMIT 1;";
                if (await check.ExecuteScalarAsync(cancellationToken) is null)
                    return 0;
            }

            // Multiple root backends may run this job. Select after the write lock so
            // a second worker observes the first one's committed queue removals.
            await using var transaction = connection.BeginTransaction(deferred: false);
            var rows = new List<(long Id, string? Text)>(batchSize);
            using (var read = connection.CreateCommand())
            {
                read.Transaction = transaction;
                read.CommandText = """
                    SELECT pending.UserInputId, input.InputText
                    FROM UserInputSearchPending AS pending
                    LEFT JOIN UserInputs AS input ON input.Id = pending.UserInputId
                    ORDER BY pending.UserInputId
                    LIMIT $limit;
                    """;
                read.Parameters.AddWithValue("$limit", batchSize);
                await using var reader = await read.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                    rows.Add((reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
            }

            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SearchIndexWriter.Synchronize(connection, transaction, row.Id, row.Text);
            }
            await transaction.CommitAsync(cancellationToken);
            return rows.Count;
        }
        catch (SqliteException exception)
        {
            throw SqliteStorageErrors.Translate(exception);
        }
    }
}
