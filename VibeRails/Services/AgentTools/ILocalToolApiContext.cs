namespace VibeRails.Services.AgentTools;

public interface ILocalToolApiContext
{
    string ApiBaseUrl { get; }
    string SessionToken { get; }
    string TabToken { get; }
    string? CurrentTabId { get; }
    IReadOnlyDictionary<string, string> BuildEnvironment(string? currentSessionId = null);
    IReadOnlyDictionary<string, string> BuildEnvironmentForTab(string tabId) => BuildEnvironment();
    void RevokeTab(string tabId) { }
    void CancelTabLaunch(string tabId) { }
    void AcceptParentContact(string session, string tab) { }
    void BindSession(string tabId, string sessionId) { }
}
