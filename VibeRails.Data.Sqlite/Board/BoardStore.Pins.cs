using System.Data;

namespace VibeRails.Services.Board;

public sealed partial class BoardStore
{
    /// <inheritdoc />
    public async Task<bool> SetCardPinnedAsync(string projectPath, string cardId, bool pinned, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var card = await ReadCardAsync(connection, transaction, NormalizeProjectPath(projectPath), cardId, cancellationToken);
        if (card is null) return false;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE BoardCards SET Pinned = $pinned WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", card.Id);
        command.Parameters.AddWithValue("$pinned", pinned ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
