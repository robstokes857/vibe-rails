using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;
using VibeRails.DTOs;
using VibeRails.Services.AgentTools;

namespace VibeRails.Services.Mcp.Tools;

[McpServerToolType]
public sealed class TerminalTools
{
    private readonly IAgentTerminalToolGateway _terminalTools;

    public TerminalTools(IAgentTerminalToolGateway terminalTools)
    {
        _terminalTools = terminalTools;
    }

    [McpServerTool]
    [Description("List VibeRails terminal tabs that can be controlled by terminal tool calls.")]
    public async Task<string> ListTerminals()
    {
        try
        {
            var response = await _terminalTools.ListTerminalsAsync();
            if (response.Terminals.Count == 0)
            {
                return $"No terminal tabs are open. Max terminals: {response.MaxTerminals}.";
            }

            var sb = new StringBuilder();
            sb.AppendLine($"Terminal tabs ({response.Terminals.Count}/{response.MaxTerminals}):");
            foreach (var terminal in response.Terminals)
            {
                sb.AppendLine(FormatTerminal(terminal));
            }

            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            return $"FAIL: {ex.Message}";
        }
    }

    [McpServerTool]
    [Description("Open a new VibeRails terminal tab. Defaults to a plain shell; pass cli as Shell, Claude, Codex, Antigravity, or Copilot.")]
    public async Task<string> OpenTerminal(
        [Description("CLI to start. Defaults to Shell.")] string? cli = "Shell",
        [Description("Optional working directory for the terminal.")] string? workingDirectory = null,
        [Description("Optional custom environment name for AI CLIs.")] string? environmentName = null,
        [Description("Optional terminal title.")] string? title = null,
        [Description("Optional initial prompt for AI CLIs.")] string? initialPrompt = null)
    {
        try
        {
            var terminal = await _terminalTools.OpenTerminalAsync(new AgentToolOpenTerminalRequest(
                WorkingDirectory: NullIfWhiteSpace(workingDirectory),
                Cli: NullIfWhiteSpace(cli) ?? "Shell",
                EnvironmentName: NullIfWhiteSpace(environmentName),
                Title: NullIfWhiteSpace(title),
                InitialPrompt: NullIfWhiteSpace(initialPrompt)));

            return $"Opened terminal.\n{FormatTerminal(terminal)}";
        }
        catch (Exception ex)
        {
            return $"FAIL: {ex.Message}";
        }
    }

    [McpServerTool]
    [Description("Send text input to a VibeRails terminal tab without attaching a viewer. Use submit=true to press Enter after the text.")]
    public async Task<string> SendTerminalInput(
        [Description("Text to send to the terminal.")] string text,
        [Description("Target terminal tab id. If omitted, uses the current tab when available.")] string? tabId = null,
        [Description("Append Enter/Return after the text.")] bool submit = false)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "FAIL: text is required.";
        }

        try
        {
            var response = await _terminalTools.SendInputAsync(
                NullIfWhiteSpace(tabId),
                new TerminalInputRequest(text, submit));

            return response.Success
                ? $"PASS: {response.Message} tab={response.TabId ?? "?"} session={response.SessionId ?? "?"}"
                : $"FAIL: {response.Message}";
        }
        catch (Exception ex)
        {
            return $"FAIL: {ex.Message}";
        }
    }

    [McpServerTool]
    [Description("Read the current terminal snapshot as JSON. The response includes screenText plus reserved xterm_ui_bytes/xterm_png_string fields for UI renderers.")]
    public async Task<string> GetTerminalSnapshot(
        [Description("Target terminal tab id. If omitted, uses the current tab when available.")] string? tabId = null)
    {
        try
        {
            var snapshot = await _terminalTools.CaptureSnapshotAsync(NullIfWhiteSpace(tabId));
            if (snapshot == null)
            {
                return "FAIL: No active terminal session was found for that tab.";
            }

            return JsonSerializer.Serialize(
                snapshot,
                AppJsonSerializerContext.Default.TerminalSnapshotResponse);
        }
        catch (Exception ex)
        {
            return $"FAIL: {ex.Message}";
        }
    }

    private static string FormatTerminal(TerminalTabStatusResponse terminal)
    {
        var state = terminal.HasActiveSession ? "active" : "empty";
        return $"- tab={terminal.TabId} state={state} session={terminal.SessionId ?? "none"} cli={terminal.Cli ?? "none"} cwd={terminal.WorkingDirectory ?? "unknown"}";
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
