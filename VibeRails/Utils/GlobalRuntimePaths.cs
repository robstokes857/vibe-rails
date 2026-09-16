namespace VibeRails.Utils;

/// <summary>
/// Initializes the process-wide paths shared by the dashboard, terminal children, and the lean
/// VBD host. This deliberately performs no Git detection, browser setup, hooks, or maintenance
/// registration.
/// </summary>
public static class GlobalRuntimePaths
{
    private const string EmptyJson = "{}";

    public static string Initialize(string? installDirectoryName = null)
    {
        var globalDirectory = ResolveGlobalDirectory(installDirectoryName);
        var environmentDirectory = Path.Combine(globalDirectory, PathConstants.ENVS_SUBDIR);
        var sandboxDirectory = Path.Combine(globalDirectory, PathConstants.SANDBOXES_SUBDIR);
        var historyDirectory = Path.Combine(globalDirectory, PathConstants.HISTORY_SUBDIR);
        var stateFile = Path.Combine(globalDirectory, PathConstants.STATE_FILENAME);
        var configFile = Path.Combine(globalDirectory, PathConstants.CONFIG_FILENAME);

        ParserConfigs.SetConfigPath(configFile);
        ParserConfigs.SetStatePath(stateFile);
        ParserConfigs.SetEnvPath(environmentDirectory);
        ParserConfigs.SetSandboxPath(sandboxDirectory);
        ParserConfigs.SetHistoryPath(historyDirectory);

        PrivateFilePermissions.EnsureDirectory(globalDirectory);
        PrivateFilePermissions.EnsureDirectory(environmentDirectory);
        PrivateFilePermissions.EnsureDirectory(sandboxDirectory);
        PrivateFilePermissions.EnsureDirectory(historyDirectory);
        if (!File.Exists(configFile))
            File.WriteAllText(configFile, EmptyJson);
        PrivateFilePermissions.EnsureFile(configFile);

        return globalDirectory;
    }

    /// <summary>
    /// The one normalization for the documented "VibeRails:InstallDirName" override, shared by
    /// Initialize and FileService.GetGlobalSavePath so they can never disagree. Path.Combine
    /// deliberately preserves the pre-VBD semantics this setting always had: a rooted value
    /// replaces the profile base entirely and a relative value may nest beneath it.
    /// (VBD registration separately requires the stable ~/.vibe_rails and reports Unavailable
    /// for custom locations; a custom location must not crash the dashboard at startup.)
    /// </summary>
    public static string ResolveGlobalDirectory(string? installDirectoryName)
    {
        var directoryName = string.IsNullOrWhiteSpace(installDirectoryName) ? null : installDirectoryName.Trim();

        // The default name means "the default location", and the default location is a policy
        // (VIBE_RAILS_HOME, a Debug build's development directory) rather than a fixed path. Only a
        // custom name keeps the historical profile-relative semantics, and VIBE_RAILS_HOME wins
        // even over that so one environment variable can redirect any build.
        if (directoryName is null
            || directoryName.Equals(PathConstants.DEFAULT_INSTALL_DIR_NAME, StringComparison.OrdinalIgnoreCase)
            || PathConstants.ExplicitHome is not null)
            return PathConstants.GetInstallDirPath();

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(profile))
            throw new InvalidOperationException("Unable to locate the current user's profile directory.");

        return Path.GetFullPath(Path.Combine(profile, directoryName));
    }
}
