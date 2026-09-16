using Microsoft.Data.Sqlite;
using VibeRails.Data.Abstractions;

namespace VibeRails.Data.Sqlite;

/// <summary>Retries derived lexical-index writes without rescanning historical input at startup.</summary>
public sealed class SqliteSearchIndexMaintenanceStore(string connectionString) : ISearchIndexMaintenanceStore
{
    private const int SqliteCorrupt = 11;

    /// <summary>
    /// Selects a batch of queued rows together with whether the shipped 1.10.10 binary already
    /// indexed each one directly (present in the FTS shadow table, absent from the content table).
    /// Writing such a row again through the content table leaves FTS5 with two entries and a row
    /// total of two for one prompt, which its strict integrity check reports as corruption, so a
    /// batch that contained any of them ends with a rebuild from the content table.
    /// </summary>
    internal const string SelectPendingBatchSql = """
        SELECT pending.UserInputId, input.InputText,
               EXISTS (SELECT 1 FROM UserInputs_fts_docsize AS indexed WHERE indexed.id = pending.UserInputId)
               AND NOT EXISTS (SELECT 1 FROM UserInputSearchDocuments AS content WHERE content.UserInputId = pending.UserInputId)
        FROM UserInputSearchPending AS pending
        LEFT JOIN UserInputs AS input ON input.Id = pending.UserInputId
        ORDER BY pending.UserInputId
        LIMIT $limit;
        """;

    public async Task<int> RepairPendingAsync(int maxRows = 100, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRows, 1);
        var batchSize = Math.Min(maxRows, 100);
        try
        {
            await using var connection = await SqliteConnectionFactory.OpenAsync(connectionString, cancellationToken);
            if (!await HasPendingAsync(connection, cancellationToken))
                return 0;

            // Multiple root backends may run this job. Select after the write lock so
            // a second worker observes the first one's committed queue removals.
            var legacyIndexed = 0;
            var rows = new List<(long Id, string? Text)>(batchSize);
            await using (var transaction = connection.BeginTransaction(deferred: false))
            {
                using (var read = connection.CreateCommand())
                {
                    read.Transaction = transaction;
                    read.CommandText = SelectPendingBatchSql;
                    read.Parameters.AddWithValue("$limit", batchSize);
                    await using var reader = await read.ExecuteReaderAsync(cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        rows.Add((reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
                        if (reader.GetInt64(2) != 0)
                            legacyIndexed++;
                    }
                }

                foreach (var row in rows)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    SearchIndexWriter.Synchronize(connection, transaction, row.Id, row.Text);
                }
                await transaction.CommitAsync(cancellationToken);
            }

            if (legacyIndexed > 0)
                await RebuildIndexAsync(connection, cancellationToken);
            return rows.Count;
        }
        catch (SqliteException exception)
        {
            throw SqliteStorageErrors.Translate(exception);
        }
    }

    /// <summary>
    /// The index is derived, so any inconsistency is repairable by rebuilding it from
    /// UserInputSearchDocuments. Two things put it out of step: the shipped 1.10.10 binary, which
    /// still inserts into UserInputs_fts directly (if the prompt is deleted before the queue drains,
    /// its postings are orphaned), and any FTS5 internal damage. The strict integrity check
    /// (rank=1) compares the index with the content table; the plain form only checks structure.
    /// Only a quiet index is judged -- queued rows are the normal path, not damage.
    /// </summary>
    public async Task<bool> RepairIndexIfInconsistentAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await SqliteConnectionFactory.OpenAsync(connectionString, cancellationToken);
            if (await HasPendingAsync(connection, cancellationToken))
                return false;

            var inconsistent = await CountOrphanedIndexRowsAsync(connection, cancellationToken) > 0;
            if (!inconsistent)
            {
                try
                {
                    using var check = connection.CreateCommand();
                    check.CommandText = "INSERT INTO UserInputs_fts(UserInputs_fts, rank) VALUES('integrity-check', 1);";
                    await check.ExecuteNonQueryAsync(cancellationToken);
                }
                catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteCorrupt)
                {
                    inconsistent = true;
                }
            }
            if (!inconsistent)
                return false;
            return await RebuildIndexAsync(connection, cancellationToken);
        }
        catch (SqliteException exception)
        {
            throw SqliteStorageErrors.Translate(exception);
        }
    }

    /// <summary>Rebuilds under the write lock unless a drain is still pending; returns whether it rebuilt.</summary>
    private static async Task<bool> RebuildIndexAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var transaction = connection.BeginTransaction(deferred: false);
        if (await HasPendingAsync(connection, cancellationToken))
        {
            // A rebuild indexes only content rows; anything still queued would be re-added by the
            // next drain anyway, which then rebuilds again if it has to.
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }
        using (var rebuild = connection.CreateCommand())
        {
            rebuild.Transaction = transaction;
            rebuild.CommandText = "INSERT INTO UserInputs_fts(UserInputs_fts) VALUES('rebuild');";
            await rebuild.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static async Task<bool> HasPendingAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM UserInputSearchPending LIMIT 1;";
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<long> CountOrphanedIndexRowsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        // UserInputs_fts_docsize is the FTS5 shadow table listing every indexed rowid; an entry with
        // no content row is exactly a direct write that nothing will ever delete.
        command.CommandText = """
            SELECT COUNT(*) FROM UserInputs_fts_docsize AS indexed
            WHERE NOT EXISTS (SELECT 1 FROM UserInputSearchDocuments AS content WHERE content.UserInputId = indexed.id);
            """;
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }
}
