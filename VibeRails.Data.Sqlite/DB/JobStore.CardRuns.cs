using System.Text.Json;
using VibeRails.DTOs;
using VibeRails.Services.Board;

namespace VibeRails.DB;

public sealed partial class JobStore
{
    public async Task<string?> EnqueueBoardCardRunAsync(string projectPath, long jobId, string cardKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(cardKey) || cardKey.Contains(':'))
            throw new ArgumentException("A card key is required.", nameof(cardKey));
        var reviewLaunch = await PrepareReviewAsync(jobId, cardKey, cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var triggerKey = $"{JobBoardContext.ManualPrefix}{cardKey}:{Guid.NewGuid():N}";
        if (_boards is not null && await _boards.FindCardAsync(projectPath, cardKey, cancellationToken) is { } card)
        {
            var failed = (await _boards.GetLaneAutomationStatusesAsync(projectPath, card.Id, cancellationToken))
                .SingleOrDefault(e => e.IsCurrent && e.JobId == jobId && !e.RequiresVerdict
                    && e.StepStatus is BoardStepStatus.Failed or BoardStepStatus.Fixing or BoardStepStatus.Cancelled
                        or BoardStepStatus.TimedOut or BoardStepStatus.Interrupted);
            if (failed is not null)
                triggerKey = $"{JobBoardContext.ManualPrefix}{cardKey}:lane:{failed.ColumnId}:{failed.EventKey}:{Guid.NewGuid():N}";
        }
        var runId = await InsertRunAsync(connection, transaction, jobId, JobTriggerKind.Manual,
            triggerKey, requireEnabled: true,
            cancellationToken, expectedProjectPath: NormalizeProjectPath(projectPath), reviewLaunch: reviewLaunch);
        await transaction.CommitAsync(cancellationToken);
        return runId;
    }

    public async Task<IReadOnlyList<JobRunRecord>> GetBoardCardRunsAsync(string projectPath, string cardKey,
        CancellationToken cancellationToken = default, IReadOnlyList<string>? linkedRecordingIds = null)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = RunSelectSql + $"""

            WHERE r.DeletedUTC IS NULL AND r.ProjectPath = $projectPath{ProjectPathCollation}
              AND r.TriggerKind = $manual AND (instr(r.TriggerKey, $prefix) = 1
                OR instr(r.TriggerKey, $laneRetryPrefix) = 1
                OR (r.Purpose = 'code_review' AND instr(r.TriggerKey, $reviewRetryPrefix) = 1))
              AND NOT EXISTS (SELECT 1 FROM json_each($recordings)
                  WHERE value = COALESCE(r.TerminalSessionId, r.SessionId))
            ORDER BY r.QueuedUTC DESC, r.Id DESC LIMIT 20;
            """;
        command.Parameters.AddWithValue("$projectPath", NormalizeProjectPath(projectPath));
        command.Parameters.AddWithValue("$manual", (int)JobTriggerKind.Manual);
        command.Parameters.AddWithValue("$prefix", $"{JobBoardContext.ManualPrefix}{cardKey}:");
        command.Parameters.AddWithValue("$laneRetryPrefix", $"{JobBoardContext.LaneRetryPrefix}{cardKey}:");
        command.Parameters.AddWithValue("$reviewRetryPrefix", $"{JobBoardContext.ReviewRetryPrefix}{cardKey}:");
        command.Parameters.AddWithValue("$recordings", JsonSerializer.Serialize(
            linkedRecordingIds?.ToList() ?? [], StorageJsonSerializerContext.Default.ListString));
        var runs = new List<JobRunRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) runs.Add(ReadRun(reader));
        return runs;
    }
}
