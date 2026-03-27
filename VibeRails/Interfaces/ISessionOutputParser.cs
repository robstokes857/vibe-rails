using VibeRails.DTOs;

namespace VibeRails.Interfaces;

public interface ISessionOutputParser
{
    Task<string> ParseAsync(IReadOnlyList<SessionLogChunkRecord> chunks, CancellationToken cancellationToken = default);

    Task<string> ParseTranscriptAsync(
        IReadOnlyList<SessionLogChunkRecord> chunks,
        IReadOnlyList<UserInputRecord> userInputs,
        CancellationToken cancellationToken = default)
        => ParseAsync(chunks, cancellationToken);
}
