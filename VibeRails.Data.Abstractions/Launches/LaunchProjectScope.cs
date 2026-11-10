namespace VibeRails.Launches;

/// <summary>Local project identity shared by definition storage and launch admission.</summary>
public static class LaunchProjectScope
{
    public const string UnavailableReason = "This definition is not available in this project. Edit its Projects selection to use it here.";

    public static string Root(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    public static string Key(string path) => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? Root(path).ToUpperInvariant() : Root(path);

    /// <summary>Null means preserve on edit; an empty list explicitly selects all projects.</summary>
    public static string[]? Normalize(string[]? paths)
    {
        if (paths is null) return null;
        if (paths.Length > 1000) throw new LaunchValidationException("Select at most 1,000 projects.");
        try
        {
            if (paths.Any(p => string.IsNullOrWhiteSpace(p) || p.Length > 4096 || p.Any(char.IsControl) || !Path.IsPathFullyQualified(p)))
                throw new LaunchValidationException("Projects must be fully qualified local project paths.");
            return paths.Select(Key).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            throw new LaunchValidationException("A selected project path is invalid.");
        }
    }

    public static bool Includes(string[]? paths, string project) => paths is null || paths.Length == 0
        || paths.Any(path => string.Equals(Key(path), Key(project), StringComparison.Ordinal));

    public static void Require(string[]? paths, string project)
    {
        if (!Includes(paths, project)) throw new LaunchValidationException(UnavailableReason);
    }
}
