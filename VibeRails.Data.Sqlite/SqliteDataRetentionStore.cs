using System.Globalization;
using Microsoft.Data.Sqlite;
using VibeRails.Data.Abstractions;
using VibeRails.Services.BertV2;

namespace VibeRails.Data.Sqlite;

/// <summary>
/// Resumable, bounded retention. Acknowledged completed sessions are the deletion boundary;
/// open/unexported sessions and unattributed proxy records are never inferred or removed.
/// </summary>
/// <remarks>
/// Every write transaction here touches exactly one database file. SQLite's BEGIN IMMEDIATE takes
/// the write lock on every ATTACHed database as well as the main one, so a cross-file join inside a
/// write transaction held state.db's writer lock for the whole proxy scan and blocked every other
/// process for 7-15 s per tick (VB-U2CEM-80). The proxy database is attached only for the read-only
/// candidate lookup and detached before any state.db write; the proxy prune reads its proof from
/// state.db on a separate connection and attaches nothing.
/// </remarks>
public sealed class SqliteDataRetentionStore(
    string stateConnectionString,
    string proxyDatabasePath,
    string? vectorDatabasePath = null) : IDataRetentionStore
{
    internal const int StateBatchSize = 5000;
    internal const int MaxStateBatches = 20;
    internal const int ProxyBatchSize = 50;
    internal const int MaxProxyBatches = 10;
    internal const int VectorBatchSize = 500;

    public Task<DataRetentionResult> PruneAsync(DateTime nowUtc, CancellationToken cancellationToken)
        => PruneAsync(nowUtc, includeProxy: true, cancellationToken);

    public async Task<DataRetentionResult> PruneAsync(DateTime nowUtc, bool includeProxy, CancellationToken cancellationToken)
    {
        if (nowUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Retention requires UTC.", nameof(nowUtc));
        try
        {
            var (proxyDeleted, proxyComplete) = includeProxy
                ? await PruneProxyAsync(nowUtc.AddDays(-7), cancellationToken)
                : (0L, false);
            await using var state = await SqliteConnectionFactory.OpenAsync(
                new SqliteConnectionStringBuilder(stateConnectionString) { Pooling = false }.ToString(), cancellationToken);
            var candidates = await ReadCandidatesAsync(state, nowUtc, cancellationToken);

            var tables = await ReadTablesAsync(state, cancellationToken);
            var hasLegacyCleanedReference = tables.Contains("CleanedUserInput")
                && SqliteSchema.HasColumn(state, null, "UserInputs", "CleanedId");
            long stateDeleted = 0;
            long embeddingsDeleted = 0;
            var sessionsDeleted = 0;
            var batches = 0;
            foreach (var sessionId in candidates)
            {
                // Every child delete is its own commit. The parent is deliberately retained until
                // all children are gone so an interruption resumes the same indexed work next tick.
                foreach (var (table, predicate, unlinkColumn) in new (string, string, string?)[]
                {
                    ("InputFileChanges", "UserInputId IN (SELECT Id FROM UserInputs WHERE SessionId=$id)", null),
                    ("InputFileChanges", "PreviousInputId IN (SELECT Id FROM UserInputs WHERE SessionId=$id)", "PreviousInputId"),
                    // Historical databases have both UserInputs.CleanedId -> CleanedUserInput
                    // and CleanedUserInput.UserInputId -> UserInputs. Break the reverse link
                    // first, including surviving inputs, while preserving their original text.
                    ("UserInputs", "CleanedId IN (SELECT Id FROM CleanedUserInput WHERE SessionId=$id)", "CleanedId"),
                    ("CleanedUserInput", "SessionId=$id", null),
                    ("ClaudePlans", "SessionId=$id", null),
                    ("UserInputs", "SessionId=$id", null),
                    ("SessionLogs", "SessionId=$id", null),
                    ("TerminalSessionLogs", "SessionId=$id", null),
                    ("sessionOutPut", "SessionId=$id", null),
                    ("ChatSummary", "SessionId=$id", null)
                })
                {
                    if (!tables.Contains(table) || (unlinkColumn == "CleanedId" && !hasLegacyCleanedReference))
                        continue;
                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (batches >= MaxStateBatches)
                            return new(sessionsDeleted, stateDeleted, proxyDeleted, embeddingsDeleted, proxyComplete);
                        await using var transaction = state.BeginTransaction(deferred: false);
                        using var delete = state.CreateCommand();
                        delete.Transaction = transaction;
                        // Keep surviving diffs when their comparison input ages out. Unlinking
                        // is bounded just like deletion, using the PreviousInputId index.
                        var action = unlinkColumn is not null ? $"UPDATE {table} SET {unlinkColumn}=NULL" : $"DELETE FROM {table}";
                        delete.CommandText = $"""
                            {action} WHERE rowid IN (
                                SELECT rowid FROM {table} WHERE ({predicate}) LIMIT $limit
                            ) AND EXISTS (SELECT 1 FROM Sessions WHERE Id=$id AND EndedUTC < $cutoff AND ExportedUTC IS NOT NULL);
                            """;
                        delete.Parameters.AddWithValue("$id", sessionId);
                        delete.Parameters.AddWithValue("$limit", StateBatchSize);
                        delete.Parameters.AddWithValue("$cutoff", ToDb(nowUtc.AddMonths(-1)));
                        var removed = await delete.ExecuteNonQueryAsync(cancellationToken);
                        await transaction.CommitAsync(cancellationToken);
                        if (unlinkColumn is null)
                            stateDeleted += removed;
                        if (removed == 0)
                            break;
                        batches++;
                        Checkpoint(state);
                        if (removed < StateBatchSize)
                            break;
                    }
                }
                // Derived embeddings go BEFORE the parent row. The vector store has no session
                // table to join against, so the Sessions row is the only thing that makes this
                // session a retention candidate at all -- delete it first and an interruption
                // orphans the vectors permanently, leaving the pruned text searchable for good.
                var (vectorsDone, vectorsRemoved) = await PruneSessionVectorsAsync(sessionId, cancellationToken);
                embeddingsDeleted += vectorsRemoved;
                if (!vectorsDone)
                    continue;

                await using (var transaction = state.BeginTransaction(deferred: false))
                {
                    using var delete = state.CreateCommand();
                    delete.Transaction = transaction;
                    delete.CommandText = "UPDATE Sessions SET ParentSessionId='' WHERE ParentSessionId=$id;";
                    delete.Parameters.AddWithValue("$id", sessionId);
                    await delete.ExecuteNonQueryAsync(cancellationToken);
                    delete.CommandText = "DELETE FROM Sessions WHERE Id=$id AND EndedUTC < $cutoff AND ExportedUTC IS NOT NULL;";
                    delete.Parameters.AddWithValue("$cutoff", ToDb(nowUtc.AddMonths(-1)));
                    sessionsDeleted += await delete.ExecuteNonQueryAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                }
                Checkpoint(state);
            }
            return new(sessionsDeleted, stateDeleted, proxyDeleted, embeddingsDeleted, proxyComplete);
        }
        catch (SqliteException ex)
        {
            throw new StorageException("Local retention could not finish; committed batches will resume next time.", ex.SqliteErrorCode is 5 or 6, ex);
        }
    }

    /// <summary>
    /// The oldest acknowledged completed sessions whose proxy exchanges are all gone. The proxy
    /// database is attached for this read only and detached again before any write transaction:
    /// a BEGIN IMMEDIATE would otherwise lock proxy_exchanges.db for every state batch.
    /// </summary>
    private async Task<List<string>> ReadCandidatesAsync(SqliteConnection state, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var (proxyAttached, proxyUsable) = await AttachProxyAsync(state, cancellationToken);
        try
        {
            var candidates = new List<string>();
            using var select = state.CreateCommand();
            select.CommandText = """
                SELECT s.Id FROM Sessions s
                WHERE s.EndedUTC IS NOT NULL AND s.EndedUTC < $cutoff AND s.ExportedUTC IS NOT NULL
                """ + (proxyUsable ? " AND NOT EXISTS (SELECT 1 FROM retention_proxy.ProxyExchanges p WHERE p.SessionId=s.Id)" : "")
                + " ORDER BY s.EndedUTC, s.Id LIMIT 20;";
            select.Parameters.AddWithValue("$cutoff", ToDb(nowUtc.AddMonths(-1)));
            await using (var reader = await select.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                    candidates.Add(reader.GetString(0));
            }
            return candidates;
        }
        finally
        {
            if (proxyAttached)
                await DetachProxyAsync(state, cancellationToken);
        }
    }

    /// <summary>
    /// Deletes exchanges the acknowledged envelope provably contained, one session at a time.
    /// The proof (each session's rowid high-water mark) is read from state.db on its own connection
    /// first, so the proxy connection attaches nothing and its write transactions lock only
    /// proxy_exchanges.db. Per-session lookups use IX_ProxyExchanges_SessionId and read CreatedUTC,
    /// an early column. The former date-range scan read SessionId, the LAST column of ~1 MB rows,
    /// which walks each row's overflow chain: 7-11 s per tick on a 40 GB file that had nothing to delete.
    /// </summary>
    /// <returns>
    /// Rows deleted, and whether nothing eligible remains. Complete is false when the batch cap
    /// stopped the pass, so the caller must not defer the next check.
    /// </returns>
    private async Task<(long Deleted, bool Complete)> PruneProxyAsync(DateTime cutoff, CancellationToken cancellationToken)
    {
        if (!File.Exists(proxyDatabasePath))
            return (0, true);
        var proofs = await ReadProxyProofsAsync(cancellationToken);
        if (proofs.Count == 0)
            return (0, true);
        await using var proxy = await SqliteConnectionFactory.OpenAsync(
            new SqliteConnectionStringBuilder { DataSource = proxyDatabasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString(), cancellationToken);
        using (var check = proxy.CreateCommand())
        {
            check.CommandText = "SELECT 1 FROM pragma_table_info('ProxyExchanges') WHERE name='SessionId';";
            if (await check.ExecuteScalarAsync(cancellationToken) is null)
                return (0, true);
        }
        // Only rows the acknowledged envelope actually contained: coverage "included" up to the
        // snapshot's largest rowid. An "empty" snapshot contained nothing, so every row present
        // now arrived after it. CreatedUTC cannot decide this on its own -- the proxy queues an
        // exchange stamped with it and may write the row after the snapshot was taken.
        const string eligible = "SessionId=$id AND rowid <= $maxRowId AND CreatedUTC < $cutoff";
        long total = 0;
        var batches = 0;
        foreach (var (sessionId, maxRowId) in proofs)
        {
            // A plain read first: a session with nothing to delete, the common case, never takes
            // the write lock that the TokenSaver proxy in every tab child needs.
            using (var probe = proxy.CreateCommand())
            {
                probe.CommandText = $"SELECT 1 FROM ProxyExchanges WHERE {eligible} LIMIT 1;";
                AddProofParameters(probe, sessionId, maxRowId, cutoff);
                if (await probe.ExecuteScalarAsync(cancellationToken) is null)
                    continue;
            }
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (batches >= MaxProxyBatches)
                    return (total, false);
                await using var transaction = proxy.BeginTransaction(deferred: false);
                using var delete = proxy.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = $"""
                    DELETE FROM ProxyExchanges WHERE rowid IN (
                        SELECT rowid FROM ProxyExchanges WHERE {eligible}
                        ORDER BY CreatedUTC, rowid LIMIT $limit
                    );
                    """;
                AddProofParameters(delete, sessionId, maxRowId, cutoff);
                delete.Parameters.AddWithValue("$limit", ProxyBatchSize);
                var removed = await delete.ExecuteNonQueryAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                total += removed;
                if (removed == 0)
                    break;
                batches++;
                Checkpoint(proxy);
                if (removed < ProxyBatchSize)
                    break;
            }
        }
        return (total, true);
    }

    /// <summary>
    /// Sessions whose acknowledged envelope proves which proxy rows were backed up, oldest first.
    /// State migrations 3 and 4 add the coverage proof and its rowid boundary. A state database
    /// that predates them cannot prove anything was backed up, so prune nothing rather than fall
    /// back to ExportedUTC.
    /// </summary>
    private async Task<List<(string SessionId, long MaxRowId)>> ReadProxyProofsAsync(CancellationToken cancellationToken)
    {
        await using var state = await SqliteConnectionFactory.OpenAsync(
            new SqliteConnectionStringBuilder(stateConnectionString) { Pooling = false }.ToString(), cancellationToken);
        var proofs = new List<(string, long)>();
        if (!SqliteSchema.HasColumn(state, null, "Sessions", "ExportedProxyMaxRowId"))
            return proofs;
        using var select = state.CreateCommand();
        select.CommandText = """
            SELECT Id, ExportedProxyMaxRowId FROM Sessions
            WHERE EndedUTC IS NOT NULL AND ExportedUTC IS NOT NULL
              AND ExportedProxyCoverage='included' AND ExportedProxyMaxRowId IS NOT NULL
            ORDER BY EndedUTC, Id;
            """;
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            proofs.Add((reader.GetString(0), reader.GetInt64(1)));
        return proofs;
    }

    private static void AddProofParameters(SqliteCommand command, string sessionId, long maxRowId, DateTime cutoff)
    {
        command.Parameters.AddWithValue("$id", sessionId);
        command.Parameters.AddWithValue("$maxRowId", maxRowId);
        command.Parameters.AddWithValue("$cutoff", ToDb(cutoff));
    }

    /// <summary>
    /// Removes this session's per-message and aggregate embeddings, and their vec0 rows, from the
    /// vector database. Bounded per call; returns Done=false when more remain, so the caller keeps
    /// the Sessions row and the next tick resumes.
    /// </summary>
    private async Task<(bool Done, long Deleted)> PruneSessionVectorsAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(vectorDatabasePath) || !File.Exists(vectorDatabasePath))
            return (true, 0);
        await using var vectors = BertVectorDatabase.Open(vectorDatabasePath);
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var tables = vectors.CreateCommand())
        {
            tables.CommandText = "SELECT name FROM sqlite_schema WHERE type IN ('table','view');";
            await using var reader = await tables.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                present.Add(reader.GetString(0));
        }

        var targets = new List<(string DocTable, string VecTable, string Id)>();
        // Aggregate chunks carry a real SessionId column.
        if (present.Contains(BertSearchSchema.SessionDocumentTableName))
            await CollectAsync(vectors, targets,
                BertSearchSchema.SessionDocumentTableName, BertSearchSchema.SessionVectorTableName,
                $"SELECT Id FROM {BertSearchSchema.SessionDocumentTableName} WHERE SessionId=$key LIMIT $limit;",
                sessionId, cancellationToken);
        // Per-message documents are keyed "<sessionId>:<userInputId>" with no SessionId column.
        if (present.Contains(BertSearchSchema.DocumentTableName) && targets.Count < VectorBatchSize)
            await CollectAsync(vectors, targets,
                BertSearchSchema.DocumentTableName, BertSearchSchema.VectorTableName,
                $@"SELECT Id FROM {BertSearchSchema.DocumentTableName} WHERE Id LIKE $prefix ESCAPE '\' LIMIT $limit;",
                EscapeLike(sessionId) + ":%", cancellationToken);

        if (targets.Count == 0)
            return (true, 0);

        long deleted = 0;
        await using (var transaction = vectors.BeginTransaction(deferred: false))
        {
            foreach (var (docTable, vecTable, id) in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var delete = vectors.CreateCommand();
                delete.Transaction = transaction;
                // One id at a time: vec0 virtual tables support deletion by primary key, not by
                // subquery. Mirrors BertVectorDatabase.PurgeLegacySecretDocuments.
                delete.CommandText = $"DELETE FROM {vecTable} WHERE Id=$id; DELETE FROM {docTable} WHERE Id=$id;";
                delete.Parameters.AddWithValue("$id", id);
                deleted += await delete.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        Checkpoint(vectors);
        return (targets.Count < VectorBatchSize, deleted);
    }

    private static async Task CollectAsync(SqliteConnection connection,
        List<(string, string, string)> targets, string docTable, string vecTable, string sql,
        string key, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$prefix", key);
        command.Parameters.AddWithValue("$limit", VectorBatchSize - targets.Count);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            targets.Add((docTable, vecTable, reader.GetString(0)));
    }

    /// <summary>Escapes LIKE wildcards so a session id containing % or _ cannot over-match.</summary>
    private static string EscapeLike(string value) => value
        .Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    /// <returns>Whether the proxy file was attached at all, and whether it has a usable schema.</returns>
    private async Task<(bool Attached, bool Usable)> AttachProxyAsync(SqliteConnection state, CancellationToken cancellationToken)
    {
        if (!File.Exists(proxyDatabasePath))
            return (false, false);
        using var command = state.CreateCommand();
        command.CommandText = "ATTACH DATABASE $path AS retention_proxy;";
        command.Parameters.AddWithValue("$path", proxyDatabasePath);
        await command.ExecuteNonQueryAsync(cancellationToken);
        // pragma_table_info's schema argument avoids selecting a same-named main table.
        command.CommandText = "SELECT 1 FROM pragma_table_info('ProxyExchanges', 'retention_proxy') WHERE name='SessionId';";
        return (true, await command.ExecuteScalarAsync(cancellationToken) is not null);
    }

    private static async Task DetachProxyAsync(SqliteConnection state, CancellationToken cancellationToken)
    {
        using var command = state.CreateCommand();
        command.CommandText = "DETACH DATABASE retention_proxy;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<HashSet<string>> ReadTablesAsync(SqliteConnection state, CancellationToken cancellationToken)
    {
        using var command = state.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_schema WHERE type='table';";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(cancellationToken))
            tables.Add(reader.GetString(0));
        return tables;
    }

    private static void Checkpoint(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(PASSIVE);";
        command.ExecuteNonQuery();
    }

    private static string ToDb(DateTime value) => value.ToString("O", CultureInfo.InvariantCulture);
}
