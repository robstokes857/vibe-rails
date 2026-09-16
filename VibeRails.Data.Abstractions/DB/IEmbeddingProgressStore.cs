using VibeRails.DTOs;

namespace VibeRails.DB;

public interface IEmbeddingProgressStore
{
    Task<List<UnembeddedUserInputRow>> GetUnembeddedUserInputsAsync(int batchSize, CancellationToken cancellationToken);

    Task MarkUserInputsBertEmbeddedAsync(IReadOnlyCollection<long> userInputIds, DateTime utcNow, CancellationToken cancellationToken);

    Task IncrementUserInputBertEmbedFailureCountsAsync(IReadOnlyCollection<long> userInputIds, CancellationToken cancellationToken);

    Task<List<string>> GetUnaggregatedEndedSessionIdsAsync(int batchSize, CancellationToken cancellationToken);

    Task<List<string>> GetUserInputTextsForSessionAsync(string sessionId, CancellationToken cancellationToken);

    Task MarkSessionsAggregateEmbeddedAsync(IReadOnlyCollection<string> sessionIds, DateTime utcNow, CancellationToken cancellationToken);

    Task IncrementSessionAggregateEmbedFailureCountsAsync(IReadOnlyCollection<string> sessionIds, CancellationToken cancellationToken);
}
