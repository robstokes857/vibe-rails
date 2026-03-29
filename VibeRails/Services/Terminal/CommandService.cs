using VibeRails.DTOs;
using VibeRails.Services.LlmClis;
using static VibeRails.Utils.ShellArgSanitizer;

namespace VibeRails.Services.Terminal;

public sealed record PreparedTerminalSession(
    string Command,
    string LaunchCommand,
    IReadOnlyList<string> SetupCommands,
    Dictionary<string, string> Environment);

public interface ICommandService
{
    /// <summary>
    /// Build the CLI command string and environment dictionary.
    /// Shared by both CLI and Web paths.
    /// </summary>
    PreparedTerminalSession PrepareSession(
        LLM llm, string? envName, string[]? extraArgs, string? initialPrompt = null, string summary = "");
}

public class CommandService : ICommandService
{
    private readonly LlmCliEnvironmentService _envService;
    private readonly McpSettings _mcpSettings;

    public CommandService(LlmCliEnvironmentService envService, McpSettings mcpSettings)
    {
        _envService = envService;
        _mcpSettings = mcpSettings;
    }

    public PreparedTerminalSession PrepareSession(
        LLM llm, string? envName, string[]? extraArgs, string? initialPrompt = null, string summary = "")
    {
        _ = initialPrompt;

        var cli = llm.ToString().ToLower();
        var cliCommand = extraArgs?.Length > 0
            ? $"{cli} {BuildSafeArgString(extraArgs)}"
            : cli;

        if (!string.IsNullOrEmpty(summary))
        {
            // Defense-in-depth: sanitize even user-edited text from the frontend.
            // Strip control chars, collapse newlines, enforce length limit.
            summary = new string(summary
                .Where(c => !char.IsControl(c) || c == ' ' || c == '\n' || c == '\r')
                .ToArray())
                .Replace("\r\n", " ")
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Trim();
            if (summary.Length > 6000)
                summary = summary[..6000];

            var resumePrompt = $"This is a summary of our previous conversation, I am ready to resume this discussion: {summary}";
            var quoted = ShellQuoteLiteral(resumePrompt);

            cliCommand = llm switch
            {
                LLM.Copilot => $"{cliCommand} --interactive={quoted}",
                _ => $"{cliCommand} {quoted}"
            };
        }

        var builder = new ShellCommandBuilder()
            .SetLaunchCommand(cliCommand);
        var setupCommands = new List<string>();



        // Register MCP server before launch
        if (!string.IsNullOrEmpty(_mcpSettings.ServerPath) && File.Exists(_mcpSettings.ServerPath))
        {
            var mcpSetup = llm switch
            {
                LLM.Claude => $"claude mcp add viberails-mcp \"{_mcpSettings.ServerPath}\"",
                LLM.Codex => $"codex mcp add viberails-mcp -- \"{_mcpSettings.ServerPath}\"",
                LLM.Gemini => $"gemini mcp add --scope user viberails-mcp \"{_mcpSettings.ServerPath}\"",
                _ => null
            };

            if (mcpSetup != null)
            {
                builder.AddSetup(mcpSetup);
                setupCommands.Add(mcpSetup);
                // Clear screen to hide MCP setup messages (e.g., "already added" warnings)
                //builder.AddSetup("clear");
            }
        }

        var environment = new Dictionary<string, string>
        {
            ["LANG"] = "en_US.UTF-8",
            ["LC_ALL"] = "en_US.UTF-8",
            ["PYTHONIOENCODING"] = "utf-8"
        };

        if (!string.IsNullOrEmpty(envName))
        {
            var envVars = _envService.GetEnvironmentVariables(envName, llm);
            foreach (var kvp in envVars)
                environment[kvp.Key] = kvp.Value;
        }

        return new PreparedTerminalSession(
            builder.Build(),
            cliCommand,
            setupCommands.AsReadOnly(),
            environment);
    }

    /// <summary>
    /// Wraps text in shell single quotes so it is treated as a literal string
    /// with zero interpretation. Works for both bash and pwsh.
    ///   bash:  only ' needs escaping → end quote, escaped quote, reopen: '\''
    ///   pwsh:  only ' needs escaping → doubled: ''
    /// </summary>
    private static string ShellQuoteLiteral(string text)
    {
        if (OperatingSystem.IsWindows())
            return "'" + text.Replace("'", "''") + "'";

        return "'" + text.Replace("'", "'\\''") + "'";
    }
}
