using System.ComponentModel;
using System.Net.Http.Json;
using System.Text.Json;
using ModelContextProtocol.Server;
using TokenSaver;
using VibeRails.Services.AgentTools;
using VibeRails.Services.Integrations.VibeCodeRemote;
using VibeRails.Services.VCA;

namespace VibeRails.Services.Mcp.Tools;

/// <summary>Creates a public replay link for the calling terminal through the existing root API.</summary>
[McpServerToolType]
public sealed class SessionSharingTool
{
    public const string HttpClientName = "viberails-session-sharing";
    private readonly IHttpClientFactory clients;
    private readonly Func<string, string?> readEnvironment;

    public SessionSharingTool(IHttpClientFactory clients) : this(clients, Environment.GetEnvironmentVariable) { }

    internal SessionSharingTool(IHttpClientFactory clients, Func<string, string?> readEnvironment)
    {
        this.clients = clients;
        this.readEnvironment = readEnvironment;
    }

    /// <summary>Returns a share URL, upload status, expiry and a ready-to-copy commit trailer.</summary>
    [McpServerTool]
    [Description("Create a public sharing link for this VibeRails terminal session. Anyone with the link can view its terminal output, prompts and saved code changes. Requires a signed-in VibeRails account and an open root backend. Active sessions can be linked now; replay is available after the session ends and uploads. Use when session sharing is requested or required by VCA. Add the returned vibe-share:<url> line to your commit message; reuse that link for this session instead of creating one per commit. Links expire after one month and can be revoked on the website. No session-id argument: only the calling terminal is shared.")]
    public async Task<string> CreateSessionShareLink(
        [Description("A descriptive name for the sharing link (1 to 160 characters).")] string displayName,
        CancellationToken cancellationToken = default)
    {
        var name = displayName?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 160)
            return "FAIL: Enter a link name of 1 to 160 characters.";

        var session = readEnvironment(LocalToolApiContext.CurrentSessionIdVariable);
        var sessionToken = readEnvironment(LocalToolApiContext.SessionTokenVariable);
        var tabToken = readEnvironment(LocalToolApiContext.TabTokenVariable);
        if (!Guid.TryParseExact(session, "D", out var sessionId) || sessionId == Guid.Empty
            || string.IsNullOrWhiteSpace(sessionToken) || string.IsNullOrWhiteSpace(tabToken)
            || !LlmProxyBaseUrl.TryNormalizeLoopback(readEnvironment(LocalToolApiContext.ApiBaseUrlVariable) ?? "", out var baseUrl))
            return "FAIL: No current VibeRails terminal session with root API credentials is available. Use Share session in the terminal tab or launch the CLI from VibeRails.";

        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            using var request = new HttpRequestMessage(HttpMethod.Post,
                new Uri(new Uri(baseUrl), $"/api/v1/sessions/{sessionId:D}/sharing-links"));
            request.Headers.Add(LlmProxyCodexConfig.SessionHeaderName, sessionToken);
            request.Headers.Add(LlmProxyCodexConfig.TabHeaderName, tabToken);
            request.Content = JsonContent.Create(new CreateSessionShareRequest(name),
                SessionSharingJsonContext.Default.CreateSessionShareRequest);
            using var response = await clients.CreateClient(HttpClientName)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode)
                return $"FAIL: The local VibeRails backend could not create a sharing link (HTTP {(int)response.StatusCode}). Use Share session in the terminal tab to check the connection.";

            var bytes = await SessionSharingService.ReadBoundedAsync(
                await response.Content.ReadAsStreamAsync(deadline.Token), 16 * 1024, deadline.Token);
            var result = JsonSerializer.Deserialize(bytes, SessionSharingJsonContext.Default.SessionShareResponse);
            if (result is null) return "FAIL: The local backend returned no sharing result.";
            if (!result.Success) return $"FAIL: {result.Message}";
            if (!SessionShareCommitRule.IsShareUrl(result.Url) || result.ExpiresUtc is null)
                return "FAIL: The local backend returned an invalid sharing link.";

            return $"{result.Message}\nStatus: {result.Status}\nExpires: {result.ExpiresUtc:O}\n"
                + "Anyone with this link can view the shared session. Add this line to the commit message:\n"
                + $"{SessionShareCommitRule.Trailer}{result.Url}";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // A share URL is a public-read capability. Never log response bodies or exception text.
            return "FAIL: Could not confirm sharing-link creation with the local backend. Check Sharing links on viberails.ai before retrying; a link may already have been created.";
        }
    }
}
