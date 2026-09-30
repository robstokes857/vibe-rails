using VibeRails.Data.Sqlite;
using VibeRails.DTOs;

namespace VibeRails.Services.Board;

public sealed partial class BoardStore
{
    private const string AgentCompletionSchemaSql = """
        CREATE TABLE IF NOT EXISTS BoardAgentCompletions (
            ProjectPath TEXT NOT NULL,
            SessionId TEXT NOT NULL,
            Outcome TEXT NOT NULL,
            Summary TEXT NOT NULL,
            CompletedUTC TEXT NOT NULL,
            PRIMARY KEY (SessionId)
        );
        """;

    public async Task<IReadOnlyList<BoardAgentSessionStatus>> GetAgentSessionsAsync(string projectPath, string cardId,
        string? sessionId = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT s.SessionId, s.CardId, s.TabId, s.Selection, s.Cli, s.DisplayName, s.Origin, s.CreatedUTC,
                   a.Outcome, a.Summary, a.CompletedUTC, substr(u.Body, 1, 600), u.CreatedUTC
            FROM {AllSessionsSql} s JOIN BoardCards c ON c.Id = s.CardId
            LEFT JOIN BoardAgentCompletions a ON a.SessionId = s.SessionId AND a.ProjectPath = c.ProjectPath{ProjectPathCollation}
            LEFT JOIN BoardComments u ON u.Id = (
                SELECT Id FROM BoardComments WHERE CardId = s.CardId AND SessionId = s.SessionId
                  AND Kind IN ('comment', 'note') AND DiscussionHidden = 0
                  AND NOT EXISTS (SELECT 1 FROM BoardDeletedComments d WHERE d.CommentId = BoardComments.Id) ORDER BY CreatedUTC DESC, Id DESC LIMIT 1)
            WHERE c.Id = $card AND c.ProjectPath = $project{ProjectPathCollation} AND c.DeletedUTC IS NULL
              AND ($session IS NULL OR s.SessionId = $session)
            ORDER BY s.CreatedUTC DESC, s.SessionId LIMIT 10;
            """;
        command.Parameters.AddWithValue("$project", NormalizeProjectPath(projectPath));
        command.Parameters.AddWithValue("$card", cardId);
        command.Parameters.AddWithValue("$session", (object?)sessionId ?? DBNull.Value);
        var result = new List<BoardAgentSessionStatus>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var session = ReadSession(reader);
            result.Add(new(session, reader.IsDBNull(8) ? null : new(session.SessionId,
                reader.GetString(8), reader.GetString(9), ParseDb(reader.GetString(10))),
                reader.IsDBNull(11) ? null : reader.GetString(11), reader.IsDBNull(12) ? null : ParseDb(reader.GetString(12))));
        }
        return result;
    }

    public async Task<BoardAgentCompletion?> CompleteAgentAsync(string projectPath, string cardId, string sessionId,
        string outcome, string summary, CancellationToken cancellationToken = default)
    {
        var project = NormalizeProjectPath(projectPath);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        // Membership and insertion share one statement. A removed/foreign link cannot authorize a report.
        command.CommandText = $"""
            INSERT INTO BoardAgentCompletions (ProjectPath, SessionId, Outcome, Summary, CompletedUTC)
            SELECT $project, $session, $outcome, $summary, $now
            WHERE EXISTS (SELECT 1 FROM {AllSessionsSql} s JOIN BoardCards c ON c.Id = s.CardId
                WHERE c.Id = $card AND c.ProjectPath = $project{ProjectPathCollation}
                  AND c.DeletedUTC IS NULL AND s.SessionId = $session)
            ON CONFLICT (SessionId) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$project", project);
        command.Parameters.AddWithValue("$card", cardId);
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$outcome", outcome);
        command.Parameters.AddWithValue("$summary", summary);
        command.Parameters.AddWithValue("$now", ToDb(DateTime.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return (await GetAgentCompletionsAsync(project, cardId, cancellationToken)).FirstOrDefault(c => c.SessionId == sessionId);
    }

    public async Task<IReadOnlyList<BoardAgentCompletion>> GetAgentCompletionsAsync(string projectPath, string cardId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT a.SessionId, a.Outcome, a.Summary, a.CompletedUTC FROM BoardAgentCompletions a
            WHERE a.ProjectPath = $project{ProjectPathCollation}
              AND EXISTS (SELECT 1 FROM {AllSessionsSql} s JOIN BoardCards c ON c.Id = s.CardId
                  WHERE c.Id = $card AND c.ProjectPath = $project{ProjectPathCollation}
                    AND c.DeletedUTC IS NULL AND s.SessionId = a.SessionId)
            ORDER BY a.CompletedUTC DESC, a.SessionId;
            """;
        command.Parameters.AddWithValue("$project", NormalizeProjectPath(projectPath));
        command.Parameters.AddWithValue("$card", cardId);
        var result = new List<BoardAgentCompletion>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), ParseDb(reader.GetString(3))));
        return result;
    }

    public async Task<IReadOnlyList<BoardAgentRun>> GetAgentRunsAsync(string projectPath, string cardId,
        CancellationToken cancellationToken = default)
    {
        var card = await FindCardAsync(projectPath, cardId, cancellationToken);
        if (card is null) return [];
        await using var state = await OpenStateAsync(cancellationToken);
        if (!await TableExistsAsync(state, "JobRuns", cancellationToken)) return [];
        var terminal = SqliteSchema.HasColumn(state, null, "JobRuns", "TerminalSessionId") ? "TerminalSessionId" : "NULL";
        await using var command = state.CreateCommand();
        command.CommandText = $"""
            SELECT Id, JobName, Status, QueuedUTC, COALESCE({terminal}, SessionId), SessionId, ErrorMessage
            FROM JobRuns WHERE ProjectPath = $project{ProjectPathCollation} AND DeletedUTC IS NULL
              AND ((TriggerKind = $lane AND instr(TriggerKey, $lanePrefix) = 1)
                OR (TriggerKind = $manual AND instr(TriggerKey, $manualPrefix) = 1))
            ORDER BY QueuedUTC DESC, Id DESC LIMIT 20;
            """;
        command.Parameters.AddWithValue("$project", NormalizeProjectPath(projectPath));
        command.Parameters.AddWithValue("$lane", (int)JobTriggerKind.BoardLane);
        command.Parameters.AddWithValue("$manual", (int)JobTriggerKind.Manual);
        command.Parameters.AddWithValue("$lanePrefix", $"board-lane:{card.Key}:");
        command.Parameters.AddWithValue("$manualPrefix", $"{JobBoardContext.ManualPrefix}{card.Key}:");
        var result = new List<BoardAgentRun>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new(reader.GetString(0), reader.GetString(1), (JobRunStatus)reader.GetInt32(2),
                ParseDb(reader.GetString(3)), reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6)));
        return result;
    }
}
