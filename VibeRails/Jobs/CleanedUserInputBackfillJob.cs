using VibeRails.DB;
using VibeRails.Services;
using VibeRails.Services.CleanedInput;
using System.Text;

namespace VibeRails.Jobs;

/// <summary>
/// Cleans uncleaned <c>UserInputs</c> rows in sessions that closed >5 min ago.
/// Catches sessions killed before their idle tick could fire (Part 2b).
/// Runs at medium priority every 5 minutes.
/// </summary>
public sealed class CleanedUserInputBackfillJob(
    ILogger<CleanedUserInputBackfillJob> logger,
    ISystemResourceService resources,
    IServiceScopeFactory scopeFactory) : JobBase(logger, resources)
{
    private const int BatchSize = 200;

    protected override TimeSpan Interval => TimeSpan.FromMinutes(3);
    protected override JobPriority Priority => JobPriority.High;

    protected override async Task ExecuteJob(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IRepository>();
        var cleanedService = scope.ServiceProvider.GetRequiredService<ICleanedUserInputService>();

        var uncleanedBatch = await repository.GetUncleanedInputsForClosedSessionsAsync(BatchSize, cancellationToken);
        var repairBatch = await repository.GetPromptPrefixedCleanedInputsForClosedSessionsAsync(BatchSize, cancellationToken);
        if (uncleanedBatch.Count == 0 && repairBatch.Count == 0)
            return;

        if (uncleanedBatch.Count > 0)
        {
            _logger.LogInformation(
                "[CleanedUserInputBackfillJob] Cleaning {Count} input(s) from closed sessions",
                uncleanedBatch.Count);
        }

        if (repairBatch.Count > 0)
        {
            _logger.LogInformation(
                "[CleanedUserInputBackfillJob] Repairing {Count} prompt-prefixed cleaned input(s)",
                repairBatch.Count);
        }

        var uncleanedBySession = uncleanedBatch
            .GroupBy(static row => row.SessionId, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.ToList(), StringComparer.Ordinal);
        var repairBySession = repairBatch
            .GroupBy(static row => row.SessionId, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.ToList(), StringComparer.Ordinal);

        var sessionIds = uncleanedBySession.Keys
            .Concat(repairBySession.Keys)
            .Distinct(StringComparer.Ordinal);

        foreach (var sessionId in sessionIds)
        {
            var sessionTuiOutput = await TryBuildSessionTuiOutputAsync(
                repository,
                sessionId,
                cancellationToken);

            if (uncleanedBySession.TryGetValue(sessionId, out var uncleanedRows))
            {
                // Sort ascending by timestamp so adjacent-row superseded detection works.
                uncleanedRows.Sort(static (a, b) => a.TimestampUTC.CompareTo(b.TimestampUTC));
                for (int i = 0; i < uncleanedRows.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var row = uncleanedRows[i];
                    var nextTs = i < uncleanedRows.Count - 1
                        ? (DateTime?)uncleanedRows[i + 1].TimestampUTC
                        : null;
                    try
                    {
                        await cleanedService.CleanAndPersistAsync(row.Id, sessionTuiOutput, nextTs, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(
                            ex,
                            "[CleanedUserInputBackfillJob] Failed to clean UserInput {UserInputId}",
                            row.Id);
                    }
                }
            }

            if (!repairBySession.TryGetValue(sessionId, out var repairRows))
                continue;

            repairRows.Sort(static (a, b) => a.TimestampUTC.CompareTo(b.TimestampUTC));
            for (int i = 0; i < repairRows.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var row = repairRows[i];
                var nextTs = i < repairRows.Count - 1
                    ? (DateTime?)repairRows[i + 1].TimestampUTC
                    : null;
                try
                {
                    await cleanedService.RepairAndPersistAsync(row.Id, sessionTuiOutput, nextTs, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "[CleanedUserInputBackfillJob] Failed to repair UserInput {UserInputId}",
                        row.Id);
                }
            }
        }
    }

    private async Task<string?> TryBuildSessionTuiOutputAsync(
        IRepository repository,
        string sessionId,
        CancellationToken cancellationToken)
    {
        try
        {
            var chunks = await repository.GetSessionLogChunksAsync(sessionId, cancellationToken);
            if (chunks.Count == 0)
                return null;

            var totalLength = chunks.Sum(static chunk => chunk.Content.Length);
            if (totalLength == 0)
                return null;

            var combined = new byte[totalLength];
            var offset = 0;
            foreach (var chunk in chunks)
            {
                Buffer.BlockCopy(chunk.Content, 0, combined, offset, chunk.Content.Length);
                offset += chunk.Content.Length;
            }

            return Encoding.UTF8.GetString(combined);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "[CleanedUserInputBackfillJob] Failed to build TUI output snapshot for session {SessionId}",
                sessionId);
            return null;
        }
    }
}
