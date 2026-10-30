using Microsoft.Data.Sqlite;

namespace VibeRails.Services.Board;

public sealed partial class BoardStore
{
    /// <inheritdoc />
    public async Task<bool> IsSessionVisibleInProjectAsync(string projectPath, string sessionId, CancellationToken cancellationToken = default)
    {
        await using var state = await OpenStateAsync(cancellationToken);
        if (!await _stateFeatures.HasTableAsync(state, "Sessions", cancellationToken)
            || !_stateFeatures.HasColumn(state, "Sessions", "WorkingDirectory")) return false;

        await using var query = state.CreateCommand();
        query.CommandText = "SELECT WorkingDirectory FROM Sessions WHERE Id = $session LIMIT 1;";
        query.Parameters.AddWithValue("$session", sessionId);
        if (await query.ExecuteScalarAsync(cancellationToken) is not string directory || string.IsNullOrWhiteSpace(directory))
            return false;
        try
        {
            return string.Equals(NormalizeProjectPath(directory), NormalizeProjectPath(projectPath), BoardPaths.ProjectPathComparison);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> ConfirmSessionLaunchOriginAsync(string projectPath, string cardId, string sessionId,
        string origin, CancellationToken cancellationToken = default)
    {
        if (origin is not (BoardSessionRecord.LaunchOrigin or BoardSessionRecord.ManagerOrigin
            or BoardSessionRecord.WorkerOrigin or BoardSessionRecord.PlanningWorkerOrigin or "chat" or "planning" or "code_review"))
            throw new BoardValidationException("Choose an explicit launch origin.");
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(System.Data.IsolationLevel.Serializable);
        var card = await ReadCardForWriteAsync(connection, transaction, NormalizeProjectPath(projectPath), cardId, cancellationToken);
        if (card is null) return false;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // Only the current launch handshake may refine an attachment's unspecified provenance.
        // Keep timestamps, names and all other link fields; never relabel another explicit launch.
        command.CommandText = """
            UPDATE BoardCardSessions SET Origin = $origin
            WHERE CardId = $card AND SessionId = $session AND Origin IN ('mcp', 'manual', $origin);
            UPDATE BoardAdditionalCardSessions SET Origin = $origin
            WHERE CardId = $card AND SessionId = $session AND Origin IN ('mcp', 'manual', $origin);
            """;
        command.Parameters.AddWithValue("$card", card.Id);
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$origin", origin);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0) return false;
        await TouchCardAsync(connection, transaction, card.Id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<BoardSessionCard>> GetSessionCardsAsync(IReadOnlyList<string> sessionIds,
        CancellationToken cancellationToken = default)
    {
        var result = new List<BoardSessionCard>();
        if (sessionIds.Count == 0) return result;
        await using var connection = await OpenAsync(cancellationToken);
        foreach (var batch in sessionIds.Distinct(StringComparer.Ordinal).Chunk(100))
        {
            await using var command = connection.CreateCommand();
            var parameters = string.Join(",", batch.Select((_, index) => $"$s{index}"));
            command.CommandText = $"""
                SELECT s.SessionId, c.Id, {CardKeySql}, c.Title, {CardDisplayIdSql}, c.ProjectPath
                FROM {AllSessionsSql} s JOIN BoardCards c ON c.Id = s.CardId
                {CardPrefixJoinSql}
                WHERE s.SessionId IN ({parameters}) AND c.DeletedUTC IS NULL
                ORDER BY s.SessionId, s.LinkOrder, s.CreatedUTC, s.CardId;
                """;
            for (var i = 0; i < batch.Length; i++) command.Parameters.AddWithValue($"$s{i}", batch[i]);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                result.Add(new BoardSessionCard(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5)));
        }
        return result;
    }

    public Task<IReadOnlySet<string>> GetAutomationSessionIdsAsync(string projectPath,
        IReadOnlyList<string> sessionIds, CancellationToken cancellationToken = default)
        => GetAutomationSessionIdsCoreAsync(projectPath, sessionIds, false, cancellationToken);

    public Task<IReadOnlySet<string>> GetReviewSessionIdsAsync(string projectPath,
        IReadOnlyList<string> sessionIds, CancellationToken cancellationToken = default)
        => GetAutomationSessionIdsCoreAsync(projectPath, sessionIds, true, cancellationToken);

    private async Task<IReadOnlySet<string>> GetAutomationSessionIdsCoreAsync(string projectPath,
        IReadOnlyList<string> sessionIds, bool reviewsOnly, CancellationToken cancellationToken)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (sessionIds.Count == 0) return result;
        // Direct review launches also retain immutable purpose if an early tool call linked the session first.
        await using (var board = await OpenAsync(cancellationToken))
        {
            foreach (var batch in sessionIds.Distinct(StringComparer.Ordinal).Chunk(100))
            {
                await using var query = board.CreateCommand();
                var parameters = string.Join(",", batch.Select((_, i) => $"$s{i}"));
                query.CommandText = $"""
                    SELECT json_extract(r.RecordJson, '$.sessionId') FROM BoardReviews r
                    JOIN BoardCards c ON c.Id = r.CardId
                    WHERE c.ProjectPath = $project{ProjectPathCollation} AND c.DeletedUTC IS NULL
                      AND json_extract(r.RecordJson, '$.sessionId') IN ({parameters});
                    """;
                query.Parameters.AddWithValue("$project", NormalizeProjectPath(projectPath));
                for (var i = 0; i < batch.Length; i++) query.Parameters.AddWithValue($"$s{i}", batch[i]);
                await using var reader = await query.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken)) result.Add(reader.GetString(0));
            }
        }
        await using var state = await OpenStateAsync(cancellationToken);
        // A Board-only/stdio host need not have initialized Jobs. This is a read, not setup.
        if (!_stateFeatures.HasColumn(state, "JobRuns", "SessionId"))
            return result;
        var purpose = _stateFeatures.HasColumn(state, "JobRuns", "Purpose") ? "Purpose" : "'work'";
        var hasTerminalSession = _stateFeatures.HasColumn(state, "JobRuns", "TerminalSessionId");
        foreach (var batch in sessionIds.Distinct(StringComparer.Ordinal).Chunk(100))
        {
            await using var command = state.CreateCommand();
            var parameters = string.Join(",", batch.Select((_, index) => $"$s{index}"));
            var terminal = hasTerminalSession ? "TerminalSessionId" : "NULL";
            command.CommandText = $"""
                SELECT SessionId, {terminal} FROM JobRuns
                WHERE ProjectPath = $project{ProjectPathCollation}
                  AND ($reviews = 0 OR {purpose} = 'code_review')
                  AND (SessionId IN ({parameters}) OR {terminal} IN ({parameters}));
                """;
            command.Parameters.AddWithValue("$reviews", reviewsOnly ? 1 : 0);
            command.Parameters.AddWithValue("$project", NormalizeProjectPath(projectPath));
            for (var i = 0; i < batch.Length; i++) command.Parameters.AddWithValue($"$s{i}", batch[i]);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                for (var i = 0; i < 2; i++)
                    if (!reader.IsDBNull(i)) result.Add(reader.GetString(i));
        }
        return result;
    }

    private static async Task<HashSet<string>> ReadCommitTargetCardsAsync(SqliteConnection connection,
        SqliteTransaction transaction, string project, string cardId, string? sessionId, CancellationToken cancellationToken)
    {
        var targets = new HashSet<string>(StringComparer.Ordinal) { cardId };
        if (string.IsNullOrWhiteSpace(sessionId))
            return targets;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT s.CardId FROM {AllSessionsSql} s JOIN BoardCards c ON c.Id = s.CardId
            WHERE s.SessionId = $session AND c.ProjectPath = $project{ProjectPathCollation} AND c.DeletedUTC IS NULL;
            """;
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$project", project);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            targets.Add(reader.GetString(0));
        return targets;
    }

    // Older versions still read/write the original table. If one recreates a primary link
    // already present in the additional table, expose that pair only once, preferring primary.
    private const string AllSessionsSql = """
        (SELECT SessionId, CardId, TabId, Selection, Cli, DisplayName, Origin, CreatedUTC, 0 AS LinkOrder
         FROM BoardCardSessions
         UNION ALL
         SELECT a.SessionId, a.CardId, a.TabId, a.Selection, a.Cli, a.DisplayName, a.Origin, a.CreatedUTC, 1 AS LinkOrder
         FROM BoardAdditionalCardSessions a
         WHERE NOT EXISTS (SELECT 1 FROM BoardCardSessions p WHERE p.SessionId = a.SessionId AND p.CardId = a.CardId))
        """;

    private const string AdditionalCardSessionsSchemaSql = """
        CREATE TABLE IF NOT EXISTS BoardAdditionalCardSessions (
            SessionId TEXT NOT NULL,
            CardId TEXT NOT NULL REFERENCES BoardCards(Id) ON DELETE CASCADE,
            TabId TEXT NULL,
            Selection TEXT NOT NULL,
            Cli TEXT NOT NULL,
            DisplayName TEXT NOT NULL,
            Origin TEXT NOT NULL,
            CreatedUTC TEXT NOT NULL,
            PRIMARY KEY (SessionId, CardId)
        );
        CREATE INDEX IF NOT EXISTS IX_BoardAdditionalCardSessions_Card ON BoardAdditionalCardSessions(CardId);
        """;
}
