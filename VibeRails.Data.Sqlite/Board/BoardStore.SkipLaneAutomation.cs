namespace VibeRails.Services.Board;

public sealed partial class BoardStore
{
    public async Task<bool> SkipLaneAutomationAsync(BoardLaneAutomationEvent entry, BoardAuthor author, string comment,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var card = await ReadCardAsync(connection, transaction, NormalizeProjectPath(entry.ProjectPath), entry.CardId, cancellationToken);
        if (card is null) return false;
        await using (var pending = connection.CreateCommand())
        {
            pending.Transaction = transaction;
            pending.CommandText = $"""
                SELECT EXISTS (SELECT 1 FROM ({PendingLaneEntriesSql}) p
                    WHERE p.CardId = $card AND p.JobId = $job AND p.EventKey = $event AND p.ColumnId = $lane);
                """;
            BindEntry(pending, entry);
            pending.Parameters.AddWithValue("$lane", card.ColumnId);
            if (Convert.ToInt64(await pending.ExecuteScalarAsync(cancellationToken)) == 0) return false;
        }
        await WriteLaneAutomationDispatchAsync(connection, transaction, entry,
            new("Skipped", "User chose to continue without this Automation."), DateTime.UtcNow, cancellationToken);
        await using (var receipt = connection.CreateCommand())
        {
            receipt.Transaction = transaction;
            receipt.CommandText = """
                INSERT INTO BoardComments (Id, CardId, AuthorKind, AuthorLabel, AuthorCli, SessionId, Body, CreatedUTC, Kind)
                VALUES ($id, $card, $kind, $label, $cli, $session, $body, $created, 'comment');
                """;
            receipt.Parameters.AddWithValue("$id", NewId("cm"));
            receipt.Parameters.AddWithValue("$card", card.Id);
            receipt.Parameters.AddWithValue("$kind", author.Kind);
            receipt.Parameters.AddWithValue("$label", author.Label);
            receipt.Parameters.AddWithValue("$cli", (object?)author.Cli ?? DBNull.Value);
            receipt.Parameters.AddWithValue("$session", (object?)author.SessionId ?? DBNull.Value);
            receipt.Parameters.AddWithValue("$body", comment);
            receipt.Parameters.AddWithValue("$created", ToDb(DateTime.UtcNow));
            await receipt.ExecuteNonQueryAsync(cancellationToken);
        }
        await TouchCardAsync(connection, transaction, card.Id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
