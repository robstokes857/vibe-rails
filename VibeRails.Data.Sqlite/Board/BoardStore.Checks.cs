using System.Text.Json;
using VibeRails.DTOs;

namespace VibeRails.Services.Board;

public sealed partial class BoardStore
{
    private const string ChecksSchemaSql = """
        CREATE TABLE IF NOT EXISTS BoardChecks (
            Id TEXT PRIMARY KEY,
            CardId TEXT NOT NULL REFERENCES BoardCards(Id) ON DELETE CASCADE,
            RunId TEXT NOT NULL,
            ActionId TEXT NOT NULL,
            StartedUTC TEXT NOT NULL,
            EndedUTC TEXT NULL,
            SummaryJson TEXT NOT NULL,
            ResultJson TEXT NULL,
            UNIQUE(CardId, RunId, ActionId)
        );
        CREATE INDEX IF NOT EXISTS IX_BoardChecks_Card ON BoardChecks(CardId, StartedUTC DESC);
        """;

    public async Task<bool> SaveCheckAsync(string projectPath, BoardCheckRecord check, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO BoardChecks (Id, CardId, RunId, ActionId, StartedUTC, EndedUTC, SummaryJson, ResultJson)
            SELECT $id, c.Id, $run, $action, $started, $ended, $summary, $result
            FROM BoardCards c WHERE c.Id = $card AND c.ProjectPath = $project{ProjectPathCollation} AND c.DeletedUTC IS NULL
            ON CONFLICT(Id) DO UPDATE SET EndedUTC = excluded.EndedUTC,
                SummaryJson = excluded.SummaryJson, ResultJson = excluded.ResultJson
            WHERE BoardChecks.CardId = excluded.CardId AND BoardChecks.RunId = excluded.RunId
              AND BoardChecks.ActionId = excluded.ActionId AND BoardChecks.EndedUTC IS NULL;
            """;
        command.Parameters.AddWithValue("$id", check.Id);
        command.Parameters.AddWithValue("$card", check.CardId);
        command.Parameters.AddWithValue("$project", NormalizeProjectPath(projectPath));
        command.Parameters.AddWithValue("$run", check.RunId);
        command.Parameters.AddWithValue("$action", check.ActionId);
        command.Parameters.AddWithValue("$started", ToDb(check.StartedUtc));
        command.Parameters.AddWithValue("$ended", check.EndedUtc is {} end ? ToDb(end) : DBNull.Value);
        command.Parameters.AddWithValue("$summary", JsonSerializer.Serialize(check with { ResultJson = null }, StorageJsonSerializerContext.Default.BoardCheckRecord));
        command.Parameters.AddWithValue("$result", (object?)check.ResultJson ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<IReadOnlyList<BoardCheckRecord>> GetChecksAsync(string projectPath, string cardId, int offset = 0, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT e.SummaryJson FROM BoardChecks e JOIN BoardCards c ON c.Id = e.CardId
            WHERE c.Id = $card AND c.ProjectPath = $project{ProjectPathCollation} AND c.DeletedUTC IS NULL
            ORDER BY e.StartedUTC DESC, e.rowid DESC LIMIT 50 OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$card", cardId);
        command.Parameters.AddWithValue("$project", NormalizeProjectPath(projectPath));
        command.Parameters.AddWithValue("$offset", offset);
        var result = new List<BoardCheckRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(JsonSerializer.Deserialize(reader.GetString(0), StorageJsonSerializerContext.Default.BoardCheckRecord)!);
        return result;
    }

    public async Task<BoardCheckRecord?> GetCheckAsync(string projectPath, string cardId, string checkId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT e.SummaryJson, e.ResultJson FROM BoardChecks e JOIN BoardCards c ON c.Id = e.CardId
            WHERE e.Id = $id AND c.Id = $card AND c.ProjectPath = $project{ProjectPathCollation} AND c.DeletedUTC IS NULL;
            """;
        command.Parameters.AddWithValue("$id", checkId);
        command.Parameters.AddWithValue("$card", cardId);
        command.Parameters.AddWithValue("$project", NormalizeProjectPath(projectPath));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return JsonSerializer.Deserialize(reader.GetString(0), StorageJsonSerializerContext.Default.BoardCheckRecord)! with
        { ResultJson = reader.IsDBNull(1) ? null : reader.GetString(1) };
    }

    public async Task<IReadOnlyList<BoardCheckRecord>> GetLatestChecksAsync(string projectPath, string cardId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            WITH ranked AS (
                SELECT e.SummaryJson, row_number() OVER (
                    PARTITION BY json_extract(e.SummaryJson, '$.tool') ORDER BY e.StartedUTC DESC, e.rowid DESC) AS rn
                FROM BoardChecks e JOIN BoardCards c ON c.Id = e.CardId
                WHERE c.Id = $card AND c.ProjectPath = $project{ProjectPathCollation} AND c.DeletedUTC IS NULL
            ) SELECT SummaryJson FROM ranked WHERE rn = 1;
            """;
        command.Parameters.AddWithValue("$card", cardId);
        command.Parameters.AddWithValue("$project", NormalizeProjectPath(projectPath));
        var result = new List<BoardCheckRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(JsonSerializer.Deserialize(reader.GetString(0), StorageJsonSerializerContext.Default.BoardCheckRecord)!);
        return result;
    }
}
