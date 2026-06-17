using Serilog;
using VibeRails.DTOs;
using VibeRails.Services.LlmClis;
using static VibeRails.Utils.ShellArgSanitizer;


namespace VibeRails.Services.Terminal;

public sealed record PreparedTerminalSession(
    string Command,
    string LaunchCommand,
    IReadOnlyList<string> SetupCommands,
    Dictionary<string, string> Environment);

public class CommandService : ICommandService
{
    private readonly LlmCliEnvironmentService _envService;
    private static int _fakeCliWarningEmitted;

    public CommandService(LlmCliEnvironmentService envService)
    {
        _envService = envService;
    }

    public PreparedTerminalSession PrepareSession(
        LLM llm, string? envName, string[]? extraArgs, string? initialPrompt = null, string summary = "")
    {
        if (Environment.GetEnvironmentVariable("VIBERAILS_TEST_FAKE_CLI") == "1")
        {
            if (System.Threading.Interlocked.Exchange(ref _fakeCliWarningEmitted, 1) == 0)
            {
                Log.Warning(
                    "[Test] VIBERAILS_TEST_FAKE_CLI is active — every CLI session will be a portable echo+sleep fake. This MUST NOT be set in production.");
            }

            // `echo` + `sleep N` are valid in pwsh (aliases of Write-Output / Start-Sleep)
            // and the Unix-like shells we spawn, so the same command runs the
            // PTY+WS+xterm path without needing a real LLM CLI installed in CI.
            var fakeCmd = $"echo VIBERAILS_FAKE_CLI_READY:{llm}; sleep 600";
            var fakeEnv = new Dictionary<string, string>
            {
                ["LANG"] = "en_US.UTF-8",
                ["LC_ALL"] = "en_US.UTF-8",
                ["PYTHONIOENCODING"] = "utf-8"
            };
            return new PreparedTerminalSession(fakeCmd, fakeCmd, Array.Empty<string>(), fakeEnv);
        }

        // Plain shell: no agent, no custom environment, no prompt/args. The PTY already
        // spawns the OS default shell, so we hand back an empty command — TerminalRunner
        // skips SendCommandAsync and the shell simply sits at its prompt. Only the base
        // locale env vars apply.
        if (llm == LLM.Shell)
        {
            var shellEnv = new Dictionary<string, string>
            {
                ["LANG"] = "en_US.UTF-8",
                ["LC_ALL"] = "en_US.UTF-8",
                ["PYTHONIOENCODING"] = "utf-8"
            };
            return new PreparedTerminalSession(string.Empty, string.Empty, Array.Empty<string>(), shellEnv);
        }

        // Every CLI's enum name lowercased is its executable — except Antigravity, whose
        // binary is `agy`. Map it explicitly so the in-app PTY launches the right command.
        var cli = llm switch
        {
            LLM.Antigravity => "agy",
            _ => llm.ToString().ToLower()
        };
        var cliCommand = extraArgs?.Length > 0
            ? $"{cli} {BuildSafeArgString(extraArgs)}"
            : cli;

        var prompt = !string.IsNullOrWhiteSpace(summary)
            ? summary
            : initialPrompt;

        if (!string.IsNullOrWhiteSpace(prompt))
        {
            var quoted = SafeShellArg(prompt);

            cliCommand = llm switch
            {
                LLM.Copilot => $"{cliCommand} --interactive={quoted}",
                LLM.Antigravity => $"{cliCommand} --prompt-interactive={quoted}",
                _ => $"{cliCommand} {quoted}"
            };
        }

        var builder = new ShellCommandBuilder()
            .SetLaunchCommand(cliCommand);
        var setupCommands = new List<string>();

        // MCP tools are exposed two ways (see Services/Mcp/AGENTS.md): in-process HTTP at /mcp
        // (for the dashboard Explorer) and a spawnable stdio server, `vb mcp` (for CLIs — no port,
        // no auth). Per-CLI auto-registration is intentionally not wired here yet, pending further
        // validation. When enabled, register the stdio server per CLI, e.g.:
        //   claude mcp add viberails -- "{Environment.ProcessPath}" mcp
        //   codex  mcp add viberails -- "{Environment.ProcessPath}" mcp

        var environment = new Dictionary<string, string>
        {
            ["LANG"] = "en_US.UTF-8",
            ["LC_ALL"] = "en_US.UTF-8",
            ["PYTHONIOENCODING"] = "utf-8"
        };

        // LLM-specific env vars (e.g. Claude's CLAUDE_CODE_FORCE_SYNC_OUTPUT) live in the
        // env service so all per-LLM injection has one home. These apply with or without a
        // custom environment.
        foreach (var kvp in _envService.GetBaseEnvironmentVariables(llm))
            environment[kvp.Key] = kvp.Value;

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
