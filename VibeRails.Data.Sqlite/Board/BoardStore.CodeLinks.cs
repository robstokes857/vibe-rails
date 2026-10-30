using System.Data;
using Microsoft.Data.Sqlite;

namespace VibeRails.Services.Board;

public sealed partial class BoardStore
{
    private const string CodeLinksSchemaSql = """
        CREATE TABLE IF NOT EXISTS BoardCodeLinks (
            Id TEXT NOT NULL PRIMARY KEY,
            CardId TEXT NOT NULL REFERENCES BoardCards(Id) ON DELETE CASCADE,
            Url TEXT NOT NULL,
            Kind TEXT NOT NULL,
            Label TEXT NOT NULL,
            CreatedUTC TEXT NOT NULL,
            UNIQUE(CardId, Url)
        );
        """;

    /// <inheritdoc />
    public async Task<IReadOnlyList<BoardCodeLink>> GetCodeLinksAsync(string projectPath, string cardId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var card = await ReadCardAsync(connection, null, NormalizeProjectPath(projectPath), cardId, cancellationToken);
        return card is null ? [] : await ReadCodeLinksAsync(connection, null, card.Id, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<BoardCodeLink?> AddCodeLinkAsync(string projectPath, string cardId, string url, CancellationToken cancellationToken = default)
    {
        var parsed = BoardCodeLinks.Parse(url);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var card = await ReadCardForWriteAsync(connection, transaction, NormalizeProjectPath(projectPath), cardId, cancellationToken);
        if (card is null) return null;
        var links = await ReadCodeLinksAsync(connection, transaction, card.Id, cancellationToken);
        var existing = links.FirstOrDefault(link => link.Url == parsed.Url);
        if (existing is not null) return existing;
        if (links.Count >= BoardCodeLinks.MaxLinks)
            throw new BoardValidationException($"A card can have at most {BoardCodeLinks.MaxLinks} GitHub links.");
        var link = parsed with { Id = "code_" + Guid.NewGuid().ToString("N"), CreatedUtc = DateTime.UtcNow };
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO BoardCodeLinks (Id, CardId, Url, Kind, Label, CreatedUTC) VALUES ($id, $card, $url, $kind, $label, $created);";
        command.Parameters.AddWithValue("$id", link.Id);
        command.Parameters.AddWithValue("$card", card.Id);
        command.Parameters.AddWithValue("$url", link.Url);
        command.Parameters.AddWithValue("$kind", link.Kind);
        command.Parameters.AddWithValue("$label", link.Label);
        command.Parameters.AddWithValue("$created", ToDb(link.CreatedUtc));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await TouchCardAsync(connection, transaction, card.Id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return link;
    }

    /// <inheritdoc />
    public async Task<bool> RemoveCodeLinkAsync(string projectPath, string cardId, string linkId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var card = await ReadCardForWriteAsync(connection, transaction, NormalizeProjectPath(projectPath), cardId, cancellationToken);
        if (card is null) return false;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM BoardCodeLinks WHERE CardId=$card AND Id=$id;";
        command.Parameters.AddWithValue("$card", card.Id);
        command.Parameters.AddWithValue("$id", linkId);
        var removed = await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        if (removed) await TouchCardAsync(connection, transaction, card.Id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return removed;
    }

    private static async Task<IReadOnlyList<BoardCodeLink>> ReadCodeLinksAsync(SqliteConnection connection,
        SqliteTransaction? transaction, string cardId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT Id, Url, Kind, Label, CreatedUTC FROM BoardCodeLinks WHERE CardId=$card ORDER BY CreatedUTC, Id;";
        command.Parameters.AddWithValue("$card", cardId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var links = new List<BoardCodeLink>();
        while (await reader.ReadAsync(ct))
            links.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), ParseDb(reader.GetString(4))));
        return links;
    }
}
