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

    public static class ParserConfigs
    {
        private static bool _localContext = false;
        private static string _rootPath = string.Empty;
        private static string _historyPath = string.Empty;
        private static string _envPath = string.Empty;
        private static string _sandboxPath = string.Empty;
        private static string _configPath = string.Empty;
        private static string _statePath = string.Empty;
        private static ParsedArgs _args = new();

        /// <summary>
        /// Parse command line arguments and store in Configs
        /// </summary>
        public static ParsedArgs ParseArgs(string[] args)
        {
            _args = ArgumentParser.Parse(args);
            return _args;
        }

        /// <summary>
        /// Get the parsed arguments
        /// </summary>
        public static ParsedArgs GetArguments() => _args;

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
        public static string GetSandboxPath()
        {
            return _sandboxPath;
        }
        public static void SetSandboxPath(string path)
        {
            _sandboxPath = path;
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
        private static string _frontendUrl = string.Empty;

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
        public static string GetFrontendUrl()
        {
            return _frontendUrl;
        }
        public static void SetFrontendUrl(string value)
        {
            _frontendUrl = value;
        }

    }
}
