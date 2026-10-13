using VibeRails.DB;
using VibeRails.Interfaces;

namespace VibeRails.Services;

public class SessionTranscriptService(
    IRepository repository,
    ISessionOutputParser sessionOutputParser) : ISessionTranscriptService
{
    public async Task<string> GetOrBuildAsync(string sessionId, CancellationToken cancellationToken, bool forceRebuild = false)
    {
        var existing = await repository.GetSessionOutputAsync(sessionId, cancellationToken);
        if (!forceRebuild && existing != null && !string.IsNullOrWhiteSpace(existing.Text))
            return existing.Text;

        var text = await BuildCurrentAsync(sessionId, cancellationToken);

        await repository.SaveSessionOutputAndMarkProcessedAsync(sessionId, text, cancellationToken);

        return text;
    }

    /// <inheritdoc />
    public async Task<string> BuildCurrentAsync(string sessionId, CancellationToken cancellationToken)
    {
        var chunks = await repository.GetSessionLogChunksAsync(sessionId, cancellationToken);
        var userInputs = await repository.GetUserInputsForSessionAsync(sessionId, cancellationToken);
        return await sessionOutputParser.ParseTranscriptAsync(chunks, userInputs, cancellationToken);
    }
}
