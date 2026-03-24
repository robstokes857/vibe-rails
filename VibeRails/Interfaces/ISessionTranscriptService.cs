namespace VibeRails.Interfaces;

public interface ISessionTranscriptService
{
    Task<string> GetOrBuildAsync(string sessionId, CancellationToken cancellationToken, bool forceRebuild = false);
}
