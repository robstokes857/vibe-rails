namespace VibeRails.Interfaces;

public interface ISessionTranscriptService
{
    Task<string> GetOrBuildAsync(string sessionId, CancellationToken cancellationToken, bool forceRebuild = false);
    /// <summary>Builds the current transcript without caching it or marking a live session processed.</summary>
    Task<string> BuildCurrentAsync(string sessionId, CancellationToken cancellationToken);
}
