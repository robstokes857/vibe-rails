using Microsoft.Data.Sqlite;

namespace VibeRails.Services.Board;

public sealed partial class BoardStore
{
    private const string AttentionSchemaSql = """
        CREATE TABLE IF NOT EXISTS BoardAttentionRequests (
            CommentId TEXT PRIMARY KEY REFERENCES BoardComments(Id) ON DELETE CASCADE,
            CardId TEXT NOT NULL REFERENCES BoardCards(Id) ON DELETE CASCADE,
            SessionId TEXT,
            ResolvedUTC TEXT
        );
        CREATE INDEX IF NOT EXISTS IX_BoardAttentionRequests_Session
            ON BoardAttentionRequests(SessionId, ResolvedUTC);
        CREATE INDEX IF NOT EXISTS IX_BoardAttentionRequests_Card
            ON BoardAttentionRequests(CardId, ResolvedUTC);
        CREATE TRIGGER IF NOT EXISTS BoardCards_ResolveAttention
        AFTER UPDATE OF Flagged ON BoardCards
        WHEN NEW.Flagged = 0
        BEGIN
            UPDATE BoardAttentionRequests SET ResolvedUTC = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
            WHERE CardId = NEW.Id AND ResolvedUTC IS NULL;
        END;
        """;

    private static async Task InsertAttentionAsync(SqliteConnection connection, SqliteTransaction transaction,
        string cardId, BoardAuthor author, string reason, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO BoardComments (Id, CardId, AuthorKind, AuthorLabel, AuthorCli, SessionId, Body, CreatedUTC, Kind, Changes)
            VALUES ($id, $card, $kind, $label, $cli, $session, $body, $created, 'comment', $changes);
            INSERT INTO BoardAttentionRequests (CommentId, CardId, SessionId) VALUES ($id, $card, $session);
            """;
        command.Parameters.AddWithValue("$id", NewId("cm"));
        command.Parameters.AddWithValue("$card", cardId);
        command.Parameters.AddWithValue("$kind", author.Kind);
        command.Parameters.AddWithValue("$label", author.Label);
        command.Parameters.AddWithValue("$cli", (object?)author.Cli ?? DBNull.Value);
        command.Parameters.AddWithValue("$session", (object?)author.SessionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$body", reason);
        command.Parameters.AddWithValue("$created", ToDb(DateTime.UtcNow));
        command.Parameters.AddWithValue("$changes", BoardAttention.CommentChanges);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlySet<string>> GetAttentionSessionIdsAsync(IReadOnlyList<string> sessionIds,
        CancellationToken cancellationToken = default)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (sessionIds.Count == 0) return result;
        // An Automation's tab runs the outer recording; its Worker calls MCP from a separate
        // recording. Match the run's exact session IDs, never another session on the same card.
        var sessions = sessionIds.Distinct(StringComparer.Ordinal)
            .ToDictionary(id => id, id => new HashSet<string>(StringComparer.Ordinal) { id }, StringComparer.Ordinal);
        await using (var state = await OpenStateAsync(cancellationToken))
        {
            if (_stateFeatures.HasColumn(state, "JobRuns", "TerminalSessionId")
                && _stateFeatures.HasColumn(state, "JobRunActions", "SessionId"))
            {
                foreach (var batch in sessionIds.Distinct(StringComparer.Ordinal).Chunk(100))
                {
                    await using var query = state.CreateCommand();
                    var parameters = string.Join(",", batch.Select((_, i) => $"$s{i}"));
                    query.CommandText = $"""
                        SELECT r.TerminalSessionId, a.SessionId FROM JobRuns r
                        JOIN JobRunActions a ON a.RunId = r.Id
                        WHERE r.TerminalSessionId IN ({parameters}) AND a.SessionId IS NOT NULL;
                        """;
                    for (var i = 0; i < batch.Length; i++) query.Parameters.AddWithValue($"$s{i}", batch[i]);
                    await using var reader = await query.ExecuteReaderAsync(cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        var worker = reader.GetString(1);
                        if (!sessions.TryGetValue(worker, out var owners))
                            sessions[worker] = owners = new HashSet<string>(StringComparer.Ordinal);
                        owners.Add(reader.GetString(0));
                    }
                }
            }
        }
        await using var connection = await OpenAsync(cancellationToken);
        foreach (var batch in sessions.Keys.Chunk(100))
        {
            await using var query = connection.CreateCommand();
            var parameters = string.Join(",", batch.Select((_, i) => $"$s{i}"));
            query.CommandText = $"""
                SELECT DISTINCT a.SessionId FROM BoardAttentionRequests a
                JOIN BoardCards c ON c.Id = a.CardId
                WHERE a.SessionId IN ({parameters}) AND a.ResolvedUTC IS NULL
                  AND c.Flagged = 1 AND c.DeletedUTC IS NULL;
                """;
            for (var i = 0; i < batch.Length; i++) query.Parameters.AddWithValue($"$s{i}", batch[i]);
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) result.UnionWith(sessions[reader.GetString(0)]);
        }
        return result;
    }
}
