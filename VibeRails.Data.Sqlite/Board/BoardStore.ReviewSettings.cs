using System.Text.Json;
using VibeRails.DTOs;

namespace VibeRails.Services.Board;

public sealed partial class BoardStore
{
    public async Task<bool> IsNonCodingSessionAsync(string projectPath, string sessionId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT EXISTS (
                SELECT 1 FROM {AllSessionsSql} s JOIN BoardCards c ON c.Id = s.CardId
                WHERE c.ProjectPath = $project{ProjectPathCollation} AND s.SessionId = $session
                  AND s.Origin IN ('chat', 'planning', 'code_review')
                UNION ALL
                SELECT 1 FROM BoardContextSamples s JOIN BoardCards c ON c.Id = s.CardId
                WHERE c.ProjectPath = $project{ProjectPathCollation} AND s.SessionId = $session
                  AND s.Intent IN ('chat', 'planning', 'code_review'));
            """;
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$project", NormalizeProjectPath(projectPath));
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) != 0;
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
