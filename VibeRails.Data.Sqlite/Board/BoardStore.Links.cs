using Microsoft.Data.Sqlite;

namespace VibeRails.Services.Board;

public sealed partial class BoardStore
{
    // Store each undirected pair once. Both ends cascade on card (or board) deletion.
    private const string CardLinksSchemaSql = """
        CREATE TABLE IF NOT EXISTS BoardCardLinks (
            CardId TEXT NOT NULL REFERENCES BoardCards(Id) ON DELETE CASCADE,
            LinkedCardId TEXT NOT NULL REFERENCES BoardCards(Id) ON DELETE CASCADE,
            PRIMARY KEY (CardId, LinkedCardId),
            CHECK (CardId < LinkedCardId)
        );
        CREATE INDEX IF NOT EXISTS IX_BoardCardLinks_LinkedCard ON BoardCardLinks(LinkedCardId);
        """;

    // A property because the prefix join interpolates ProjectPathCollation (see CardSequenceReseedSql).
    private static string LinkedCardSelect => $"""
        SELECT c.Id, c.Number, c.Title, b.Id, b.Name, k.Id, k.Name, {CardPrefixSql}, c.CardKey, c.DisplayId, c.ProjectPath
        FROM BoardCards c
        JOIN BoardColumns k ON k.Id = c.ColumnId
        JOIN Boards b ON b.Id = k.BoardId
        {CardPrefixJoinSql}
        """;

    public async Task<IReadOnlyList<BoardLinkedCardRecord>?> GetCardLinkCandidatesAsync(
        string projectPath, string? idOrKey, string query, CancellationToken cancellationToken = default,
        string? preferredProjectPath = null, bool currentProjectOnly = false)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        var card = idOrKey is null ? null : await ReadCardAsync(connection, null, project, idOrKey, cancellationToken);
        if (idOrKey is not null && card is null) return null;

        await using var command = connection.CreateCommand();
        command.CommandText = LinkedCardSelect + $"""

            WHERE c.Id <> $card AND c.DeletedUTC IS NULL
              AND (NOT $projectOnly OR c.ProjectPath = $sourceProject{ProjectPathCollation})
              AND (instr(lower(c.Title), lower($query)) > 0 OR instr(lower({CardKeySql}), lower($query)) > 0 OR instr(lower({CardDisplayIdSql}), lower($query)) > 0
                   OR instr(lower({CardPrefixSql} || '-' || c.Number), lower($query)) > 0)
              AND NOT EXISTS (
                SELECT 1 FROM BoardCardLinks l
                WHERE (l.CardId = $card AND l.LinkedCardId = c.Id)
                   OR (l.LinkedCardId = $card AND l.CardId = c.Id))
            ORDER BY (c.ProjectPath = $project{ProjectPathCollation}) DESC,
                (lower({CardDisplayIdSql}) = lower($query) OR lower({CardKeySql}) = lower($query) OR lower({CardPrefixSql} || '-' || c.Number) = lower($query)) DESC, c.Number DESC, c.Id
            LIMIT 50;
            """;
        command.Parameters.AddWithValue("$project", NormalizeProjectPath(preferredProjectPath ?? project));
        command.Parameters.AddWithValue("$sourceProject", project);
        command.Parameters.AddWithValue("$projectOnly", currentProjectOnly);
        command.Parameters.AddWithValue("$card", card?.Id ?? "");
        command.Parameters.AddWithValue("$query", query);
        return await ReadLinkedCardRowsAsync(command, cancellationToken);
    }

    public async Task<BoardLinkedCardRecord?> LinkCardAsync(
        string projectPath, string idOrKey, string targetIdOrKey, CancellationToken cancellationToken = default)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var card = await ReadCardAsync(connection, transaction, project, idOrKey, cancellationToken);
        var target = await ReadLinkTargetAsync(connection, transaction, project, targetIdOrKey, cancellationToken);
        if (card is null || target is null) return null;
        if (card.Id == target.Id) throw new BoardValidationException("A card cannot link to itself.");

        var (first, second) = OrderedCardPair(card.Id, target.Id);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR IGNORE INTO BoardCardLinks (CardId, LinkedCardId) VALUES ($first, $second);";
        command.Parameters.AddWithValue("$first", first);
        command.Parameters.AddWithValue("$second", second);
        if (await command.ExecuteNonQueryAsync(cancellationToken) > 0)
        {
            await TouchCardAsync(connection, transaction, first, cancellationToken);
            await TouchCardAsync(connection, transaction, second, cancellationToken);
        }
        command.CommandText = LinkedCardSelect + " WHERE c.Id = $target;";
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$target", target.Id);
        var linked = (await ReadLinkedCardRowsAsync(command, cancellationToken)).Single();
        await transaction.CommitAsync(cancellationToken);
        return linked;
    }

    public async Task<IReadOnlyList<string>> GetLinkedCardIdsAsync(string projectPath, string cardId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT CASE WHEN l.CardId = $card THEN l.LinkedCardId ELSE l.CardId END
            FROM BoardCardLinks l
            WHERE (l.CardId = $card OR l.LinkedCardId = $card)
              AND EXISTS (SELECT 1 FROM BoardCards c WHERE c.Id = $card
                  AND c.ProjectPath = $project{ProjectPathCollation} AND c.DeletedUTC IS NULL);
            """;
        command.Parameters.AddWithValue("$card", cardId);
        command.Parameters.AddWithValue("$project", NormalizeProjectPath(projectPath));
        var ids = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) ids.Add(reader.GetString(0));
        return ids;
    }

    public async Task<bool> UnlinkCardAsync(
        string projectPath, string idOrKey, string targetIdOrKey, CancellationToken cancellationToken = default)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var card = await ReadCardAsync(connection, transaction, project, idOrKey, cancellationToken);
        var target = await ReadLinkTargetAsync(connection, transaction, project, targetIdOrKey, cancellationToken);
        if (card is null || target is null) return false;

        var (first, second) = OrderedCardPair(card.Id, target.Id);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM BoardCardLinks WHERE CardId = $first AND LinkedCardId = $second;";
        command.Parameters.AddWithValue("$first", first);
        command.Parameters.AddWithValue("$second", second);
        var removed = await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        if (removed)
        {
            await TouchCardAsync(connection, transaction, first, cancellationToken);
            await TouchCardAsync(connection, transaction, second, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return removed;
    }

    private static (string First, string Second) OrderedCardPair(string cardId, string targetId) =>
        string.CompareOrdinal(cardId, targetId) < 0 ? (cardId, targetId) : (targetId, cardId);

    // Resolve global identities in this write transaction, never accept a caller-supplied
    // target project. Short keys/display labels retain their source-project meaning.
    private static async Task<BoardCardRecord?> ReadLinkTargetAsync(SqliteConnection connection,
        SqliteTransaction transaction, string project, string idOrKey, CancellationToken ct) =>
        await ReadLocalCardAsync(connection, transaction, idOrKey, ct)
        ?? await ReadCardAsync(connection, transaction, project, idOrKey, ct);

    private static async Task InsertDraftLinksAsync(SqliteConnection connection, SqliteTransaction transaction,
        BoardCardRecord card, IReadOnlyList<string>? targets, CancellationToken ct)
    {
        if (targets is null) return;
        if (targets.Count > 50) throw new BoardValidationException("A new card can link up to 50 cards.");
        foreach (var id in targets.Distinct(StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(id) || id.Length > 100) throw new BoardValidationException("Invalid linked card.");
            var target = await ReadLinkTargetAsync(connection, transaction, card.ProjectPath, id, ct)
                ?? throw new BoardValidationException("A linked card is no longer available locally.");
            if (target.Id == card.Id) throw new BoardValidationException("A card cannot link to itself.");
            var (first, second) = OrderedCardPair(card.Id, target.Id);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT OR IGNORE INTO BoardCardLinks (CardId, LinkedCardId) VALUES ($first, $second);";
            command.Parameters.AddWithValue("$first", first);
            command.Parameters.AddWithValue("$second", second);
            await command.ExecuteNonQueryAsync(ct);
            await TouchCardAsync(connection, transaction, target.Id, ct);
        }
    }

    // Hosted activity keeps the project-only default: linking another local project does
    // not authorize publishing that project's title/board metadata to this board's viewers.
    private static async Task<IReadOnlyList<BoardLinkedCardRecord>> ReadLinkedCardsAsync(
        SqliteConnection connection, string project, string cardId, CancellationToken cancellationToken,
        int limit = int.MaxValue, bool includeOtherProjects = false)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = LinkedCardSelect + $"""

            WHERE ($allProjects OR c.ProjectPath = $project{ProjectPathCollation}) AND c.DeletedUTC IS NULL AND c.Id IN (
                SELECT LinkedCardId FROM BoardCardLinks WHERE CardId = $card
                UNION ALL SELECT CardId FROM BoardCardLinks WHERE LinkedCardId = $card)
            ORDER BY (c.ProjectPath = $project{ProjectPathCollation}) DESC, c.Number, c.Id LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$project", project);
        command.Parameters.AddWithValue("$allProjects", includeOtherProjects);
        command.Parameters.AddWithValue("$card", cardId);
        command.Parameters.AddWithValue("$limit", limit);
        return await ReadLinkedCardRowsAsync(command, cancellationToken);
    }

    private static async Task<List<BoardLinkedCardRecord>> ReadLinkedCardRowsAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        var cards = new List<BoardLinkedCardRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            cards.Add(new BoardLinkedCardRecord(reader.GetString(0), reader.GetInt32(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), KeyPrefix: reader.GetString(7),
                StoredKey: reader.IsDBNull(8) ? null : reader.GetString(8), StoredDisplayId: reader.IsDBNull(9) ? null : reader.GetString(9),
                ProjectPath: reader.GetString(10)));
        return cards;
    }
}
