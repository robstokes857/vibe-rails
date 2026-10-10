namespace VibeRails.Services.Board;

public sealed partial class BoardStore
{
    // Match the existing completed-lane UI/paging convention, including a named Closed lane.
    private static string DesktopClosedLaneSql(string name) =>
        $"(instr(lower({name}), 'ship') > 0 OR instr(lower({name}), 'done') > 0 " +
        $"OR instr(lower({name}), 'complete') > 0 OR instr(lower({name}), 'closed') > 0)";

    private static string DesktopActivitySchemaSql => $"""
        CREATE TABLE IF NOT EXISTS BoardDesktopActivity (
            CardId TEXT NOT NULL,
            ClientId TEXT NOT NULL,
            ClientLabel TEXT NOT NULL,
            ExpiresUTC TEXT NOT NULL,
            EndedUTC TEXT,
            PRIMARY KEY (CardId, ClientId)
        );
        CREATE INDEX IF NOT EXISTS IX_BoardDesktopActivity_Client
            ON BoardDesktopActivity(ClientId, EndedUTC, ExpiresUTC);
        CREATE TRIGGER IF NOT EXISTS BoardCards_EndDesktopActivity
        AFTER UPDATE OF ColumnId, DeletedUTC ON BoardCards
        WHEN NEW.DeletedUTC IS NOT NULL OR EXISTS (
            SELECT 1 FROM BoardColumns lane WHERE lane.Id = NEW.ColumnId
                AND {DesktopClosedLaneSql("lane.Name")})
        BEGIN
            UPDATE BoardDesktopActivity SET EndedUTC = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
            WHERE CardId = NEW.Id AND EndedUTC IS NULL;
        END;
        CREATE TRIGGER IF NOT EXISTS BoardCards_DeleteDesktopActivity
        AFTER DELETE ON BoardCards
        BEGIN
            UPDATE BoardDesktopActivity SET EndedUTC = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
            WHERE CardId = OLD.Id AND EndedUTC IS NULL;
        END;
        CREATE TRIGGER IF NOT EXISTS BoardColumns_EndDesktopActivity
        AFTER UPDATE OF Name ON BoardColumns
        WHEN {DesktopClosedLaneSql("NEW.Name")}
        BEGIN
            UPDATE BoardDesktopActivity SET EndedUTC = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
            WHERE EndedUTC IS NULL AND CardId IN (
                SELECT Id FROM BoardCards WHERE ColumnId = NEW.Id);
        END;
        """;

    /// <inheritdoc />
    public async Task<bool> StartDesktopActivityAsync(string projectPath, string cardId, string clientId,
        string clientLabel, DateTime expiresUtc, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        var now = DateTime.UtcNow;
        if (expiresUtc.ToUniversalTime() <= now) return false;
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        // One statement validates current membership and starts the lease atomically, including
        // when another process closes or deletes the card at the same time.
        command.CommandText = $"""
            INSERT INTO BoardDesktopActivity (CardId, ClientId, ClientLabel, ExpiresUTC, EndedUTC)
            SELECT c.Id, $client, $label, $expires, NULL
            FROM BoardCards c JOIN BoardColumns lane ON lane.Id = c.ColumnId
            WHERE c.Id = $card AND c.ProjectPath = $project{ProjectPathCollation}
                AND c.DeletedUTC IS NULL AND NOT {DesktopClosedLaneSql("lane.Name")}
            ON CONFLICT(CardId, ClientId) DO UPDATE SET
                ClientLabel = excluded.ClientLabel, ExpiresUTC = excluded.ExpiresUTC, EndedUTC = NULL;
            """;
        command.Parameters.AddWithValue("$client", clientId);
        command.Parameters.AddWithValue("$label", clientLabel);
        command.Parameters.AddWithValue("$expires", ToDb(expiresUtc));
        command.Parameters.AddWithValue("$card", cardId);
        command.Parameters.AddWithValue("$project", NormalizeProjectPath(projectPath));
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <inheritdoc />
    public async Task RenewDesktopActivityAsync(string clientId, DateTime expiresUtc,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        if (expiresUtc.ToUniversalTime() <= now) return;
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        // Evaluate freshness after SQLite obtains its writer lock. A lease can expire while
        // this operation waits behind another process, and that wait must not resurrect it.
        command.CommandText = $"""
            UPDATE BoardDesktopActivity SET ExpiresUTC = $expires
            WHERE ClientId = $client AND EndedUTC IS NULL AND julianday(ExpiresUTC) > julianday('now')
                AND EXISTS (
                    SELECT 1 FROM BoardCards c JOIN BoardColumns lane ON lane.Id = c.ColumnId
                    WHERE c.Id = BoardDesktopActivity.CardId AND c.DeletedUTC IS NULL
                        AND NOT {DesktopClosedLaneSql("lane.Name")});
            """;
        command.Parameters.AddWithValue("$client", clientId);
        command.Parameters.AddWithValue("$expires", ToDb(expiresUtc));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task EndDesktopActivityAsync(string clientId, string? cardId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE BoardDesktopActivity SET EndedUTC = $now
            WHERE ClientId = $client AND ($card IS NULL OR CardId = $card) AND EndedUTC IS NULL;
            """;
        command.Parameters.AddWithValue("$client", clientId);
        command.Parameters.AddWithValue("$card", (object?)cardId ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", ToDb(DateTime.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlySet<string>> GetDesktopActiveCardIdsAsync(string projectPath,
        IReadOnlyList<string> cardIds, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (cardIds.Count == 0) return result;
        await using var connection = await OpenAsync(cancellationToken);
        foreach (var batch in cardIds.Distinct(StringComparer.Ordinal).Chunk(100))
        {
            await using var command = connection.CreateCommand();
            var parameters = string.Join(",", batch.Select((_, i) => $"$card{i}"));
            command.CommandText = $"""
                SELECT DISTINCT activity.CardId FROM BoardDesktopActivity activity
                JOIN BoardCards c ON c.Id = activity.CardId
                JOIN BoardColumns lane ON lane.Id = c.ColumnId
                WHERE activity.CardId IN ({parameters}) AND activity.EndedUTC IS NULL
                    AND activity.ExpiresUTC > $now AND c.ProjectPath = $project{ProjectPathCollation}
                    AND c.DeletedUTC IS NULL AND NOT {DesktopClosedLaneSql("lane.Name")};
                """;
            command.Parameters.AddWithValue("$project", NormalizeProjectPath(projectPath));
            command.Parameters.AddWithValue("$now", ToDb(nowUtc));
            for (var i = 0; i < batch.Length; i++) command.Parameters.AddWithValue($"$card{i}", batch[i]);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) result.Add(reader.GetString(0));
        }
        return result;
    }
}
