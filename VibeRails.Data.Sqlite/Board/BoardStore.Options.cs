using System.Text.Json;
using Microsoft.Data.Sqlite;
using VibeRails.DTOs;
using VibeRails.Data.Sqlite;

namespace VibeRails.Services.Board;

public sealed partial class BoardStore
{
    private static async Task WriteBaseLlmOptionsAsync(SqliteConnection connection, SqliteTransaction transaction,
        string cardId, BaseLlmOptions? options, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = options is null
            ? "DELETE FROM BoardCardOptions WHERE CardId = $card;"
            : "INSERT INTO BoardCardOptions (CardId, OptionsJson) VALUES ($card, $json) ON CONFLICT(CardId) DO UPDATE SET OptionsJson = excluded.OptionsJson;";
        command.Parameters.AddWithValue("$card", cardId);
        if (options is not null)
            command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(options, StorageJsonSerializerContext.Default.BaseLlmOptions));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

}
