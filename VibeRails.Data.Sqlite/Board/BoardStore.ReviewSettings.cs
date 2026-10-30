using System.Text.Json;
using VibeRails.DTOs;

namespace VibeRails.Services.Board;

public sealed partial class BoardStore
{
    public async Task<bool> IsNonCodingSessionAsync(string projectPath, string sessionId, CancellationToken cancellationToken = default)
        => (await GetNonCodingSessionIdsAsync(projectPath, [sessionId], cancellationToken)).Contains(sessionId);

    public async Task<IReadOnlySet<string>> GetNonCodingSessionIdsAsync(string projectPath, IReadOnlyList<string> sessionIds, CancellationToken cancellationToken = default)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (sessionIds.Count == 0) return result;
        await using var connection = await OpenAsync(cancellationToken);
        foreach (var batch in sessionIds.Distinct(StringComparer.Ordinal).Chunk(100))
        {
            await using var command = connection.CreateCommand();
            var parameters = string.Join(",", batch.Select((_, i) => $"$s{i}"));
            command.CommandText = $"""
                SELECT s.SessionId FROM {AllSessionsSql} s JOIN BoardCards c ON c.Id = s.CardId
                WHERE c.ProjectPath = $project{ProjectPathCollation} AND s.SessionId IN ({parameters})
                  AND s.Origin IN ('chat', 'planning', 'code_review', 'agent_manager', 'agent_worker_planning')
                UNION
                SELECT s.SessionId FROM BoardContextSamples s JOIN BoardCards c ON c.Id = s.CardId
                WHERE c.ProjectPath = $project{ProjectPathCollation} AND s.SessionId IN ({parameters})
                  AND s.Intent IN ('chat', 'planning', 'code_review', 'manager');
                """;
            for (var i = 0; i < batch.Length; i++) command.Parameters.AddWithValue($"$s{i}", batch[i]);
            command.Parameters.AddWithValue("$project", NormalizeProjectPath(projectPath));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) result.Add(reader.GetString(0));
        }
        return result;
    }

    private const string ReviewSettingsSchemaSql = """
        CREATE TABLE IF NOT EXISTS BoardReviewSettings (
            CardId TEXT PRIMARY KEY REFERENCES BoardCards(Id) ON DELETE CASCADE,
            SettingsJson TEXT NOT NULL
        );
        """;

    public async Task<BoardReviewSettings?> GetReviewSettingsAsync(string projectPath, string cardId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT s.SettingsJson FROM BoardReviewSettings s JOIN BoardCards c ON c.Id = s.CardId
            WHERE c.Id = $card AND c.ProjectPath = $project{ProjectPathCollation} AND c.DeletedUTC IS NULL;
            """;
        command.Parameters.AddWithValue("$card", cardId);
        command.Parameters.AddWithValue("$project", NormalizeProjectPath(projectPath));
        return await command.ExecuteScalarAsync(cancellationToken) is string json
            ? JsonSerializer.Deserialize(json, StorageJsonSerializerContext.Default.BoardReviewSettings) : null;
    }

    public async Task<bool> SaveReviewSettingsAsync(string projectPath, string cardId, BoardReviewSettings settings, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await RequireWritableCardAsync(connection, null, cardId, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO BoardReviewSettings (CardId, SettingsJson)
            SELECT Id, $json FROM BoardCards WHERE Id = $card AND ProjectPath = $project{ProjectPathCollation} AND DeletedUTC IS NULL
            ON CONFLICT(CardId) DO UPDATE SET SettingsJson = excluded.SettingsJson;
            """;
        command.Parameters.AddWithValue("$card", cardId);
        command.Parameters.AddWithValue("$project", NormalizeProjectPath(projectPath));
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(settings, StorageJsonSerializerContext.Default.BoardReviewSettings));
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }
}
