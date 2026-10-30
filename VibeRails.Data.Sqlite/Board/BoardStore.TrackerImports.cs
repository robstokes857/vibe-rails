using Microsoft.Data.Sqlite;

namespace VibeRails.Services.Board;

public sealed partial class BoardStore
{
    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<int, bool>> GetAdoImportStatesAsync(string projectPath, string connectionId,
        IReadOnlyList<int> workItemIds, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<int, bool>();
        foreach (var batch in workItemIds.Distinct().Chunk(500))
        {
            await using var db = await OpenAsync(cancellationToken);
            await using var command = ImportStatesCommand(db, "BoardAdoLinks", "ConnectionId", "WorkItemId",
                projectPath, connectionId, batch.Select(id => (object)id).ToArray());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) result.Add(reader.GetInt32(0), reader.GetBoolean(1));
        }
        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, bool>> GetJiraImportStatesAsync(string projectPath, string siteId,
        IReadOnlyList<string> issueIds, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var batch in issueIds.Distinct(StringComparer.Ordinal).Chunk(500))
        {
            await using var db = await OpenAsync(cancellationToken);
            await using var command = ImportStatesCommand(db, "BoardJiraLinks", "SiteId", "IssueId",
                projectPath, siteId, batch.Cast<object>().ToArray());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) result.Add(reader.GetString(0), reader.GetBoolean(1));
        }
        return result;
    }

    // Identifiers are private constants from the two callers; every external value is a parameter.
    private static SqliteCommand ImportStatesCommand(SqliteConnection db, string table, string connectionColumn,
        string itemColumn, string projectPath, string connectionId, object[] ids)
    {
        var command = db.CreateCommand();
        var parameters = ids.Select((_, index) => "$id" + index).ToArray();
        command.CommandText = $"""
            SELECT link.{itemColumn}, card.Id IS NOT NULL AND card.DeletedUTC IS NULL
            FROM {table} link LEFT JOIN BoardCards card ON card.Id = link.CardId
            WHERE link.{connectionColumn} = $connection
              AND (card.ProjectPath = $project{ProjectPathCollation} OR card.Id IS NULL)
              AND link.{itemColumn} IN ({string.Join(",", parameters)});
            """;
        command.Parameters.AddWithValue("$connection", connectionId);
        command.Parameters.AddWithValue("$project", NormalizeProjectPath(projectPath));
        for (var index = 0; index < ids.Length; index++) command.Parameters.AddWithValue(parameters[index], ids[index]);
        return command;
    }
}
