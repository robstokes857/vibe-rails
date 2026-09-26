using System.Text.Json;
using VibeRails.DTOs;

namespace VibeRails.Services.Board;

public sealed partial class BoardStore
{
    public async Task<IReadOnlyList<BoardCardActivityRecord>> GetCardActivityAsync(string projectPath, string boardId,
        IReadOnlyList<string> cardIds, IReadOnlyList<string> liveSessionIds, CancellationToken cancellationToken = default)
    {
        if (cardIds.Count == 0) return [];
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        // Select only requested cards and currently live links. Large descriptions and ended
        // session histories never enter this result, including cards in partially loaded lanes.
        command.CommandText = $"""
            SELECT c.Id, s.SessionId, s.Origin
            FROM BoardCards c
            JOIN BoardColumns lane ON lane.Id = c.ColumnId AND lane.ProjectPath = c.ProjectPath
            JOIN Boards b ON b.Id = lane.BoardId AND b.ProjectPath = c.ProjectPath
            LEFT JOIN (
                SELECT CardId, SessionId, Origin, CreatedUTC FROM BoardCardSessions
                WHERE CardId IN (SELECT value FROM json_each($cards))
                  AND SessionId IN (SELECT value FROM json_each($live))
                UNION ALL
                SELECT a.CardId, a.SessionId, a.Origin, a.CreatedUTC FROM BoardAdditionalCardSessions a
                WHERE a.CardId IN (SELECT value FROM json_each($cards))
                  AND a.SessionId IN (SELECT value FROM json_each($live))
                  AND NOT EXISTS (SELECT 1 FROM BoardCardSessions p
                      WHERE p.CardId = a.CardId AND p.SessionId = a.SessionId)
            ) s ON s.CardId = c.Id
            WHERE c.ProjectPath = $project{ProjectPathCollation} AND b.Id = $board
              AND c.Id IN (SELECT value FROM json_each($cards))
            ORDER BY c.Id, s.CreatedUTC, s.SessionId;
            """;
        command.Parameters.AddWithValue("$project", NormalizeProjectPath(projectPath));
        command.Parameters.AddWithValue("$board", boardId);
        command.Parameters.AddWithValue("$cards", JsonSerializer.Serialize(cardIds.ToList(), StorageJsonSerializerContext.Default.ListString));
        command.Parameters.AddWithValue("$live", JsonSerializer.Serialize(liveSessionIds.ToList(), StorageJsonSerializerContext.Default.ListString));
        var result = new List<BoardCardActivityRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2)));
        return result;
    }
}
