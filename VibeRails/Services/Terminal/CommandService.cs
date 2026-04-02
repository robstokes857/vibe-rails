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
            var quoted = SafeShellArg(summary);

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
    /// Sanitizes and shell-quotes text so it can be safely embedded as a
    /// literal argument in a shell command. Strips control characters,
    /// collapses to one line, enforces a length limit, then wraps in
    /// platform-appropriate quotes.
    /// </summary>
    private static string SafeShellArg(string text, int maxLength = 6000)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "\"\"";

        var clean = text
            .Replace("\r\n", " ")
            .Replace("\r", " ")
            .Replace("\n", " ")
            // Normalize Unicode smart/curly quotes to ASCII equivalents
            .Replace("\u201c", "\"")  // "
            .Replace("\u201d", "\"")  // "
            .Replace("\u201e", "\"")  // „
            .Replace("\u2018", "'")   // '
            .Replace("\u2019", "'")   // '
            .Replace("\u201a", "'");  // ‚
        clean = new string(clean.Where(c => !char.IsControl(c) || c == ' ').ToArray()).Trim();

        if (clean.Length > maxLength)
            clean = clean[..maxLength];

        if (OperatingSystem.IsWindows())
        {
            var escaped = clean
                .Replace("`", "``")
                .Replace("\"", "`\"")
                .Replace("$", "`$");
            return "\"" + escaped + "\"";
        }

        return "'" + clean.Replace("'", "'\\''") + "'";
    }
}
