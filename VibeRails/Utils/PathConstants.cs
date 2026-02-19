namespace VibeRails.Utils
{
    /// <summary>
    /// Centralized constants for all VibeRails directory and file paths.
    /// This is the single source of truth for path references across the codebase.
    /// </summary>
    public static class PathConstants
    {
        // Primary installation directory name
        // This can be overridden via appsettings.json's "VibeRails:InstallDirName" property
        public const string DEFAULT_INSTALL_DIR_NAME = ".vibe_rails";

        // Subdirectories within the installation directory
        public const string ENVS_SUBDIR = "envs";
        public const string SANDBOXES_SUBDIR = "sandboxes";
        public const string HISTORY_SUBDIR = "history";
        public const string VECTOR_SUBDIR = "vector";

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

        /// <summary>
        /// Get the full path to the VibeRails installation directory.
        /// Returns: ~/[DEFAULT_INSTALL_DIR_NAME] (e.g., ~/.vibe_rails/)
        /// </summary>
        public static string GetInstallDirPath()
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, DEFAULT_INSTALL_DIR_NAME);
        }
    }
}
