using VibeRails.DTOs;

namespace VibeRails.Services.Board;

public sealed partial class BoardStore
{
    public async Task<BoardLaneStepRun?> FindLaneStepRunAsync(string projectPath, string cardId, string sessionId,
        CancellationToken cancellationToken = default)
    {
        var card = await FindCardAsync(projectPath, cardId, cancellationToken);
        if (card is null || !(await GetAgentSessionsAsync(projectPath, cardId, sessionId, cancellationToken)).Any()) return null;
        await using var state = await OpenStateAsync(cancellationToken);
        if (!await _stateFeatures.HasTableAsync(state, "JobRuns", cancellationToken)
            || !await _stateFeatures.HasTableAsync(state, "JobRunActions", cancellationToken)) return null;
        await using var query = state.CreateCommand();
        query.CommandText = $"""
            SELECT r.Id, r.JobId, r.TriggerKey, r.QueuedUTC, r.Purpose, r.Status, r.TriggerKind
            FROM JobRuns r WHERE r.ProjectPath = $project{ProjectPathCollation}
              AND (r.SessionId = $session OR EXISTS (SELECT 1 FROM JobRunActions a WHERE a.RunId = r.Id AND a.Kind = 0 AND a.SessionId = $session))
            ORDER BY r.QueuedUTC DESC LIMIT 1;
            """;
        query.Parameters.AddWithValue("$project", NormalizeProjectPath(projectPath));
        query.Parameters.AddWithValue("$session", sessionId);
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)
            || JobBoardContext.GetCardKey((JobTriggerKind)reader.GetInt32(6), reader.GetString(2)) != card.Key) return null;
        return new(reader.GetString(0), reader.GetInt64(1), reader.GetString(2), ParseDb(reader.GetString(3)),
            reader.GetString(4), (JobRunStatus)reader.GetInt32(5) is JobRunStatus.Queued or JobRunStatus.Running);
    }

    public async Task<bool> ReportLaneStepAsync(string projectPath, string cardId, BoardLaneStepReport report,
        BoardAuthor author, CancellationToken cancellationToken = default)
    {
        if (report.Status is not (BoardStepStatus.Reviewing or BoardStepStatus.Fixing or BoardStepStatus.Passed or BoardStepStatus.Failed or BoardStepStatus.Skipped)
            || report.Status == BoardStepStatus.Skipped && author.Kind != BoardAuthor.User().Kind
            || string.IsNullOrWhiteSpace(report.Summary) || report.Summary.Length > 4000)
            throw new BoardValidationException("Choose a valid step status and a summary of 1–4,000 characters.");
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var card = await ReadCardAsync(connection, transaction, NormalizeProjectPath(projectPath), cardId, cancellationToken);
        if (card is null) return false;
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = $"""
            SELECT EXISTS (SELECT 1 FROM (
                {PendingLaneEntriesSql}
                UNION ALL SELECT CardId, ColumnId, JobId, EventKey, DueUnixMs FROM BoardLaneAutomationDispatch
            ) e WHERE e.CardId = $card AND e.ColumnId = $lane AND e.JobId = $job AND e.EventKey = $event
              AND (EXISTS (SELECT 1 FROM BoardLaneWorkflowSteps s JOIN BoardLaneWorkflows w ON w.Id = s.WorkflowId
                    WHERE s.EventKey = e.EventKey AND s.JobId = e.JobId AND w.Current = 1)
                OR (NOT EXISTS (SELECT 1 FROM BoardLaneWorkflowSteps s WHERE s.EventKey = e.EventKey AND s.JobId = e.JobId)
                    AND NOT EXISTS (SELECT 1 FROM BoardLaneWorkflows w WHERE w.CardId = e.CardId AND w.CreatedUnixMs > e.DueUnixMs - 60000)))
              AND ($session IS NULL OR EXISTS (SELECT 1 FROM {AllSessionsSql} cs WHERE cs.CardId = e.CardId AND cs.SessionId = $session)));
            """;
        query.Parameters.AddWithValue("$card", card.Id);
        query.Parameters.AddWithValue("$lane", card.ColumnId);
        query.Parameters.AddWithValue("$job", report.JobId);
        query.Parameters.AddWithValue("$event", report.EventKey);
        query.Parameters.AddWithValue("$session", (object?)author.SessionId ?? DBNull.Value);
        if (Convert.ToInt64(await query.ExecuteScalarAsync(cancellationToken)) == 0) return false;
        query.CommandText = "SELECT Status, RunId FROM BoardLaneStepReports WHERE EventKey = $event AND JobId = $job ORDER BY Id DESC LIMIT 1;";
        string? previous = null, previousRun = null;
        await using (var reader = await query.ExecuteReaderAsync(cancellationToken))
            if (await reader.ReadAsync(cancellationToken))
            {
                previous = reader.GetString(0);
                previousRun = reader.IsDBNull(1) ? null : reader.GetString(1);
            }
        if (previous == BoardStepStatus.Skipped) return report.Status == BoardStepStatus.Skipped;
        if (previous == BoardStepStatus.Passed && report.Status != BoardStepStatus.Skipped)
        {
            // A reported pass is final only if its run succeeds. Preserve the decision while
            // that run finishes, but allow a fresh attempt after a crash or cancellation.
            await using var state = await OpenStateAsync(cancellationToken);
            if (previousRun is null || !await _stateFeatures.HasTableAsync(state, "JobRuns", cancellationToken)) return false;
            await using var run = state.CreateCommand();
            run.CommandText = $"SELECT Status FROM JobRuns WHERE Id = $run AND ProjectPath = $project{ProjectPathCollation};";
            run.Parameters.AddWithValue("$run", previousRun);
            run.Parameters.AddWithValue("$project", NormalizeProjectPath(projectPath));
            if (await run.ExecuteScalarAsync(cancellationToken) is not long value) return false;
            if ((JobRunStatus)value is JobRunStatus.Queued or JobRunStatus.Running or JobRunStatus.Succeeded)
                return previous == report.Status && previousRun == report.RunId;
        }
        query.CommandText = """
            INSERT INTO BoardLaneStepReports (EventKey, JobId, Status, Summary, RunId, ReviewId, SessionId, CreatedUTC)
            VALUES ($event, $job, $status, $summary, $run, $review, $session, $now);
            INSERT INTO BoardComments (Id, CardId, AuthorKind, AuthorLabel, AuthorCli, SessionId, Body, CreatedUTC, Kind)
            VALUES ($id, $card, $kind, $label, $cli, $session, $body, $now, 'comment');
            """;
        query.Parameters.AddWithValue("$status", report.Status);
        query.Parameters.AddWithValue("$summary", report.Summary.Trim());
        query.Parameters.AddWithValue("$run", (object?)report.RunId ?? DBNull.Value);
        query.Parameters.AddWithValue("$review", (object?)report.ReviewId ?? DBNull.Value);
        query.Parameters.AddWithValue("$now", ToDb(DateTime.UtcNow));
        query.Parameters.AddWithValue("$id", NewId("cm"));
        query.Parameters.AddWithValue("$kind", author.Kind);
        query.Parameters.AddWithValue("$label", author.Label);
        query.Parameters.AddWithValue("$cli", (object?)author.Cli ?? DBNull.Value);
        query.Parameters.AddWithValue("$body", $"Automation step #{report.JobId}: {report.Status}. {report.Summary.Trim()}");
        await query.ExecuteNonQueryAsync(cancellationToken);
        await TouchCardAsync(connection, transaction, card.Id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
