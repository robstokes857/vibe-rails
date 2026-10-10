using System.ComponentModel;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ModelContextProtocol.Server;
using TokenSaver;
using VibeRails.Services.AgentTools;
using VibeRails.Services.Integrations.VibeCodeRemote;
using VibeRails.Services.VCA;

namespace VibeRails.Services.Mcp.Tools;

/// <summary>Creates a replay link for the calling terminal through the existing root API.</summary>
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
    [Description("Create a sharing link for this VibeRails terminal session. By default anyone with the link can view its terminal output, prompts and saved code changes; pass emails to allow only people who sign in to viberails.ai with one of those verified addresses (up to 10 per session). Requires a signed-in VibeRails account and an open root backend. Active sessions can be linked now; replay is available after the session ends and uploads. Use when session sharing is requested or required by VCA. Add the returned vibe-share:<url> line to your commit message; reuse that link for this session instead of creating one per commit. Links expire after one month and can be revoked on the website. No session-id argument: only the calling terminal is shared.")]
    public async Task<string> CreateSessionShareLink(
        [Description("A descriptive name for the sharing link (1 to 160 characters).")] string displayName,
        CancellationToken cancellationToken = default,
        [Description("Optional comma-separated email addresses. When given, only people signed in to viberails.ai with one of these verified addresses can open the link; nobody is notified or looked up. Leave empty for a public link.")] string? emails = null)
    {
        var name = displayName?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 160)
            return "FAIL: Enter a link name of 1 to 160 characters.";
        var listed = (emails ?? "").Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (listed.Length > 0 && !ShareAudience.TryNormalize(ShareAudience.Email, listed, out _, out _, out var audienceError))
            return "FAIL: " + audienceError;

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
            var http = clients.CreateClient(HttpClientName);
            if (listed.Length > 0)
            {
                // This process may be newer than the root it inherited. An older root ignores the new
                // fields, creates a public link and uploads the recording for it, so ask it first:
                // an older root has no capabilities route and is then never asked to create anything.
                using var probe = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(baseUrl), "/api/v1/session-sharing/capabilities"));
                probe.Headers.Add(LlmProxyCodexConfig.SessionHeaderName, sessionToken);
                probe.Headers.Add(LlmProxyCodexConfig.TabHeaderName, tabToken);
                using var support = await http.SendAsync(probe, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                if (support.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
                    return "FAIL: The running VibeRails backend does not support links for listed people, so no link was created. Update VibeRails and restart it, then try again; or leave emails empty for a public link.";
                if (!support.IsSuccessStatusCode)
                    return $"FAIL: The local VibeRails backend could not confirm sharing support (HTTP {(int)support.StatusCode}). Use Share session in the terminal tab to check the connection.";
                var capabilities = JsonSerializer.Deserialize(await SessionSharingService.ReadBoundedAsync(
                    await support.Content.ReadAsStreamAsync(deadline.Token), 16 * 1024, deadline.Token), SessionSharingJsonContext.Default.ShareCapabilitiesResponse);
                if (!ShareAudience.Supports(capabilities))
                    return "FAIL: The running VibeRails backend does not support links for listed people, so no link was created. Update VibeRails and restart it, then try again.";
            }
            using var request = new HttpRequestMessage(HttpMethod.Post,
                new Uri(new Uri(baseUrl), $"/api/v1/sessions/{sessionId:D}/sharing-links"));
            request.Headers.Add(LlmProxyCodexConfig.SessionHeaderName, sessionToken);
            request.Headers.Add(LlmProxyCodexConfig.TabHeaderName, tabToken);
            var body = listed.Length > 0 ? new CreateSessionShareRequest(name, ShareAudience.Email, listed) : new CreateSessionShareRequest(name);
            request.Content = JsonContent.Create(body, SessionSharingJsonContext.Default.CreateSessionShareRequest);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode)
                return $"FAIL: The local VibeRails backend could not create a sharing link (HTTP {(int)response.StatusCode}). Use Share session in the terminal tab to check the connection.";

            var bytes = await SessionSharingService.ReadBoundedAsync(
                await response.Content.ReadAsStreamAsync(deadline.Token), 16 * 1024, deadline.Token);
            var result = JsonSerializer.Deserialize(bytes, SessionSharingJsonContext.Default.SessionShareResponse);
            if (result is null) return "FAIL: The local backend returned no sharing result.";
            if (!result.Success) return $"FAIL: {result.Message}";
            if (!SessionShareCommitRule.IsShareUrl(result.Url) || result.ExpiresUtc is null)
                return "FAIL: The local backend returned an invalid sharing link.";
            // The backend must confirm the requested audience before any URL or commit trailer is
            // handed out: a link whose people were not confirmed may be readable by anyone.
            if (!ShareAudience.Confirms(listed.Length > 0 ? ShareAudience.Email : ShareAudience.Public, result.Access)
                || (listed.Length > 0 && !ShareAudience.SamePeople(listed, result.Recipients)))
                return "FAIL: The local VibeRails backend did not confirm the listed people for this link, so no link is reported. Check Sharing links on viberails.ai and revoke any public link created for this session, then update VibeRails and restart it.";

            var audience = listed.Length > 0
                ? "Only these people can open this link after signing in with that verified email: " + string.Join(", ", result.Recipients!) + "."
                : "Anyone with this link can view the shared session.";
            return $"{result.Message}\nStatus: {result.Status}\nExpires: {result.ExpiresUtc:O}\n"
                + audience + " Add this line to the commit message:\n"
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
