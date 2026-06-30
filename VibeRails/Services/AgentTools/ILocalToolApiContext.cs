namespace VibeRails.Services.AgentTools;

public interface ILocalToolApiContext
{
    string ApiBaseUrl { get; }
    string SessionToken { get; }
    string TabToken { get; }
    string? CurrentTabId { get; }
    IReadOnlyDictionary<string, string> BuildEnvironment(string? currentSessionId = null);
}
