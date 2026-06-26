using Serilog;
using VibeRails.DTOs;
using VibeRails.Services.LlmClis;
using VibeRails.Utils;
using static VibeRails.Utils.ShellArgSanitizer;


namespace VibeRails.Services.Terminal;

public sealed record PreparedTerminalSession(
    string Command,
    string LaunchCommand,
    IReadOnlyList<string> SetupCommands,
    Dictionary<string, string> Environment,
    // Non-null ⇒ this launch carries the one-time opted-out `mcp remove` for this CLI, and the
    // caller MUST call ICommandService.RecordMcpRemovalIssuedAsync once the command has actually
    // been sent to the PTY. We deliberately do NOT record removal at construction time: if startup
    // fails before the send, cleanup must remain pending for the next launch (see TerminalRunner).
    LLM? McpRemovalToRecord = null);

public class CommandService : ICommandService
{
    private readonly LlmCliEnvironmentService _envService;
    private readonly IGlobalCache _globalCache;
    private const string VibeRailsMcpServerName = "viberails-mcp";
    private static int _fakeCliWarningEmitted;

    /// <summary>
    /// CLIs into which VibeRails registers its MCP server. Exposed so callers that need to
    /// reset MCP bookkeeping (e.g. the settings route on opt-in) iterate the same set.
    /// </summary>
    public static readonly IReadOnlyList<LLM> McpClis = new[]
    {
        LLM.Claude, LLM.Codex, LLM.Antigravity, LLM.Copilot
    };

    public CommandService(LlmCliEnvironmentService envService, IGlobalCache globalCache)
    {
        _envService = envService;
        _globalCache = globalCache;
    }

    public async Task<PreparedTerminalSession> PrepareSessionAsync(
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
        // no auth). Register it before launching managed CLIs; setup failures remain non-blocking.
        // This keeps custom environments isolated because the setup command runs inside the same
        // PTY environment as the agent launch (CLAUDE_CONFIG_DIR / CODEX_HOME already set below).
        var (mcpSetupCommands, mcpRemovalPending) = await BuildMcpSetupCommandsAsync(llm);
        foreach (var setupCommand in mcpSetupCommands)
        {
            setupCommands.Add(setupCommand);
            builder.AddSetup(setupCommand);
        }

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
            environment,
            mcpRemovalPending ? llm : null);
    }

    /// <summary>
    /// Returns the MCP setup commands for this launch and whether this launch carries the
    /// one-time opted-out `mcp remove` that the caller must record once it has been sent.
    /// </summary>
    private async Task<(IReadOnlyList<string> commands, bool removalPending)> BuildMcpSetupCommandsAsync(LLM llm)
    {
        var (removeCommand, addCommand) = GetMcpCommands(llm);
        if (removeCommand is null)
        {
            // This CLI doesn't support MCP registration — nothing to add or clean up.
            return ([], false);
        }

        // Opted IN: register the server. Remove-first repairs older installs that registered
        // the deleted standalone MCP_Server.exe. Failures are non-blocking because the shell
        // command chain uses ';' between setup steps and the final agent launch.
        if (ParserConfigs.GetMcpEnabled())
        {
            return ([removeCommand, addCommand!], false);
        }

        // Opted OUT (default): never add. Remove it once per CLI to clean up a registration we
        // added before. We must NOT record the removal here — the command has only been built,
        // not sent. If startup fails before TerminalRunner writes it to the PTY, recording now
        // would skip cleanup forever. Signal the pending removal; the caller records it only
        // after the command is actually sent. The record is reset on opt-in (AppSettingsRoutes).
        var removedFlagKey = GlobalCacheKeys.McpRemovedFromCli(llm);
        if (await _globalCache.GetAsBoolAsync(removedFlagKey))
        {
            return ([], false);
        }

        return ([removeCommand], true);
    }

    /// <inheritdoc />
    public Task RecordMcpRemovalIssuedAsync(LLM llm) =>
        _globalCache.SetAsync(GlobalCacheKeys.McpRemovedFromCli(llm), "true");

    // Returns the (remove, add) command pair for a CLI, or (null, null) if the CLI has no
    // MCP registration support. Keep the supported set in sync with <see cref="McpClis"/>.
    private static (string? remove, string? add) GetMcpCommands(LLM llm)
    {
        var serverCommand = BuildSafeArgString(ResolveMcpServerCommandParts());
        return llm switch
        {
            LLM.Claude => (
                $"claude mcp remove {VibeRailsMcpServerName}",
                $"claude mcp add --scope user {VibeRailsMcpServerName} -- {serverCommand}"),
            LLM.Codex => (
                $"codex mcp remove {VibeRailsMcpServerName}",
                $"codex mcp add {VibeRailsMcpServerName} -- {serverCommand}"),
            LLM.Antigravity => (
                $"agy mcp remove {VibeRailsMcpServerName}",
                $"agy mcp add {VibeRailsMcpServerName} -- {serverCommand}"),
            LLM.Copilot => (
                $"copilot mcp remove {VibeRailsMcpServerName}",
                $"copilot mcp add {VibeRailsMcpServerName} -- {serverCommand}"),
            _ => (null, null)
        };
    }

    private static string[] ResolveMcpServerCommandParts()
    {
        var processPath = Environment.ProcessPath;
        if (IsVibeRailsExecutable(processPath))
        {
            return [processPath!, "mcp"];
        }

        var appBaseDll = Path.Combine(AppContext.BaseDirectory, "vb.dll");
        if (File.Exists(appBaseDll))
        {
            return [ResolveDotNetHost(processPath), appBaseDll, "mcp"];
        }

        return ["vb", "mcp"];
    }

    private static bool IsVibeRailsExecutable(string? processPath)
    {
        if (string.IsNullOrWhiteSpace(processPath) || !File.Exists(processPath))
        {
            return false;
        }

        var fileName = Path.GetFileNameWithoutExtension(processPath);
        return fileName.Equals("vb", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveDotNetHost(string? processPath)
    {
        if (!string.IsNullOrWhiteSpace(processPath)
            && File.Exists(processPath)
            && Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return processPath;
        }

        return "dotnet";
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
