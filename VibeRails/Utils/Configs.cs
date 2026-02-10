namespace VibeRails.Utils
{
    /// <summary>
    /// Parsed command line arguments
    /// </summary>
    public class ParsedArgs
    {
        // CLI command structure (e.g., "vb env list" → Command="env", SubCommand="list")
        public string? Command { get; set; }
        public string? SubCommand { get; set; }
        public string? Target { get; set; }  // e.g., environment name, agent path

        // LMBootstrap mode args
        public bool IsLMBootstrap { get; set; }
        public string? LMBootstrapCli { get; set; }
        public string? WorkDir { get; set; }
        public string[] ExtraArgs { get; set; } = [];

        // VCA Validation mode args
        public bool ValidateVca { get; set; }
        public bool PreCommit { get; set; }
        public bool Staged { get; set; }
        public string? CommitMsgFile { get; set; }
        public string? AgentPath { get; set; }

        // Hook management args
        public bool InstallHook { get; set; }
        public bool UninstallHook { get; set; }

        // Environment command options
        public string? Cli { get; set; }       // --cli claude/codex/gemini
        public string? Args { get; set; }      // --args "custom arguments"
        public string? Prompt { get; set; }    // --prompt "custom prompt"

        // Agent/Rules command options
        public string? Rule { get; set; }      // --rule "rule text"
        public string? Level { get; set; }     // --level WARN/COMMIT/STOP
        public string? Rules { get; set; }     // --rules "rule1,rule2" (comma-separated)
        public string? Name { get; set; }      // --name "custom name"

        // Gemini settings options
        public string? Theme { get; set; }     // --theme Default/Dark/Light
        public bool? Sandbox { get; set; }     // --sandbox / --no-sandbox
        public bool? AutoApprove { get; set; } // --auto-approve / --no-auto-approve
        public bool? VimMode { get; set; }     // --vim / --no-vim
        public bool? CheckUpdates { get; set; } // --check-updates / --no-check-updates
        public bool? Yolo { get; set; }        // --yolo / --no-yolo

        // Codex settings options
        public string? Model { get; set; }         // --model "o3"
        public string? CodexSandbox { get; set; }  // --codex-sandbox read-only|workspace-write|danger-full-access
        public string? CodexApproval { get; set; } // --codex-approval untrusted|on-failure|on-request|never
        public bool? FullAuto { get; set; }        // --full-auto / --no-full-auto
        public bool? Search { get; set; }          // --search / --no-search

        // Claude settings options
        public string? ClaudePermissionMode { get; set; }   // --permission-mode default|plan|bypassPermissions
        public string? ClaudeAllowedTools { get; set; }     // --allowed-tools "tool1,tool2"
        public string? ClaudeDisallowedTools { get; set; }  // --disallowed-tools "tool1,tool2"
        public bool? ClaudeSkipPermissions { get; set; }    // --skip-permissions / --no-skip-permissions

        // Output options
        public bool Verbose { get; set; }      // --verbose

        // Help/Version
        public bool Help { get; set; }
        public bool Version { get; set; }
    }

    public static class Configs
    {
        private static bool _localContext = false;
        private static string _rootPath = string.Empty;
        private static string _historyPath = string.Empty;
        private static string _envPath = string.Empty;
        private static string _configPath = string.Empty;
        private static string _statePath = string.Empty;
        private static ParsedArgs _args = new();

        // Known commands that indicate CLI mode
        private static readonly HashSet<string> KnownCommands = new(StringComparer.OrdinalIgnoreCase)
        {
            "env", "agent", "rules", "validate", "hooks", "launch", "gemini", "codex", "claude", "update", "help"
        };

        /// <summary>
        /// Parse command line arguments and store in Configs
        /// </summary>
        public static ParsedArgs ParseArgs(string[] args)
        {
            var parsed = new ParsedArgs();
            var positionalArgs = new List<string>();

            for (int i = 0; i < args.Length; i++)
            {
                var arg = args[i];

                // Handle flags and options
                if (arg.StartsWith("--") || arg.StartsWith("-"))
                {
                    switch (arg)
                    {
                        case "--lmbootstrap":
                        case "--environment":
                        case "--env":
                            parsed.IsLMBootstrap = true;
                            if (i + 1 < args.Length && !args[i + 1].StartsWith("-"))
                            {
                                parsed.LMBootstrapCli = args[++i];
                            }
                            break;

                        case "--workdir":
                        case "--dir":
                            if (i + 1 < args.Length)
                            {
                                parsed.WorkDir = args[++i];
                            }
                            break;

                        case "--help":
                        case "-h":
                            parsed.Help = true;
                            break;

                        case "--version":
                        case "-v":
                            parsed.Version = true;
                            break;

                        case "--validate-vca":
                            parsed.ValidateVca = true;
                            break;

                        case "--pre-commit":
                            parsed.PreCommit = true;
                            break;

                        case "--staged":
                            parsed.Staged = true;
                            break;

                        case "--commit-msg":
                            if (i + 1 < args.Length)
                            {
                                parsed.CommitMsgFile = args[++i];
                            }
                            break;

                        case "--install-hook":
                            parsed.InstallHook = true;
                            break;

                        case "--uninstall-hook":
                            parsed.UninstallHook = true;
                            break;

                        case "--cli":
                            if (i + 1 < args.Length)
                            {
                                parsed.Cli = args[++i];
                            }
                            break;

                        case "--args":
                            if (i + 1 < args.Length)
                            {
                                parsed.Args = args[++i];
                            }
                            break;

                        case "--prompt":
                            if (i + 1 < args.Length)
                            {
                                parsed.Prompt = args[++i];
                            }
                            break;

                        case "--rule":
                            if (i + 1 < args.Length)
                            {
                                parsed.Rule = args[++i];
                            }
                            break;

                        case "--level":
                            if (i + 1 < args.Length)
                            {
                                parsed.Level = args[++i];
                            }
                            break;

                        case "--rules":
                            if (i + 1 < args.Length)
                            {
                                parsed.Rules = args[++i];
                            }
                            break;

                        case "--name":
                            if (i + 1 < args.Length)
                            {
                                parsed.Name = args[++i];
                            }
                            break;

                        case "--agent":
                            if (i + 1 < args.Length)
                            {
                                parsed.AgentPath = args[++i];
                            }
                            break;

                        case "--verbose":
                            parsed.Verbose = true;
                            break;

                        // Gemini settings flags
                        case "--theme":
                            if (i + 1 < args.Length)
                            {
                                parsed.Theme = args[++i];
                            }
                            break;

                        case "--sandbox":
                            parsed.Sandbox = true;
                            break;

                        case "--no-sandbox":
                            parsed.Sandbox = false;
                            break;

                        case "--auto-approve":
                            parsed.AutoApprove = true;
                            break;

                        case "--no-auto-approve":
                            parsed.AutoApprove = false;
                            break;

                        case "--vim":
                            parsed.VimMode = true;
                            break;

                        case "--no-vim":
                            parsed.VimMode = false;
                            break;

                        case "--check-updates":
                            parsed.CheckUpdates = true;
                            break;

                        case "--no-check-updates":
                            parsed.CheckUpdates = false;
                            break;

                        case "--yolo":
                            parsed.Yolo = true;
                            break;

                        case "--no-yolo":
                            parsed.Yolo = false;
                            break;

                        // Codex settings flags
                        case "--model":
                            if (i + 1 < args.Length)
                            {
                                parsed.Model = args[++i];
                            }
                            break;

                        case "--codex-sandbox":
                            if (i + 1 < args.Length)
                            {
                                parsed.CodexSandbox = args[++i];
                            }
                            break;

                        case "--codex-approval":
                            if (i + 1 < args.Length)
                            {
                                parsed.CodexApproval = args[++i];
                            }
                            break;

                        case "--full-auto":
                            parsed.FullAuto = true;
                            break;

                        case "--no-full-auto":
                            parsed.FullAuto = false;
                            break;

                        case "--search":
                            parsed.Search = true;
                            break;

                        case "--no-search":
                            parsed.Search = false;
                            break;

                        // Claude settings flags
                        case "--permission-mode":
                            if (i + 1 < args.Length)
                            {
                                parsed.ClaudePermissionMode = args[++i];
                            }
                            break;

                        case "--allowed-tools":
                            if (i + 1 < args.Length)
                            {
                                parsed.ClaudeAllowedTools = args[++i];
                            }
                            break;

                        case "--disallowed-tools":
                            if (i + 1 < args.Length)
                            {
                                parsed.ClaudeDisallowedTools = args[++i];
                            }
                            break;

                        case "--skip-permissions":
                            parsed.ClaudeSkipPermissions = true;
                            break;

                        case "--no-skip-permissions":
                            parsed.ClaudeSkipPermissions = false;
                            break;

                        case "--":
                            // Everything after -- is extra args
                            parsed.ExtraArgs = args.Skip(i + 1).ToArray();
                            i = args.Length; // Exit loop
                            break;
                    }
                }
                else
                {
                    // Positional argument
                    positionalArgs.Add(arg);
                }
            }

            // Process positional arguments: command, subcommand, target
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

            _args = parsed;
            return parsed;
        }

        /// <summary>
        /// Get the parsed arguments
        /// </summary>
        public static ParsedArgs GetAarguments() => _args;

        public static string GetHistoryPath()
        {
            return _historyPath;
        }
        public static void SetHistoryPath(string path)
        {
            _historyPath = path;
        }
        public static string GetEnvPath()
        {
            return _envPath;
        }
        public static void SetEnvPath(string path)
        {
            _envPath = path;
        }
        public static string GetConfigPath()
        {
            return _configPath;
        }
        public static void SetConfigPath(string path)
        {
            _configPath = path;
        }

        public static string GetStatePath()
        {
            return _statePath;
        }
        public static void SetStatePath(string path)
        {
            _statePath = path;
        }

        public static bool IsLocalContext()
        {
            return _localContext;
        }
        public static void SetLocalContext(bool value)
        {
            _localContext = value;
        }
        public static string GetRootPath()
        {
            return _rootPath;
        }
        public static void SetRootPath(string path)
        {
            _rootPath = path;
        }

        private static bool _remoteAccess = false;
        private static string _apiKey = string.Empty;

        public static bool GetRemoteAccess()
        {
            return _remoteAccess;
        }
        public static void SetRemoteAccess(bool value)
        {
            _remoteAccess = value;
        }
        public static string GetApiKey()
        {
            return _apiKey;
        }
        public static void SetApiKey(string value)
        {
            _apiKey = value;
        }

    }
}
