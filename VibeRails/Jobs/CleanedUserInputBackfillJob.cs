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

        var batch = await repository.GetUncleanedInputsForClosedSessionsAsync(BatchSize, cancellationToken);
        if (batch.Count == 0)
            return;

        _logger.LogInformation(
            "[CleanedUserInputBackfillJob] Cleaning {Count} input(s) from closed sessions",
            batch.Count);

        foreach (var sessionGroup in batch.GroupBy(static row => row.SessionId, StringComparer.Ordinal))
        {
            var sessionTuiOutput = await TryBuildSessionTuiOutputAsync(
                repository,
                sessionGroup.Key,
                cancellationToken);

            foreach (var row in sessionGroup)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await cleanedService.CleanAndPersistAsync(row.Id, sessionTuiOutput, cancellationToken);
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
