using System.ComponentModel;
using System.Net.Http.Json;
using ModelContextProtocol.Server;
using TokenSaver;
using VibeRails.DTOs;
using VibeRails.Routes;
using VibeRails.Services.AgentTools;

namespace VibeRails.Services.Mcp.Tools;

/// <summary>Only the calling CLI's captured session can be ended; no caller-supplied target.</summary>
[McpServerToolType]
public sealed class AgentSessionTool
{
    private readonly IHttpClientFactory clients;
    private readonly Func<string, string?> readEnvironment;
    public AgentSessionTool(IHttpClientFactory clients) : this(clients, Environment.GetEnvironmentVariable) { }
    internal AgentSessionTool(IHttpClientFactory clients, Func<string, string?> readEnvironment)
    {
        this.clients = clients; this.readEnvironment = readEnvironment;
    }
    public const string HttpClientName = "viberails-agent-session-control";
    public const string BaseUrlVariable = "VIBERAILS_AGENT_CONTROL_BASE";
    public const string SessionTokenVariable = "VIBERAILS_AGENT_CONTROL_SESSION_TOKEN";
    public const string TabTokenVariable = "VIBERAILS_AGENT_CONTROL_TAB_TOKEN";

    [McpServerTool]
    [Description("When completely finished, schedule your own PTY and its child processes to close in 30 seconds. First save all reports, comments and handoffs, make intended card moves, and call complete_board_agent. Call this last, then send your final response; do not start more work or wait for another agent afterward. Safe to repeat: the original deadline stays. No target argument; cannot end another session. Recordings are retained. This is an intentional normal terminal exit, not a review approval.")]
    public async Task<string> EndAgentSession(CancellationToken cancellationToken = default)
    {
        var session = readEnvironment(LocalToolApiContext.CurrentSessionIdVariable);
        var baseUrl = readEnvironment(BaseUrlVariable);
        var sessionToken = readEnvironment(SessionTokenVariable);
        var tabToken = readEnvironment(TabTokenVariable);
        if (!Guid.TryParseExact(session, "D", out _) || string.IsNullOrWhiteSpace(sessionToken) || string.IsNullOrWhiteSpace(tabToken)
            || !LlmProxyBaseUrl.TryNormalizeLoopback(baseUrl ?? "", out var normalized))
            return "FAIL: No current VibeRails agent session with local control credentials is available. Nothing was stopped.";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(normalized), AgentSessionControlRoutes.Path));
            request.Headers.TryAddWithoutValidation(LlmProxyCodexConfig.SessionHeaderName, sessionToken);
            request.Headers.TryAddWithoutValidation(LlmProxyCodexConfig.TabHeaderName, tabToken);
            request.Headers.TryAddWithoutValidation(AgentSessionControlRoutes.SessionHeader, session);
            using var response = await clients.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return $"FAIL: Session completion was not scheduled (HTTP {(int)response.StatusCode}). The session may have already ended.";
            var result = await response.Content.ReadFromJsonAsync(AppJsonSerializerContext.Default.AgentSessionEndResponse, cancellationToken);
            return result is null ? "FAIL: No completion deadline was returned."
                : $"Your PTY will close at {result.ClosesAtUtc:O} (30-second grace period from the first call). Send your final response now. Recordings are retained.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return "FAIL: Could not reach this session's local backend. Completion was not confirmed."; }
    }
}
