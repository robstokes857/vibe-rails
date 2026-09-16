namespace VibeRails.Utils
{
    /// <summary>
    /// Centralized constants for all VibeRails directory and file paths, and the one policy that
    /// decides which data directory a process opens.
    ///
    /// The policy exists because every vb.exe on a machine -- the shipped extension, a Debug build
    /// started with F5, a test package, an agent's MCP server -- used to open the same
    /// ~/.vibe_rails. On 2026-09-16 that let a branch build migrate the production database while
    /// the shipped binary was still writing to it. Now:
    ///   1. VIBE_RAILS_HOME, when set, is the data directory. Always wins.
    ///   2. Otherwise a Debug build uses ~/.vibe_rails_dev unless VIBE_RAILS_ALLOW_PROD_DATA=1.
    ///   3. Otherwise (Release) the directory is ~/.vibe_rails.
    /// </summary>
    public static class PathConstants
    {
        public static string GetStateFilePath()
        {
            return Path.Combine(GetInstallDirPath(), STATE_FILENAME);
        }

        // Primary installation directory name. The documented "VibeRails:InstallDirName" setting
        // still renames it; the default name resolves through the policy above.
        public const string DEFAULT_INSTALL_DIR_NAME = ".vibe_rails";

        /// <summary>Where Debug builds keep their data unless told otherwise. Never the shipped extension's directory.</summary>
        public const string DEVELOPMENT_INSTALL_DIR_NAME = ".vibe_rails_dev";

        /// <summary>Absolute path that replaces the data directory for this process. Wins over everything else.</summary>
        public const string HOME_ENVIRONMENT_VARIABLE = "VIBE_RAILS_HOME";

        /// <summary>Set to 1 to let a Debug build open the production directory deliberately.</summary>
        public const string ALLOW_PRODUCTION_DATA_ENVIRONMENT_VARIABLE = "VIBE_RAILS_ALLOW_PROD_DATA";

        // Subdirectories within the installation directory
        public const string ENVS_SUBDIR = "envs";
        public const string SANDBOXES_SUBDIR = "sandboxes";
        public const string HISTORY_SUBDIR = "history";
        public const string MODELS_SUBDIR = "models";
        public const string VECTOR_SUBDIR = "vector";
        public const string JOB_WORKSPACES_SUBDIR = "job-workspaces";

        // File names
        public const string CONFIG_FILENAME = "config.json";
        public const string STATE_FILENAME = "state.db";
        public const string SETTINGS_FILENAME = "settings.json";
        public const string LOG_SUBDIR = "log";
        public const string MCP_LOG_SUBDIR = "mcp";
        public const string MCP_LOG_FILENAME = "mcp-server.log";

        // Vector database file names
        public const string USER_TERMS_FILENAME = "user_terms.jsonl";
        public const string CONVERSATION_HISTORY_FILENAME = "conversation_history.jsonl";

        // A field rather than a const so the compiler does not fold the policy branches away
        // (and warn about the unreachable half) in whichever configuration is being built.
#if DEBUG
        public static readonly bool IsDebugBuild = true;
#else
        public static readonly bool IsDebugBuild = false;
#endif

        /// <summary>The VIBE_RAILS_HOME override as an absolute path, or null when unset.</summary>
        public static string? ExplicitHome
        {
            get
            {
                var value = Environment.GetEnvironmentVariable(HOME_ENVIRONMENT_VARIABLE);
                return string.IsNullOrWhiteSpace(value) ? null : Path.GetFullPath(value.Trim());
            }
        }

        public static bool ProductionDataAllowed =>
            IsTruthy(Environment.GetEnvironmentVariable(ALLOW_PRODUCTION_DATA_ENVIRONMENT_VARIABLE));

        /// <summary>True when this process is a Debug build kept away from ~/.vibe_rails by policy.</summary>
        public static bool UsesDevelopmentDirectory => ExplicitHome is null && IsDebugBuild && !ProductionDataAllowed;

        /// <summary>
        /// Get the full path to the VibeRails installation (data) directory for this process.
        /// Returns VIBE_RAILS_HOME, else ~/.vibe_rails_dev for Debug builds, else ~/.vibe_rails.
        /// </summary>
        public static string GetInstallDirPath()
        {
            if (ExplicitHome is { } home)
                return home;
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(profile, UsesDevelopmentDirectory ? DEVELOPMENT_INSTALL_DIR_NAME : DEFAULT_INSTALL_DIR_NAME);
        }

        /// <summary>One line for the startup log saying why this process opened the directory it did.</summary>
        public static string DescribeDataDirectoryPolicy()
        {
            if (ExplicitHome is not null)
                return HOME_ENVIRONMENT_VARIABLE + " is set";
            if (UsesDevelopmentDirectory)
                return $"Debug build, development directory (set {ALLOW_PRODUCTION_DATA_ENVIRONMENT_VARIABLE}=1 to use {DEFAULT_INSTALL_DIR_NAME})";
            if (IsDebugBuild)
                return $"Debug build, production directory ({ALLOW_PRODUCTION_DATA_ENVIRONMENT_VARIABLE}=1)";
            return "Release build, production directory";
        }

        private static bool IsTruthy(string? value) =>
            value is not null && value.Trim().ToLowerInvariant() is "1" or "true" or "yes";
    }
}
