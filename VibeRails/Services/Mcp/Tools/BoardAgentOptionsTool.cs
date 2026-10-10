using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;
using TokenSaver;
using VibeRails.DTOs;
using VibeRails.Services.AgentTools;
using VibeRails.Services.Board;
using VibeRails.Services.Integrations.VibeCodeRemote;
using VibeRails.Services.LlmClis;

namespace VibeRails.Services.Mcp.Tools;

/// <summary>Discovers the current root project's Board agent picker and shared launch capabilities.</summary>
[McpServerToolType]
public sealed class BoardAgentOptionsTool
{
    private readonly IHttpClientFactory clients;
    private readonly Func<string, string?> readEnvironment;

    public BoardAgentOptionsTool(IHttpClientFactory clients) : this(clients, Environment.GetEnvironmentVariable) { }

    internal BoardAgentOptionsTool(IHttpClientFactory clients, Func<string, string?> readEnvironment)
    {
        this.clients = clients;
        this.readEnvironment = readEnvironment;
    }

    /// <summary>Lists enabled Board assignees and the model, effort, mode and speed options for base CLIs.</summary>
    [McpServerTool]
    [Description("List the same enabled base agents and saved environments as the current VibeRails project's Board picker, plus supported base launch settings, including Codex model/speed combinations. Use the returned assignee key and baseLlmOptions with create_board_card or update_board_card, then start_board_agent. Requires a VibeRails-launched terminal with an open root backend. Saved environment settings and credentials are not returned.")]
    public async Task<string> ListBoardAgentOptions(CancellationToken cancellationToken = default)
    {
        var sessionToken = readEnvironment(LocalToolApiContext.SessionTokenVariable);
        var tabToken = readEnvironment(LocalToolApiContext.TabTokenVariable);
        var apiBase = readEnvironment(LocalToolApiContext.ApiBaseUrlVariable);
        if (string.IsNullOrWhiteSpace(sessionToken) || string.IsNullOrWhiteSpace(tabToken)
            || string.IsNullOrWhiteSpace(apiBase) || !LlmProxyBaseUrl.TryNormalizeLoopback(apiBase, out var baseUrl))
            return "FAIL: No inherited VibeRails root API context is available. Launch the CLI from VibeRails to discover its Board agents.";

        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            using var request = new HttpRequestMessage(HttpMethod.Get,
                new Uri(new Uri(baseUrl), "/api/v1/llm-picker/preferences"));
            request.Headers.Add(LlmProxyCodexConfig.SessionHeaderName, sessionToken);
            request.Headers.Add(LlmProxyCodexConfig.TabHeaderName, tabToken);
            using var response = await clients.CreateClient(BoardAgentLaunchTool.HttpClientName)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode)
                return $"FAIL: The local VibeRails backend could not list Board agents (HTTP {(int)response.StatusCode}).";
            var bytes = await SessionSharingService.ReadBoundedAsync(
                await response.Content.ReadAsStreamAsync(deadline.Token), 512 * 1024, deadline.Token);
            var preferences = JsonSerializer.Deserialize(bytes, AppJsonSerializerContext.Default.LlmPickerPreferencesResponse);
            if (preferences?.Items is null)
                return "FAIL: The local VibeRails backend returned no Board agent picker.";
            return FormatOptions(preferences);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // Never include credential-bearing requests, response bodies or exception details.
            return "FAIL: Could not read Board agents from the local VibeRails backend. Check that the root backend is open.";
        }
    }

    private static string FormatOptions(LlmPickerPreferencesResponse preferences)
    {
        var builder = new StringBuilder("Enabled Board agents for the current VibeRails project:\n");
        foreach (var item in preferences.Items.Where(item => item.Enabled)
                     .OrderBy(item => item.Kind == "base" ? 0 : 1).ThenBy(item => item.Order))
        {
            if (!BoardSelection.TryParse(item.Key, out var selection) || selection is null
                || selection.IsEnvironment != (item.Kind == "environment")
                || selection.EnvironmentId != item.EnvironmentId)
                continue;
            builder.Append("- ").Append(selection.Key).Append(": ").Append(BoardPromptComposer.SanitizeLine(item.Label, 200)).Append('\n');
            if (selection.IsEnvironment)
            {
                builder.Append("  Uses saved environment settings; baseLlmOptions cannot override it.\n");
                continue;
            }
            var pinned = BaseLlmOptionsBuilder.PinnedModel(selection.Llm);
            if (pinned is not null) builder.Append("  Fixed model: ").Append(pinned).Append('\n');
            AppendAllowed(builder, "Effort", selection.Llm, ["none", "minimal", "low", "medium", "high", "xhigh", "max", "ultra"],
                value => new BaseLlmOptions(Effort: value), value => value?.Effort);
            AppendAllowed(builder, "Start mode", selection.Llm, ["plan", "accept-edits", "interactive", "autopilot", "build"],
                value => new BaseLlmOptions(Mode: value), value => value?.Mode);
        }
        builder.Append("\nCodex model / speed catalog (shared with the UI):\n");
        foreach (var model in CodexModelCapabilities.Models)
            builder.Append("- ").Append(model).Append(": default, ")
                .AppendJoin(", ", new[] { "fast", "ultrafast" }.Where(speed => CodexModelCapabilities.SupportsSpeed(model, speed)))
                .Append('\n');
        builder.Append("\nbaseLlmOptions fields: model, effort, mode, yolo, speed. Omitted/default options inherit CLI configuration. ")
            .Append("For other selectable models use the provider's model ID; fixed-model agents do not accept a different model. ")
            .Append("YOLO is optional and defaults to false. Speed is Codex-only; ultrafast requires gpt-6-astra.\n")
            .Append("Example: update_board_card(card=..., assignee=\"base:codex\", baseLlmOptions={\"model\":\"gpt-6-astra\",\"effort\":\"xhigh\",\"speed\":\"ultrafast\"}). ")
            .Append("Then start_board_agent(card=...) uses the saved choices. Passing baseLlmOptions replaces the whole options object; ")
            .Append("omitting it preserves options for an unchanged assignee. clearBaseLlmOptions=true removes all card overrides. ")
            .Append("Assignment changes affect future launches, not an already-running agent.");
        return builder.ToString();
    }

    private static void AppendAllowed(StringBuilder builder, string label, LLM llm, string[] candidates,
        Func<string, BaseLlmOptions> create, Func<BaseLlmOptions?, string?> read)
    {
        var allowed = new List<string>();
        foreach (var candidate in candidates)
        {
            try
            {
                if (read(BaseLlmOptionsBuilder.Normalize(llm, create(candidate))) == candidate) allowed.Add(candidate);
            }
            catch (ArgumentException) { }
        }
        if (allowed.Count > 0) builder.Append("  ").Append(label).Append(": default, ").AppendJoin(", ", allowed).Append('\n');
    }
}
