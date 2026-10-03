using System.Text.Json;
using VibeRails.DTOs;

namespace VibeRails.Services.Board;

public sealed partial class BoardStore
{
    public async Task<IReadOnlyList<string>> GetWaitingAutomationCardIdsAsync(string projectPath,
        IReadOnlyList<string> cardIds, CancellationToken cancellationToken = default)
    {
        if (cardIds.Count == 0) return [];
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        await using (var connection = await OpenAsync(cancellationToken))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT c.Id, 'board-lane:' || {CardKeySql} || ':' || p.ColumnId || ':' || p.EventKey
                FROM ({PendingLaneEntriesSql}) p JOIN BoardCards c ON c.Id = p.CardId
                {CardPrefixJoinSql}
                WHERE c.ProjectPath = $project{ProjectPathCollation} AND c.DeletedUTC IS NULL
                    AND c.ColumnId = p.ColumnId AND c.Id IN (SELECT value FROM json_each($cards));
                """;
            command.Parameters.AddWithValue("$project", NormalizeProjectPath(projectPath));
            command.Parameters.AddWithValue("$cards", JsonSerializer.Serialize(cardIds.ToList(), StorageJsonSerializerContext.Default.ListString));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) entries[reader.GetString(1)] = reader.GetString(0);
        }
        if (entries.Count == 0) return [];
        // A committed run wins even when its separate Board acknowledgment failed.
        await using var state = await OpenStateAsync(cancellationToken);
        if (await _stateFeatures.HasTableAsync(state, "JobRuns", cancellationToken))
        {
            await using var command = state.CreateCommand();
            command.CommandText = $"""
                SELECT TriggerKey FROM JobRuns WHERE ProjectPath = $project{ProjectPathCollation}
                    AND TriggerKind = $kind AND TriggerKey IN (SELECT value FROM json_each($keys));
                """;
            command.Parameters.AddWithValue("$project", NormalizeProjectPath(projectPath));
            command.Parameters.AddWithValue("$kind", (int)JobTriggerKind.BoardLane);
            command.Parameters.AddWithValue("$keys", JsonSerializer.Serialize(entries.Keys.ToList(), StorageJsonSerializerContext.Default.ListString));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) entries.Remove(reader.GetString(0));
        }
        return entries.Values.Distinct(StringComparer.Ordinal).ToList();
    }
}
