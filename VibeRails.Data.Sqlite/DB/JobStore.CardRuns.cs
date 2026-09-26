using System.Text.Json;
using VibeRails.DTOs;

namespace VibeRails.DB;

public sealed partial class JobStore
{
    public async Task<string?> EnqueueBoardCardRunAsync(string projectPath, long jobId, string cardKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(cardKey) || cardKey.Contains(':'))
            throw new ArgumentException("A card key is required.", nameof(cardKey));
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var runId = await InsertRunAsync(connection, transaction, jobId, JobTriggerKind.Manual,
            $"{JobBoardContext.ManualPrefix}{cardKey}:{Guid.NewGuid():N}", requireEnabled: true,
            cancellationToken, expectedProjectPath: NormalizeProjectPath(projectPath));
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
              AND r.TriggerKind = $manual AND instr(r.TriggerKey, $prefix) = 1
              AND NOT EXISTS (SELECT 1 FROM json_each($recordings)
                  WHERE value = COALESCE(r.TerminalSessionId, r.SessionId))
            ORDER BY r.QueuedUTC DESC, r.Id DESC LIMIT 20;
            """;
        command.Parameters.AddWithValue("$projectPath", NormalizeProjectPath(projectPath));
        command.Parameters.AddWithValue("$manual", (int)JobTriggerKind.Manual);
        command.Parameters.AddWithValue("$prefix", $"{JobBoardContext.ManualPrefix}{cardKey}:");
        command.Parameters.AddWithValue("$recordings", JsonSerializer.Serialize(
            linkedRecordingIds?.ToList() ?? [], StorageJsonSerializerContext.Default.ListString));
        var runs = new List<JobRunRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) runs.Add(ReadRun(reader));
        return runs;
    }
}
