using Serilog;
using TokenSaver;
using VibeRails.Services.LlmClis;
using VibeRails.Services.LlmProxy;
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
    private readonly ILocalLlmProxyContext _llmProxyContext;
    private readonly ILlmProxySettingsService _llmProxySettings;
    private const string VibeRailsMcpServerName = "viberails-mcp";
    private static int _fakeCliWarningEmitted;

    /// <summary>
    /// CLIs into which VibeRails registers its MCP server.
    /// </summary>
    public static readonly IReadOnlyList<LLM> McpClis = new[]
    {
        LLM.Claude, LLM.Codex, LLM.Antigravity, LLM.Copilot
    };

    public CommandService(
        LlmCliEnvironmentService envService,
        ILocalLlmProxyContext llmProxyContext,
        ILlmProxySettingsService llmProxySettings)
    {
        _envService = envService;
        _llmProxyContext = llmProxyContext;
        _llmProxySettings = llmProxySettings;
    }

    public Task<PreparedTerminalSession> PrepareSessionAsync(
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
            return Task.FromResult(new PreparedTerminalSession(fakeCmd, fakeCmd, Array.Empty<string>(), fakeEnv));
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
            return Task.FromResult(new PreparedTerminalSession(string.Empty, string.Empty, Array.Empty<string>(), shellEnv));
        }

        // Every CLI's enum name lowercased is its executable — except Antigravity, whose
        // binary is `agy`. Map it explicitly so the in-app PTY launches the right command.
        var cli = llm switch
        {
            LLM.Antigravity => "agy",
            _ => llm.ToString().ToLower()
        };
        // One fresh snapshot for the whole prepare so the enabled/mode fields can't tear across a
        // concurrent settings save and the launch hits the disk once, not once per field.
        var proxySettings = _llmProxySettings.GetSettings();
        var launchArgs = BuildLaunchArgs(llm, extraArgs, proxySettings);
        var cliCommand = launchArgs.Length > 0
            ? $"{cli} {BuildSafeArgString(launchArgs)}"
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
                // OpenCode's TUI treats a positional arg as the [project] path, not a prompt,
                // so the initial prompt must ride on --prompt (never the default branch).
                LLM.OpenCode => $"{cliCommand} --prompt={quoted}",
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
        var mcpSetupCommands = BuildMcpSetupCommands(llm);
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

        // Codex reads proxy auth headers from environment variables named in its generated
        // provider config. Use process-local credentials here; the VIBERAILS_TOOL_* variables
        // added by TerminalRunner intentionally continue to target the root agent-tool API.
        if (ShouldUseCodexProxy(llm, extraArgs, proxySettings))
        {
            environment[LocalLlmProxyContext.SessionTokenVariable] = _llmProxyContext.SessionToken;
            environment[LocalLlmProxyContext.TabTokenVariable] = _llmProxyContext.TabToken;
        }

        // If Claude proxying is enabled separately, route Claude Code's Anthropic API traffic
        // through the local LLM proxy. Keep it independent from Codex's subscription/API setting:
        // Claude may use API tokens while Codex uses a ChatGPT subscription.
        // Skip if the selected environment already points Claude somewhere (base URL OR custom
        // headers): respect the user's explicit Anthropic config instead of clobbering it — the
        // proxy owns both variables together, so a partial overwrite would drop their headers.
        if (proxySettings.ClaudeLlmProxyEnabled
            && llm == LLM.Claude
            && !environment.ContainsKey(LlmProxyClaudeConfig.BaseUrlVariable)
            && !environment.ContainsKey(LlmProxyClaudeConfig.CustomHeadersVariable))
        {
            var claudeProxyEnv = LlmProxyClaudeConfig.BuildClaudeProxyEnvironment(
                _llmProxyContext.ApiBaseUrl,
                _llmProxyContext.SessionToken,
                _llmProxyContext.TabToken);
            foreach (var kvp in claudeProxyEnv)
                environment[kvp.Key] = kvp.Value;
        }

        LogMcpSetup(llm, envName, setupCommands);

        return Task.FromResult(new PreparedTerminalSession(
            builder.Build(),
            cliCommand,
            setupCommands.AsReadOnly(),
            environment));
    }

    private string[] BuildLaunchArgs(LLM llm, string[]? extraArgs, LlmProxySettings proxySettings)
    {
        if (!ShouldUseCodexProxy(llm, extraArgs, proxySettings))
            return extraArgs ?? [];

        var proxyArgs = LlmProxyCodexConfig.BuildCodexProxyArgs(
            _llmProxyContext.ApiBaseUrl,
            proxySettings.CodexLlmProxyMode,
            LocalLlmProxyContext.SessionTokenVariable,
            LocalLlmProxyContext.TabTokenVariable);
        if (extraArgs is not { Length: > 0 })
            return proxyArgs;

        var merged = new string[proxyArgs.Length + extraArgs.Length];
        proxyArgs.CopyTo(merged, 0);
        extraArgs.CopyTo(merged, proxyArgs.Length);
        return merged;
    }

    private static bool ShouldUseCodexProxy(
        LLM llm,
        string[]? extraArgs,
        LlmProxySettings proxySettings) =>
        proxySettings.CodexLlmProxyEnabled
        && llm == LLM.Codex
        && !ShouldSkipCodexProxy(extraArgs);

    private static bool ShouldSkipCodexProxy(string[]? extraArgs)
    {
        if (extraArgs is not { Length: > 0 })
            return false;

        for (var i = 0; i < extraArgs.Length; i++)
        {
            var arg = extraArgs[i];
            if (arg.Equals("--oss", StringComparison.Ordinal)
                || arg.Equals("--local-provider", StringComparison.Ordinal)
                || arg.StartsWith("--local-provider=", StringComparison.Ordinal))
            {
                return true;
            }

            // A Codex profile can set its own model_provider, and profile config outranks a
            // prepended -c, so never force the proxy when the user selected one. Covers spaced,
            // =-form, and glued short options (--profile, --profile=x, -p, -p x, -px).
            if (arg.Equals("--profile", StringComparison.Ordinal)
                || arg.StartsWith("--profile=", StringComparison.Ordinal)
                || arg.Equals("-p", StringComparison.Ordinal)
                || (arg.StartsWith("-p", StringComparison.Ordinal) && arg.Length > 2))
            {
                return true;
            }

            if ((arg.Equals("-c", StringComparison.Ordinal) || arg.Equals("--config", StringComparison.Ordinal))
                && i + 1 < extraArgs.Length
                && IsCodexProviderOverride(extraArgs[i + 1]))
            {
                return true;
            }

            if ((arg.StartsWith("-c=", StringComparison.Ordinal) || arg.StartsWith("--config=", StringComparison.Ordinal))
                && IsCodexProviderOverride(arg[(arg.IndexOf('=') + 1)..]))
            {
                return true;
            }

            // Glued short form: -c<key>=<value> (e.g. -cmodel_provider="x").
            if (arg.StartsWith("-c", StringComparison.Ordinal)
                && arg.Length > 2
                && arg[2] != '='
                && IsCodexProviderOverride(arg[2..]))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsCodexProviderOverride(string configArg)
    {
        var trimmed = configArg.AsSpan().TrimStart();
        return trimmed.StartsWith("model_provider", StringComparison.Ordinal)
            || trimmed.StartsWith("model_providers.", StringComparison.Ordinal)
            || trimmed.StartsWith("openai_base_url", StringComparison.Ordinal)
            || trimmed.StartsWith("chatgpt_base_url", StringComparison.Ordinal);
    }

    /// <summary>
    /// Returns the MCP setup commands for this launch. Registration is always on for supported
    /// CLIs; remove-first repairs stale registrations and add restores the current stdio server.
    /// </summary>
    private static IReadOnlyList<string> BuildMcpSetupCommands(LLM llm)
    {
        var (removeCommand, addCommand) = GetMcpCommands(llm);
        if (removeCommand is null)
        {
            // This CLI doesn't support MCP registration — nothing to add or clean up.
            return [];
        }

        return [removeCommand, addCommand!];
    }

    // Returns the (remove, add) command pair for a CLI, or (null, null) if the CLI has no
    // MCP registration support. Keep the supported set in sync with <see cref="McpClis"/>.
    private static (string? remove, string? add) GetMcpCommands(LLM llm)
    {
        var serverCommand = BuildSafeArgString(ResolveMcpServerCommandParts());
        return llm switch
        {
            LLM.Claude => (
                SuppressCommandOutput($"claude mcp remove {VibeRailsMcpServerName}"),
                $"claude mcp add --scope user {VibeRailsMcpServerName} -- {serverCommand}"),
            LLM.Codex => (
                SuppressCommandOutput($"codex mcp remove {VibeRailsMcpServerName}"),
                $"codex mcp add {VibeRailsMcpServerName} -- {serverCommand}"),
            LLM.Antigravity => (
                SuppressCommandOutput($"agy mcp remove {VibeRailsMcpServerName}"),
                $"agy mcp add {VibeRailsMcpServerName} -- {serverCommand}"),
            LLM.Copilot => (
                SuppressCommandOutput($"copilot mcp remove {VibeRailsMcpServerName}"),
                $"copilot mcp add {VibeRailsMcpServerName} -- {serverCommand}"),
            _ => (null, null)
        };
    }

    private static string SuppressCommandOutput(string command)
    {
        return OperatingSystem.IsWindows()
            ? $"{command} *> $null"
            : $"{command} >/dev/null 2>&1";
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

    private static void LogMcpSetup(LLM llm, string? envName, IReadOnlyList<string> setupCommands)
    {
        if (!McpClis.Contains(llm))
        {
            Log.Information("[MCP] Registration skipped for unsupported terminal type {Cli}", llm);
            return;
        }

        Log.Information(
            "[MCP] Registration setup prepared. cli={Cli} env={Env} commandCount={CommandCount} commands={Commands}",
            llm,
            string.IsNullOrWhiteSpace(envName) ? "(base)" : envName,
            setupCommands.Count,
            string.Join(" | ", setupCommands));
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
