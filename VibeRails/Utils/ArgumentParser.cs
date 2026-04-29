namespace VibeRails.Utils
{
    /// <summary>
    /// Handles parsing of command-line arguments for the VibeRails CLI.
    /// Extracted from Configs.cs for better maintainability.
    /// </summary>
    public static class ArgumentParser
    {
        // Known commands that indicate CLI mode
        private static readonly HashSet<string> KnownCommands = new(StringComparer.OrdinalIgnoreCase)
        {
            "env", "agent", "rules", "validate", "hooks", "launch", "gemini", "codex", "claude", "update", "help"
        };

        /// <summary>
        /// Parse command line arguments into a ParsedArgs object
        /// </summary>
        public static ParsedArgs Parse(string[] args)
        {
            var parsed = new ParsedArgs();
            var positionalArgs = new List<string>();

            for (int i = 0; i < args.Length; i++)
            {
                var arg = args[i];

                // Handle flags and options
                if (arg.StartsWith("--") || arg.StartsWith("-"))
                {
                    i = ParseFlag(args, i, parsed);
                }
                else
                {
                    // Positional argument
                    positionalArgs.Add(arg);
                }
            }

            // Process positional arguments: command, subcommand, target
            ParsePositionalArgs(positionalArgs, parsed);

            return parsed;
        }

        private static int ParseFlag(string[] args, int index, ParsedArgs parsed)
        {
            var arg = args[index];

            // Try each category of flags
            if (TryParseEnvironmentFlag(arg, args, ref index, parsed)) return index;
            if (TryParseValidationFlag(arg, args, ref index, parsed)) return index;
            if (TryParseHookFlag(arg, args, ref index, parsed)) return index;
            if (TryParseCommandFlag(arg, args, ref index, parsed)) return index;
            if (TryParseRuleFlag(arg, args, ref index, parsed)) return index;
            if (TryParseGeminiFlag(arg, args, ref index, parsed)) return index;
            if (TryParseCodexFlag(arg, args, ref index, parsed)) return index;
            if (TryParseClaudeFlag(arg, args, ref index, parsed)) return index;
            if (TryParseGeneralFlag(arg, args, ref index, parsed)) return index;

            // Handle special case: everything after -- is extra args
            if (arg == "--")
            {
                parsed.ExtraArgs = args.Skip(index + 1).ToArray();
                return args.Length; // Exit loop
            }

            return index;
        }

        private static bool TryParseEnvironmentFlag(string arg, string[] args, ref int index, ParsedArgs parsed)
        {
            switch (arg)
            {
                case "--lmbootstrap":
                case "--environment":
                case "--env":
                    parsed.IsLMBootstrap = true;
                    if (index + 1 < args.Length && !args[index + 1].StartsWith("-"))
                    {
                        parsed.LMBootstrapCli = args[++index];
                    }
                    return true;

                case "--workdir":
                case "--dir":
                    if (index + 1 < args.Length)
                    {
                        parsed.WorkDir = args[++index];
                    }
                    return true;

                case "--cli":
                    if (index + 1 < args.Length)
                    {
                        parsed.Cli = args[++index];
                    }
                    return true;

                case "--args":
                    if (index + 1 < args.Length)
                    {
                        parsed.Args = args[++index];
                    }
                    return true;

                case "--prompt":
                    if (index + 1 < args.Length)
                    {
                        parsed.Prompt = args[++index];
                    }
                    return true;

                case "--make-remote":
                    parsed.MakeRemote = true;
                    return true;

                case "--vs-code-v1":
                    parsed.IsVsCodeMode = true;
                    return true;

                default:
                    return false;
            }
        }

        private static bool TryParseValidationFlag(string arg, string[] args, ref int index, ParsedArgs parsed)
        {
            switch (arg)
            {
                case "--validate-vca":
                    parsed.ValidateVca = true;
                    return true;

                case "--pre-commit":
                    parsed.PreCommit = true;
                    return true;

                case "--staged":
                    parsed.Staged = true;
                    return true;

                case "--commit-msg":
                    if (index + 1 < args.Length)
                    {
                        parsed.CommitMsgFile = args[++index];
                    }
                    return true;

                case "--agent":
                    if (index + 1 < args.Length)
                    {
                        parsed.AgentPath = args[++index];
                    }
                    return true;

                default:
                    return false;
            }
        }

        private static bool TryParseHookFlag(string arg, string[] args, ref int index, ParsedArgs parsed)
        {
            switch (arg)
            {
                case "--install-hook":
                    parsed.InstallHook = true;
                    return true;

                case "--uninstall-hook":
                    parsed.UninstallHook = true;
                    return true;

                default:
                    return false;
            }
        }

        private static bool TryParseCommandFlag(string arg, string[] args, ref int index, ParsedArgs parsed)
        {
            switch (arg)
            {
                case "--name":
                    if (index + 1 < args.Length)
                    {
                        parsed.Name = args[++index];
                    }
                    return true;

                default:
                    return false;
            }
        }

        private static bool TryParseRuleFlag(string arg, string[] args, ref int index, ParsedArgs parsed)
        {
            switch (arg)
            {
                case "--rule":
                    if (index + 1 < args.Length)
                    {
                        parsed.Rule = args[++index];
                    }
                    return true;

                case "--level":
                    if (index + 1 < args.Length)
                    {
                        parsed.Level = args[++index];
                    }
                    return true;

                case "--rules":
                    if (index + 1 < args.Length)
                    {
                        parsed.Rules = args[++index];
                    }
                    return true;

                default:
                    return false;
            }
        }

        private static bool TryParseGeminiFlag(string arg, string[] args, ref int index, ParsedArgs parsed)
        {
            switch (arg)
            {
                case "--theme":
                    if (index + 1 < args.Length)
                    {
                        parsed.Theme = args[++index];
                    }
                    return true;

                case "--sandbox":
                    parsed.Sandbox = true;
                    return true;

                case "--no-sandbox":
                    parsed.Sandbox = false;
                    return true;

                case "--auto-approve":
                    parsed.AutoApprove = true;
                    return true;

                case "--no-auto-approve":
                    parsed.AutoApprove = false;
                    return true;

                case "--vim":
                    parsed.VimMode = true;
                    return true;

                case "--no-vim":
                    parsed.VimMode = false;
                    return true;

                case "--check-updates":
                    parsed.CheckUpdates = true;
                    return true;

                case "--no-check-updates":
                    parsed.CheckUpdates = false;
                    return true;

                case "--yolo":
                    parsed.Yolo = true;
                    return true;

                case "--no-yolo":
                    parsed.Yolo = false;
                    return true;

                default:
                    return false;
            }
        }

        private static bool TryParseCodexFlag(string arg, string[] args, ref int index, ParsedArgs parsed)
        {
            switch (arg)
            {
                case "-a":
                case "--ask-for-approval":
                case "--codex-approval":
                    if (index + 1 < args.Length)
                    {
                        parsed.CodexApproval = args[++index];
                    }
                    return true;

                case "--full-auto":
                    parsed.FullAuto = true;
                    return true;

                case "--no-full-auto":
                    parsed.FullAuto = false;
                    return true;

                case "--no-alt-screen":
                    parsed.NoAltScreen = true;
                    return true;

                case "--alt-screen":
                    parsed.NoAltScreen = false;
                    return true;

                case "--oss":
                    parsed.Oss = true;
                    return true;

                case "--no-oss":
                    parsed.Oss = false;
                    return true;

                default:
                    return false;
            }
        }

        private static bool TryParseClaudeFlag(string arg, string[] args, ref int index, ParsedArgs parsed)
        {
            switch (arg)
            {
                case "--effort":
                    if (index + 1 < args.Length)
                    {
                        parsed.ClaudeEffort = args[++index];
                    }
                    return true;

                case "--no-session-persistence":
                    parsed.ClaudeNoSessionPersistence = true;
                    return true;

                case "--permission-mode":
                    if (index + 1 < args.Length)
                    {
                        parsed.ClaudePermissionMode = args[++index];
                    }
                    return true;

                case "--system-prompt":
                    if (index + 1 < args.Length)
                    {
                        parsed.ClaudeSystemPrompt = args[++index];
                    }
                    return true;

                case "--allow-dangerously-skip-permissions":
                    parsed.ClaudeAllowDangerouslySkipPermissions = true;
                    return true;

                case "--dangerously-load-development-channels":
                    if (index + 1 < args.Length)
                    {
                        parsed.ClaudeDangerouslyLoadDevelopmentChannels = args[++index];
                    }
                    return true;

                case "--dangerously-skip-permissions":
                    parsed.ClaudeDangerouslySkipPermissions = true;
                    return true;

                case "--allowedTools":
                case "--allowed-tools":
                    if (index + 1 < args.Length)
                    {
                        parsed.ClaudeAllowedTools = args[++index];
                    }
                    return true;

                case "--append-system-prompt":
                    if (index + 1 < args.Length)
                    {
                        parsed.ClaudeAppendSystemPrompt = args[++index];
                    }
                    return true;

                case "--bare":
                    parsed.ClaudeBare = true;
                    return true;

                case "--betas":
                    if (index + 1 < args.Length)
                    {
                        parsed.ClaudeBetas = args[++index];
                    }
                    return true;

                case "--channels":
                    if (index + 1 < args.Length)
                    {
                        parsed.ClaudeChannels = args[++index];
                    }
                    return true;

                case "--debug":
                    parsed.ClaudeDebug = true;
                    return true;

                case "--debug-filter":
                    if (index + 1 < args.Length)
                    {
                        parsed.ClaudeDebug = true;
                        parsed.ClaudeDebugFilter = args[++index];
                    }
                    return true;

                default:
                    if (arg.StartsWith("--debug=", StringComparison.Ordinal))
                    {
                        parsed.ClaudeDebug = true;
                        parsed.ClaudeDebugFilter = arg["--debug=".Length..];
                        return true;
                    }
                    return false;
            }
        }

        private static bool TryParseGeneralFlag(string arg, string[] args, ref int index, ParsedArgs parsed)
        {
            switch (arg)
            {
                case "--help":
                case "-h":
                    parsed.Help = true;
                    return true;

                case "--version":
                case "-v":
                    parsed.Version = true;
                    return true;

                case "--verbose":
                    parsed.Verbose = true;
                    return true;

                case "--trace":
                    parsed.Trace = true;
                    return true;

                case "--web":
                case "--launch-web":
                    parsed.OpenBrowser = true;
                    parsed.ShutdownOnBrowserClose = true;
                    return true;

                case "--open-browser":
                case "--launch-browser":
                    parsed.OpenBrowser = true;
                    return true;

                default:
                    return false;
            }
        }

        private static void ParsePositionalArgs(List<string> positionalArgs, ParsedArgs parsed)
        {
            if (positionalArgs.Count > 0)
            {
                var firstArg = positionalArgs[0];
                if (KnownCommands.Contains(firstArg))
                {
                    parsed.Command = firstArg;
                    if (positionalArgs.Count > 1)
                    {
                        parsed.SubCommand = positionalArgs[1];
                    }
                    if (positionalArgs.Count > 2)
                    {
                        parsed.Target = positionalArgs[2];
                    }
                }
            }
        }
    }
}
