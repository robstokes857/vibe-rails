namespace VibeRails.DB;

/// <summary>Path normalization and matching shared by persistence and callers.</summary>
public static class CodeAnalyzerIgnoreRules
{
    /// <summary>
    /// Case sensitivity for repository-relative path comparison, following host filesystem
    /// semantics: case-insensitive on Windows and macOS, case-sensitive on Linux. This mirrors how
    /// the checkout itself distinguishes (or conflates) src/Foo.cs and src/foo.cs.
    /// </summary>
    public static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    /// <summary>
    /// True if <paramref name="relativePath"/> is excluded by any of <paramref name="rules"/>.
    /// File rules match the exact path; directory rules match the directory path itself plus
    /// everything under it. Comparison follows host filesystem case semantics
    /// (<see cref="PathComparison"/>).
    /// </summary>
    public static bool IsIgnored(string relativePath, IReadOnlyList<CodeAnalyzerIgnoredFile> rules)
    {
        if (rules.Count == 0) return false;
        var normalized = NormalizePath(relativePath);
        for (var i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            var rulePath = NormalizePath(rule.Path);
            if (string.Equals(rulePath, normalized, PathComparison))
                return true;
            if (IsDirectoryMatchKind(rule.MatchKind)
                && normalized.StartsWith(rulePath + "/", PathComparison))
                return true;
        }
        return false;
    }

    private static bool IsDirectoryMatchKind(string? matchKind) =>
        string.Equals(matchKind, CodeAnalyzerIgnoreMatchKind.Directory, StringComparison.OrdinalIgnoreCase);

    /// <summary>Repository keys are absolute paths; normalize separators and trailing slash.</summary>
    public static string NormalizeRepository(string repositoryPath) =>
        System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(repositoryPath))
            .Replace('\\', '/');

    /// <summary>File keys are repository-relative, forward slashes, no leading "./".</summary>
    public static string NormalizePath(string path)
    {
        var normalized = path.Replace('\\', '/').Trim();
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        // Directory rules are stored without a trailing slash so they don't accidentally
        // match siblings via prefix logic. The matcher adds the "/" back when comparing.
        return normalized.TrimEnd('/');
    }

}
