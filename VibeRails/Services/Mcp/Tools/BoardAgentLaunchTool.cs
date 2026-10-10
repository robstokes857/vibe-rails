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
    [Description("Start work on a card using its saved assignee and launch options, exactly like the Board's Start work action. Save the desired model, reasoning effort and service tier with update_board_card first. Requires an open VibeRails root backend and inherited root API credentials; only cards in that root's project can launch. Starts a new agent terminal, applies the normal workspace and Board tool grants, and links its session. Returns the session ID for get_board_agent_status; starting is not completing. When asked to wait, poll that session until its completion/exit and check remaining workflow status. Do not retry an uncertain launch before checking status; an agent may already be running.")]
    public async Task<string> StartBoardAgent(
        [Description("Required card key or row ID in the current root backend's project.")] string card,
        CancellationToken cancellationToken = default)
    {
        var target = card?.Trim();
        if (!IsCardIdentifier(target))
            return "FAIL: Enter a card key or row ID (1 to 256 letters, digits, hyphens or underscores).";

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
            request.Content = JsonContent.Create(new LaunchBoardCardRequest(Intent: "work"),
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
