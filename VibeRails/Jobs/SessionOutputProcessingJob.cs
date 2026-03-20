using VibeRails.Interfaces;
using VibeRails.Services;

namespace VibeRails.Jobs;

public sealed class SessionOutputProcessingJob(
    ILogger<SessionOutputProcessingJob> logger,
    ISystemResourceService resources,
    IServiceScopeFactory scopeFactory,
    ISessionOutputParser sessionOutputParser) : JobBase(logger, resources)
{
    private const int BatchSize = 5;

    protected override TimeSpan Interval => TimeSpan.FromMinutes(1);
    protected override JobPriority Priority => JobPriority.Low;

    protected override async Task ExecuteJob(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbService = scope.ServiceProvider.GetRequiredService<IDbService>();
        var sessionIds = await dbService.GetEndedUnprocessedSessionIdsAsync(BatchSize, cancellationToken);

        foreach (var sessionId in sessionIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var chunks = await dbService.GetSessionLogChunksAsync(sessionId, cancellationToken);
                var text = await sessionOutputParser.ParseAsync(chunks, cancellationToken);
                await dbService.SaveSessionOutputAndMarkProcessedAsync(sessionId, text, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "[SessionOutputProcessingJob] Failed to parse session output for {SessionId}",
                    sessionId);
            }
        }
    }
}
