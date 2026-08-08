namespace VibeRails.Utils
{
    /// <summary>
    /// Parsed command line arguments
    /// </summary>
    public class ParsedArgs
    {
        // LMBootstrap mode (vb --env <llm|name> --workdir <path> [-- <extra-args>])
        public bool IsLMBootstrap { get; set; }
        public string? LMBootstrapCli { get; set; }
        public string? WorkDir { get; set; }
        public string[] ExtraArgs { get; set; } = [];

        // Automated Job run: vb --env <worker> --workdir <repo> --job-run <runId> [--max-runtime <min>].
        // Runs the worker's env exactly as configured, interactively, in whatever terminal window
        // spawned this process, while reporting the run's lifecycle back to the JobRuns table.
        public string? JobRunId { get; set; }

        // Opt-in absolute deadline in minutes for a --job-run process. Absent (null) means the run
        // lives until the CLI exits or the user closes the window — that is the default.
        // `--job-tick` is not parsed here. It is a retired scheduler argument retained only as an
        // inert compatibility tombstone and exits before the web host is built.
        public int? MaxRuntimeMinutes { get; set; }

        // VS Code extension / parent->child terminal-tab spawn (vb --vs-code-v1 [--parent-pid <pid>])
        public bool IsVsCodeMode { get; set; }

        // Web dashboard (vb / vb --web)
        public bool OpenBrowser { get; set; }
        public bool ShutdownOnBrowserClose { get; set; }

        // Focused Git Guard preflight dashboard (vb --git-guard)
        public bool IsGitGuardMode { get; set; }

        // Top-level
        public bool Help { get; set; }
        public bool Version { get; set; }
    }

    public static class ParserConfigs
    {
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

        public static string GetRootPath()
        {
            return _rootPath;
        }

        private static string? _gitBranch;
        private static string? _gitRemoteUrl;

        public static string? GetGitBranch() => _gitBranch;
        public static void SetGitBranch(string? value) => _gitBranch = value;
        public static string? GetGitRemoteUrl() => _gitRemoteUrl;
        public static void SetGitRemoteUrl(string? value) => _gitRemoteUrl = value;

        // Whether the current root is an actual git repository. Decoupled from RootPath:
        // RootPath is always populated (git root when in a repo, otherwise the launch
        // directory), while this flag is the single source of truth for git-presence.
        private static bool _isInGit = false;
        public static bool GetIsInGit() => _isInGit;

        /// <summary>
        /// Applies a git-detection result as one grouped update so request threads are far
        /// less likely to observe a torn (RootPath, IsInGit) pair. This is the single place
        /// that mutates the related git fields — keep Init, ProjectCacheRefreshJob and the
        /// git/init route routed through here so the logic can't drift into stale copies.
        /// RootPath is always set to a usable working directory (git root when in a repo,
        /// otherwise the launch/fallback dir); IsInGit is written last so a reader that
        /// gates on it will already see the matching RootPath. (Single-user process — not a
        /// full memory barrier; add a lock here if this ever needs strict atomicity.)
        /// </summary>
        public static void SetGitState(string rootPath, bool isInGit, string? gitBranch = null, string? gitRemoteUrl = null)
        {
            _rootPath = rootPath;
            _gitBranch = gitBranch;
            _gitRemoteUrl = gitRemoteUrl;
            _isInGit = isInGit;
        }

        private static bool _remoteAccess = false;
        private static bool _routeThroughVibeRailsAi = false;
        private static string _apiKey = string.Empty;
        private static bool _useVsCodeTheme = false;
        private static string _frontendUrl = string.Empty;

        public static bool GetRemoteAccess()
        {
            return _remoteAccess;
        }
        public static void SetRemoteAccess(bool value)
        {
            _remoteAccess = value;
        }
        public static bool GetRouteThroughVibeRailsAi()
        {
            return _routeThroughVibeRailsAi;
        }
        public static void SetRouteThroughVibeRailsAi(bool value)
        {
            _routeThroughVibeRailsAi = value;
        }
        public static string GetApiKey()
        {
            return _apiKey;
        }
        public static void SetApiKey(string value)
        {
            _apiKey = value;
        }
        public static bool GetUseVsCodeTheme()
        {
            return _useVsCodeTheme;
        }
        public static void SetUseVsCodeTheme(bool value)
        {
            _useVsCodeTheme = value;
        }
        public static bool GetMcpEnabled()
        {
            return true;
        }
        public static void SetMcpEnabled(bool value)
        {
            _ = value;
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
