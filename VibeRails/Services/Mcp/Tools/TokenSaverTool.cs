using System.ComponentModel;
using System.Globalization;
using System.Net.Http.Json;
using ModelContextProtocol.Server;
using Serilog;
using TokenSaver;
using VibeRails.DTOs;
using VibeRails.Routes;
using VibeRails.Services.LlmProxy;

namespace VibeRails.Services.Mcp.Tools;

/// <summary>
/// Lets an agent turn VibeRails' token compression off for a few minutes when it needs to read
/// output the saver elided, then get it back automatically.
///
/// The interesting part is which process answers. This tool usually runs inside a <c>vb mcp</c>
/// child that the CLI spawned, while the proxy doing the compressing lives in the terminal tab's
/// own child <c>vb.exe</c> — a different process entirely. The link between them is the environment:
/// the CLI was launched with <see cref="LocalLlmProxyContext.BaseUrlVariable"/> and its two proxy
/// tokens, and a spawned MCP server inherits that environment, so it can call the exact proxy whose
/// output it is reading. That also makes the pause per-tab for free: the only proxy this tool can
/// reach is its own tab's.
///
/// When those variables are absent — the dashboard's MCP Explorer, or a CLI launched with the proxy
/// off — there is nothing to pause, and the tools say so rather than silently succeeding.
/// </summary>
[McpServerToolType]
public sealed class TokenSaverTool
{
    /// <summary>
    /// Named client with a short timeout: every call is a loopback POST to a process on this
    /// machine, so a slow one means something is wrong and the agent should be told promptly
    /// rather than parked on the default 100-second timeout.
    /// </summary>
    public const string HttpClientName = "viberails-token-saver-control";

    private const string NoProxyMessage =
        "No VibeRails LLM proxy is attached to this session, so no token compression is being "
        + "applied — the tool output you are seeing is already verbatim. (Nothing to pause.)";

    /// <summary>
    /// The pause window as prose, built from the same constant the window is enforced from, so the
    /// number stated in every <c>[Description]</c> below cannot drift from the number an agent gets.
    /// </summary>
    private const string PauseWindowText = TokenSaverPauseState.PauseWindowMinutesText + " minutes";

    private readonly IHttpClientFactory _httpClientFactory;

    public TokenSaverTool(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    [McpServerTool]
    [Description(
        "Turns VibeRails' token compression off for " + PauseWindowText + " for this terminal tab. "
        + "Tool output is normally compressed before you see it: long output keeps only the first "
        + "150 and last 50 lines ('[... N lines elided ...]'), 3+ identical lines collapse to one "
        + "('[xN]'), and runs of passing test lines collapse to '[... N passed ...]'. Call this when "
        + "you need to read one of those elided regions verbatim, then re-run the command that "
        + "produced it. The pause expires on its own after " + PauseWindowText + "; call "
        + "resume_token_saver as soon as you have what you needed. Do not call it speculatively or "
        + "on a hunch: switching compression on or off invalidates the provider's prompt cache for "
        + "this conversation, which costs real money on the next turn.")]
    public Task<string> PauseTokenSaver(CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "pause", DescribePause, cancellationToken);

    [McpServerTool]
    [Description(
        "Restores VibeRails' token compression immediately, ending an active pause_token_saver "
        + "window early. Safe to call when nothing is paused.")]
    public Task<string> ResumeTokenSaver(CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "resume", DescribeResume, cancellationToken);

    [McpServerTool]
    [Description(
        "Reports whether VibeRails is compressing this session's tool output, and whether a "
        + "pause_token_saver window is currently open and when it ends.")]
    public Task<string> GetTokenSaverStatus(CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, "status", DescribeStatus, cancellationToken);

    private static string DescribePause(TokenSaverPausePayload state)
    {
        if (!state.SaverEnabled)
        {
            return "Token compression is switched off for this session already, so there was "
                + "nothing to pause — the tool output you are seeing is verbatim.";
        }

        return $"Token compression paused until {FormatExpiry(state.PausedUntilUtc)}. Re-run the "
            + "command whose output you need in full; it will now arrive uncompressed. Compression "
            + "comes back on its own — call resume_token_saver once you have what you needed.";
    }

    private static string DescribeResume(TokenSaverPausePayload state)
    {
        // Only reachable if another pause landed between this resume and the read-back.
        if (state.PausedUntilUtc is not null)
            return $"Token compression is still paused until {FormatExpiry(state.PausedUntilUtc)}.";

        return state.SaverEnabled
            ? "Token compression is active again."
            : "Token compression is not paused. It is switched off for this session anyway, so tool "
                + "output reaches you verbatim.";
    }

    private static string DescribeStatus(TokenSaverPausePayload state)
    {
        if (!state.SaverEnabled)
            return "Token compression is switched off for this session. Tool output reaches you verbatim.";

        return state.PausedUntilUtc is null
            ? "Token compression is on. Long tool output may be truncated ('[... N lines elided ...]'), "
                + "deduplicated ('[xN]'), or have passing tests elided ('[... N passed ...]'). "
                + "Call pause_token_saver if you need to read an elided region."
            : $"Token compression is on but paused until {FormatExpiry(state.PausedUntilUtc)}; "
                + "tool output is reaching you verbatim until then.";
    }

    private static string FormatExpiry(string? pausedUntilUtc)
    {
        if (!DateTimeOffset.TryParse(
                pausedUntilUtc,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var until))
        {
            return "an unknown time";
        }

        // Both forms on purpose: the absolute instant survives being quoted back later, and the
        // relative one is what the agent actually reasons about.
        var remaining = until - DateTimeOffset.UtcNow;
        var minutes = Math.Max(0, Math.Round(remaining.TotalMinutes, 1));
        return $"{until.UtcDateTime:HH:mm:ss}Z (~{minutes.ToString("0.#", CultureInfo.InvariantCulture)} min from now)";
    }

    private async Task<string> SendAsync(
        HttpMethod method,
        string action,
        Func<TokenSaverPausePayload, string> describe,
        CancellationToken cancellationToken)
    {
        var baseUrl = ReadEnv(LocalLlmProxyContext.BaseUrlVariable);
        var sessionToken = ReadEnv(LocalLlmProxyContext.SessionTokenVariable);
        var tabToken = ReadEnv(LocalLlmProxyContext.TabTokenVariable);
        if (baseUrl is null || sessionToken is null || tabToken is null)
            return NoProxyMessage;

        // The two proxy tokens go on this request as headers, so the destination has to be proven
        // local before they are attached — the base URL arrives in an inherited environment
        // variable, and an absolute URL naming any other host would post this session's credentials
        // there. Refusing outright rather than stripping the tokens and trying anyway: a call to a
        // host that is not this machine's proxy has no useful meaning.
        if (!LlmProxyBaseUrl.TryNormalizeLoopback(baseUrl, out var normalizedBaseUrl))
        {
            Log.Warning(
                "[TokenSaver] Refusing to send proxy credentials to a non-loopback control URL. "
                + "variable={Variable} action={Action}",
                LocalLlmProxyContext.BaseUrlVariable,
                action);
            return $"FAIL: {LocalLlmProxyContext.BaseUrlVariable} does not name a local VibeRails "
                + "proxy, so the token-saver controls are unavailable for this session.";
        }

        var url = string.Concat(
            normalizedBaseUrl,
            TokenSaverPauseRoutes.ControlPathPrefix,
            "/",
            action);

        try
        {
            using var request = new HttpRequestMessage(method, url);
            request.Headers.TryAddWithoutValidation(LlmProxyCodexConfig.SessionHeaderName, sessionToken);
            request.Headers.TryAddWithoutValidation(LlmProxyCodexConfig.TabHeaderName, tabToken);

            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return $"FAIL: the VibeRails proxy answered {(int)response.StatusCode} "
                    + $"({response.ReasonPhrase}) for token-saver {action}.";
            }

            var state = await response.Content.ReadFromJsonAsync(
                AppJsonSerializerContext.Default.TokenSaverPausePayload,
                cancellationToken);
            if (state is null)
                return $"FAIL: the VibeRails proxy returned an empty token-saver {action} response.";

            return describe(state);
        }
        catch (Exception ex)
        {
            // The agent gets a readable sentence; the detail goes to the file log. A failed pause
            // must not read like a successful one — worst case the agent re-runs a command and
            // still sees elision markers, which is confusing but not wrong.
            //
            // The exception text stays out of the returned string on purpose. HttpRequestException
            // routinely carries the resolved host and port, and this return value is model-visible
            // output; the log is where an operator can see it without it entering a transcript.
            Log.Warning(ex, "[TokenSaver] MCP control call failed. action={Action} url={Url}", action, url);
            return $"FAIL: could not reach the VibeRails proxy to {action} token compression. "
                + "See the VibeRails log for details.";
        }
    }

    private static string? ReadEnv(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
