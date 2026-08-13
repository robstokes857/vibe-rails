using Serilog;
using TokenSaver;
using VibeRails.Services.Environments;
using VibeRails.Services.LlmClis;
using VibeRails.Services.LlmProxy;
using VibeRails.Utils;
using static VibeRails.Utils.ShellArgSanitizer;

namespace VibeRails.Services.Terminal;

/// <param name="Command">
/// The full shell program (setup commands + launch) to type into an interactive PTY shell.
/// </param>
/// <param name="Executable">
/// The CLI's bare executable name, for callers that spawn it as the PTY's own process instead of
/// typing <paramref name="Command"/> into a shell. Null when there is no CLI to spawn — a plain
/// Shell session, or the fake-CLI test harness, both of which are shell programs rather than a
/// program plus arguments. Callers must fall back to the shell path when this is null.
/// </param>
/// <param name="Argv">
/// <paramref name="Executable"/>'s arguments as real argv, with the initial prompt appended by the
/// same per-CLI convention every other launch path uses. Unlike <paramref name="Command"/> this
/// carries no shell quoting, so it survives prompts containing quotes and metacharacters.
/// </param>
public sealed record PreparedTerminalSession(
    string Command,
    string LaunchCommand,
    IReadOnlyList<string> SetupCommands,
    Dictionary<string, string> Environment,
    bool OpenCodeProxyActive = false,
    string? Executable = null,
    IReadOnlyList<string>? Argv = null);

public class CommandService : ICommandService
{
    private readonly LlmCliEnvironmentService _envService;
    private readonly ILocalLlmProxyContext _llmProxyContext;
    private readonly ILlmProxySettingsService _llmProxySettings;
    private readonly ILlmProxySessionState _llmProxySessionState;
    private const string VibeRailsMcpServerName = "viberails-mcp";

    /// <summary>
    /// Ceiling on the assembled launch command on Windows. CreateProcess rejects a command line
    /// over 32,767 chars, and the shell this gets typed into hits that limit when it execs the
    /// CLI — surfacing as "The filename or extension is too long", which points at nothing the
    /// user can act on. The margin leaves room for the shell's own wrapping.
    /// </summary>
    private const int MaxWindowsCommandChars = 32_000;

    private static int _fakeCliWarningEmitted;

    /// <summary>
    /// CLIs into which VibeRails registers its MCP server.
    /// </summary>
    public static readonly IReadOnlyList<LLM> McpClis = new[]
    {
        LLM.Claude,
        LLM.Codex,
        LLM.Antigravity,
        LLM.Copilot,
        LLM.OpenCode,
        LLM.Glm52
    };

    public CommandService(
        LlmCliEnvironmentService envService,
        ILocalLlmProxyContext llmProxyContext,
        ILlmProxySettingsService llmProxySettings,
        ILlmProxySessionState llmProxySessionState)
    {
        _envService = envService;
        _llmProxyContext = llmProxyContext;
        _llmProxySettings = llmProxySettings;
        _llmProxySessionState = llmProxySessionState;
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

        var cli = ResolveCliExecutable(llm);
        // The Glm52 pseudo-CLI is OpenCode with a pinned --model flag. For base CLI
        // launches (no envName) inject it here so every base launch pins the right model.
        // Custom environments already carry --model in their CustomArgs (built by the env
        // settings form), so we skip injection when an env is selected to avoid a duplicate.
        if (string.IsNullOrEmpty(envName))
        {
            extraArgs = llm switch
            {
                LLM.Glm52 => WithPinnedModel(extraArgs, "zai/glm-5.2"),
                _ => extraArgs
            };
        }
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
                // Glm52 is OpenCode under the hood and shares the --prompt convention.
                LLM.OpenCode or LLM.Glm52 => $"{cliCommand} --prompt={quoted}",
                _ => $"{cliCommand} {quoted}"
            };

            // Checked after escaping, not before: every ` " $ in the prompt doubles on Windows and
            // every ' quadruples on POSIX, so a message that passed the resolver's char limit can
            // still assemble into a command the OS refuses.
            if (OperatingSystem.IsWindows() && cliCommand.Length > MaxWindowsCommandChars)
                throw PromptTooLongException.ForCommandLine(cliCommand.Length, MaxWindowsCommandChars);
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
            AddProxyContactDetails(environment, LlmProxyProvider.Codex);
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
            AddProxyContactDetails(environment, LlmProxyProvider.Claude);
        }

        // If OpenCode proxying is enabled, route OpenCode's zai (Z.AI/GLM) provider traffic through
        // the local LLM proxy. Like the Claude path this is env-var-only — no opencode.json is
        // written: OPENCODE_CONFIG_CONTENT carries an inline JSON override of the zai provider's
        // baseURL + auth headers (see LlmProxyZaiConfig). Skip if the caller already set
        // OPENCODE_CONFIG_CONTENT, so an explicit value is respected rather than clobbered.
        // Glm52 is included because it is the zai provider model.
        var inheritedOpenCodeConfig = Environment.GetEnvironmentVariable(
            LlmProxyZaiConfig.ConfigContentVariable);
        var openCodeProxyActive = proxySettings.OpenCodeLlmProxyLaunchEnabled
            && (llm == LLM.OpenCode || llm == LLM.Glm52)
            && !environment.ContainsKey(LlmProxyZaiConfig.ConfigContentVariable)
            && string.IsNullOrEmpty(inheritedOpenCodeConfig);
        if (openCodeProxyActive)
        {
            AddProxyContactDetails(environment, LlmProxyProvider.OpenCode);
            environment[LlmProxyZaiConfig.ConfigContentVariable] = LlmProxyZaiConfig.BuildOpencodeConfigContent(
                _llmProxyContext.ApiBaseUrl,
                _llmProxyContext.SessionToken,
                _llmProxyContext.TabToken);
        }

        LogMcpSetup(llm, envName, setupCommands);

        // The same launch expressed as argv rather than a shell string. Built unconditionally
        // because it is cheap and side-effect free; whether it gets used is the caller's call.
        var directArgv = new List<string>(launchArgs);
        LlmPromptArgvBuilder.AppendInitialPrompt(directArgv, llm, prompt);

        return Task.FromResult(new PreparedTerminalSession(
            builder.Build(),
            cliCommand,
            setupCommands.AsReadOnly(),
            environment,
            OpenCodeProxyActive: openCodeProxyActive,
            Executable: cli,
            Argv: directArgv));
    }

    /// <summary>
    /// Stamps the session environment with everything needed to <b>call</b> this process's LLM
    /// proxy: where it is, and the two tokens it demands. Set identically by all three provider
    /// blocks, because the consumer is not the CLI — it is whatever the CLI spawns and hands its
    /// environment to (today, the <c>vb mcp</c> server behind the token-saver pause tools). Those
    /// children can't read Codex's <c>--config</c> args or parse OpenCode's config JSON, so the
    /// contact details have to be stated the same way regardless of which CLI is launching.
    ///
    /// Tokens ride environment variables rather than arguments precisely so they never appear in a
    /// process command line. The base URL is this process's own loopback address, and consumers
    /// re-check that before attaching the tokens to a request
    /// (<see cref="LlmProxyBaseUrl.TryNormalizeLoopback"/>) rather than trusting the variable to
    /// still say what was written here.
    ///
    /// <paramref name="provider"/> is recorded rather than derived later because this is the only
    /// place that knows it: the proxy child hosts one tab's relay, and questions like "is the saver
    /// on for this session" have no answer without knowing which provider's toggle to read.
    /// </summary>
    private void AddProxyContactDetails(
        Dictionary<string, string> environment,
        LlmProxyProvider provider)
    {
        environment[LocalLlmProxyContext.BaseUrlVariable] = _llmProxyContext.ApiBaseUrl;
        environment[LocalLlmProxyContext.SessionTokenVariable] = _llmProxyContext.SessionToken;
        environment[LocalLlmProxyContext.TabTokenVariable] = _llmProxyContext.TabToken;
        _llmProxySessionState.RecordProxiedLaunch(provider);
    }

    /// <summary>
    /// Every CLI's enum name lowercased is its executable — except Antigravity (binary <c>agy</c>)
    /// and the OpenCode-backed pseudo-CLI Glm52 (binary <c>opencode</c>).
    ///
    /// Must stay in step with <c>IBaseLlmCliLauncher.CliExecutable</c>, which is the same mapping
    /// expressed per-launcher for the native-terminal path. The two agree today; Antigravity is the
    /// one that bites, because its enum, its env name, and its binary are all different.
    /// </summary>
    public static string ResolveCliExecutable(LLM llm) => llm switch
    {
        LLM.Antigravity => "agy",
        LLM.Glm52 => "opencode",
        _ => llm.ToString().ToLower()
    };

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

    /// <summary>
    /// Prepends a <c>--model=&lt;model&gt;</c> arg to <paramref name="extraArgs"/>. Used by the
    /// OpenCode-backed pseudo-CLI Glm52 to pin its model on base CLI launches.
    /// Uses =-form so the arg survives ShellArgSanitizer without re-quoting. It is prepended so a
    /// later caller-supplied <c>--model</c> retains yargs' last-value precedence.
    /// </summary>
    private static string[] WithPinnedModel(string[]? extraArgs, string model)
    {
        var modelArg = $"--model={model}";
        if (extraArgs is not { Length: > 0 })
            return [modelArg];

        var result = new string[extraArgs.Length + 1];
        result[0] = modelArg;
        extraArgs.CopyTo(result, 1);
        return result;
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
    /// CLIs. CLIs with a remove command use remove-first repair; OpenCode's idempotent add replaces
    /// the named entry directly.
    /// </summary>
    private static IReadOnlyList<string> BuildMcpSetupCommands(LLM llm)
    {
        var (removeCommand, addCommand) = GetMcpCommands(llm);
        if (addCommand is null)
        {
            // This CLI doesn't support MCP registration — nothing to add or clean up.
            return [];
        }

        return removeCommand is null
            ? [addCommand]
            : [removeCommand, addCommand];
    }

    // Returns the optional remove command and required add command for a supported CLI, or
    // (null, null) when unsupported. Keep the supported set in sync with <see cref="McpClis"/>.
    private static (string? remove, string? add) GetMcpCommands(LLM llm)
    {
        var serverCommand = BuildSafeArgString(ResolveMcpServerCommandParts());
        var openCodeExecutable = ResolveOpenCodeMcpExecutable();
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
            LLM.OpenCode or LLM.Glm52 => (
                null,
                $"{openCodeExecutable} mcp add {VibeRailsMcpServerName} -- {serverCommand}"),
            _ => (null, null)
        };
    }

    private static string ResolveOpenCodeMcpExecutable()
    {
        // PowerShell consumes the `--` separator when it invokes npm's opencode.ps1 shim, so the
        // local-command form silently falls back to help. The .cmd shim preserves argv exactly.
        return OperatingSystem.IsWindows() ? "opencode.cmd" : "opencode";
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
    ///
    /// <para>The cap matches <see cref="PromptPlaceholderService.MaxResolvedPromptChars"/>, so for
    /// an Initial Message — which the resolver has already refused above that length — truncation
    /// here can never fire. It still guards the paths that skip the resolver (a pasted job summary
    /// most of all), and those are exactly the ones where a silent cut would be invisible, hence
    /// the warning.</para>
    /// </summary>
    private static string SafeShellArg(string text, int maxLength = PromptPlaceholderService.MaxResolvedPromptChars)
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
        {
            Log.Warning(
                "[Terminal] Launch text truncated from {ActualChars} to {MaxChars} chars before quoting",
                clean.Length, maxLength);
            // Slicing by code unit would leave a lone surrogate at the cut, which the PTY writes
            // out as a replacement character.
            clean = TextTruncation.Truncate(clean, maxLength);
        }

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
