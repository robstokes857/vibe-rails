using System.ComponentModel;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ModelContextProtocol.Server;
using TokenSaver;
using VibeRails.DTOs;
using VibeRails.Services.AgentTools;
using VibeRails.Services.Integrations.VibeCodeRemote;

namespace VibeRails.Services.Mcp.Tools;

/// <summary>Starts a card's saved assignment through the same authenticated root API as Start work.</summary>
[McpServerToolType]
public sealed class BoardAgentLaunchTool
{
    /// <summary>The restricted HTTP client used for the inherited root API.</summary>
    public const string HttpClientName = "viberails-board-agent-launch";
    private readonly IHttpClientFactory clients;
    private readonly Func<string, string?> readEnvironment;

    /// <summary>Creates the tool using the root API credentials inherited by this process.</summary>
    public BoardAgentLaunchTool(IHttpClientFactory clients) : this(clients, Environment.GetEnvironmentVariable) { }

    internal BoardAgentLaunchTool(IHttpClientFactory clients, Func<string, string?> readEnvironment)
    {
        this.clients = clients;
        this.readEnvironment = readEnvironment;
    }

    /// <summary>Starts work and returns the linked session to poll; launch success is not completion.</summary>
    [McpServerTool]
    [Description("Start a card's saved workflow agent, or delegate a worker/reviewer without changing the saved manager or worker assignment. intent=work (default) starts the manager on Agent cards; intent=worker starts the saved worker, selection optionally picks another enabled agent; intent=code_review requests a direct review. task supplies bounded worker instructions. Requires an open VibeRails root and inherited credentials; launches remain project-scoped. Returns a session to poll with get_board_agent_status. Launch success is not completion; check status before retrying an uncertain launch.")]
    public async Task<string> StartBoardAgent(
        [Description("Required card key or row ID in the current root backend's project.")] string card,
        CancellationToken cancellationToken = default,
        [Description("work | manager | worker | code_review. Default work starts the saved manager on Agent cards.")] string intent = "work",
        [Description("Optional enabled picker key for a delegated worker/reviewer from list_board_agent_options. Does not change saved assignments.")] string? selection = null,
        [Description("Optional worker task or repair instructions, up to 4,000 characters. Only with intent=worker.")] string? task = null)
    {
        var target = card?.Trim();
        if (!IsCardIdentifier(target))
            return "FAIL: Enter a card key or row ID (1 to 256 letters, digits, hyphens or underscores).";
        intent = intent.Trim().ToLowerInvariant();
        if (intent is not ("work" or "manager" or "worker" or "code_review"))
            return "FAIL: intent must be work, manager, worker or code_review.";
        if (task is not null && (task.Length > 4000 || intent != "worker"))
            return "FAIL: task is supported only for worker launches and must be at most 4,000 characters.";
        if (selection is not null && (intent is "work" or "manager"))
            return "FAIL: selection overrides are supported only for worker or code_review launches; save the manager on the card.";
        if (selection is not null && !VibeRails.Services.Board.BoardSelection.TryParse(selection, out _))
            return "FAIL: selection must be an enabled base agent or saved environment picker key.";

        var sessionToken = readEnvironment(LocalToolApiContext.SessionTokenVariable);
        var tabToken = readEnvironment(LocalToolApiContext.TabTokenVariable);
        var apiBase = readEnvironment(LocalToolApiContext.ApiBaseUrlVariable);
        if (string.IsNullOrWhiteSpace(sessionToken) || string.IsNullOrWhiteSpace(tabToken) || string.IsNullOrWhiteSpace(apiBase)
            || !LlmProxyBaseUrl.TryNormalizeLoopback(apiBase, out var baseUrl))
            return "FAIL: No inherited VibeRails root API credentials are available. Launch the CLI from VibeRails, or use Start work in the Board.";

        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            using var request = new HttpRequestMessage(HttpMethod.Post,
                new Uri(new Uri(baseUrl), $"/api/v1/board/cards/{Uri.EscapeDataString(target!)}/launch"));
            request.Headers.Add(LlmProxyCodexConfig.SessionHeaderName, sessionToken);
            request.Headers.Add(LlmProxyCodexConfig.TabHeaderName, tabToken);
            request.Content = JsonContent.Create(new LaunchBoardCardRequest(Selection: selection, Intent: intent, Task: task),
                AppJsonSerializerContext.Default.LaunchBoardCardRequest);
            using var response = await clients.CreateClient(HttpClientName)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode)
                return response.StatusCode switch
                {
                    HttpStatusCode.NotFound => "FAIL: Card not found in the current root backend's project (HTTP 404). Cross-project agent launches are not supported.",
                    HttpStatusCode.BadRequest => "FAIL: The root backend rejected the saved assignment or launch options (HTTP 400). Check the card's assignee and options with get_board_card, or use Start work in the Board for details.",
                    HttpStatusCode.Conflict => "FAIL: The root backend could not start this card (HTTP 409). An agent may already be starting/running or the terminal limit may be reached. Check get_board_agent_status and the Board before retrying.",
                    _ => $"FAIL: Could not confirm agent launch with the root backend (HTTP {(int)response.StatusCode}). Check get_board_agent_status before retrying; an agent may already have started."
                };

            var bytes = await SessionSharingService.ReadBoundedAsync(
                await response.Content.ReadAsStreamAsync(deadline.Token), 16 * 1024, deadline.Token);
            var result = JsonSerializer.Deserialize(bytes, AppJsonSerializerContext.Default.LaunchBoardCardResponse);
            if (result is null || !Guid.TryParseExact(result.SessionId, "D", out var sessionId) || sessionId == Guid.Empty
                || !IsCardIdentifier(result.CardId))
                return UnconfirmedLaunch;

            return $"Started agent for card {result.CardId}.\nSession ID: {sessionId:D}\n"
                + $"Launch succeeded; work is not yet complete. Poll get_board_agent_status(card: \"{result.CardId}\", sessionId: \"{sessionId:D}\") "
                + $"for this agent's completion report and recorded exit, then call get_board_agent_status(card: \"{result.CardId}\") to check remaining workflow actions.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // A timed-out request may already have launched an agent. Never retry or log credentials/response bodies.
            return UnconfirmedLaunch;
        }
    }

    private const string UnconfirmedLaunch = "FAIL: Could not confirm agent launch with the root backend. Check get_board_agent_status and the Board before retrying; an agent may already have started.";

    private static bool IsCardIdentifier(string? value) => value is { Length: > 0 and <= 256 }
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
}
