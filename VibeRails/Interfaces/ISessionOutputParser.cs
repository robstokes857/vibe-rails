using VibeRails.DTOs;

namespace VibeRails.Interfaces;

public interface ISessionOutputParser
{
    Task<string> ParseAsync(IReadOnlyList<SessionLogChunkRecord> chunks, CancellationToken cancellationToken = default);
}
