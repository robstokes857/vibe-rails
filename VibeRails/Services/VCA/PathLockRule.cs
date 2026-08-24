namespace VibeRails.Services.VCA;

public enum PathLockKind
{
    File,
    Directory
}

public sealed record PathLockSpecification(PathLockKind Kind, string RelativePath);

/// <summary>
/// Syntax, resolution, and matching for VCA's parameterized path-lock rules.
/// Paths are relative to the directory containing the declaring vc.rules.md and may not escape it.
/// </summary>
public static class PathLockRule
{
    public const string FileTemplate = "File Lock('path to file')";
    public const string DirectoryTemplate = "Directory Lock('path to directory')";

    private const string FilePrefix = "File Lock";
    private const string DirectoryPrefix = "Directory Lock";

    public static bool LooksLikePathLock(string? ruleText)
    {
        var trimmed = ruleText?.TrimStart();
        return trimmed?.StartsWith(FilePrefix, StringComparison.OrdinalIgnoreCase) == true
            || trimmed?.StartsWith(DirectoryPrefix, StringComparison.OrdinalIgnoreCase) == true;
    }

    public static bool TryParse(string? ruleText, out PathLockSpecification specification)
    {
        if (TryParse(ruleText, FilePrefix, PathLockKind.File, out specification))
        {
            return true;
        }

        return TryParse(ruleText, DirectoryPrefix, PathLockKind.Directory, out specification);
    }

    public static bool TryResolveRepositoryPath(
        PathLockSpecification specification,
        string sourceAgentFile,
        string repositoryRoot,
        out string repositoryRelativePath,
        out string error)
    {
        repositoryRelativePath = string.Empty;
        error = string.Empty;

        var requestedPath = specification.RelativePath.Trim();
        if (requestedPath.Length == 0)
        {
            error = "the lock path is empty";
            return false;
        }

        if (IsAbsoluteOnAnySupportedPlatform(requestedPath))
        {
            error = "the lock path must be relative to the declaring vc.rules.md";
            return false;
        }

        try
        {
            var root = Path.GetFullPath(repositoryRoot);
            var source = Path.GetFullPath(sourceAgentFile);
            var sourceDirectory = Path.GetDirectoryName(source) ?? root;
            var localPath = requestedPath
                .Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar);
            var target = Path.GetFullPath(Path.Combine(sourceDirectory, localPath));

            var relativeToSource = Path.GetRelativePath(sourceDirectory, target);
            if (EscapesBase(relativeToSource))
            {
                error = "the lock path may not escape the declaring vc.rules.md directory";
                return false;
            }

            var relativeToRoot = Path.GetRelativePath(root, target);
            if (EscapesBase(relativeToRoot))
            {
                error = "the resolved lock path is outside the repository";
                return false;
            }

            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (specification.Kind == PathLockKind.File
                && target.Equals(source, comparison))
            {
                error = "a File Lock cannot target its own declaring vc.rules.md";
                return false;
            }

            repositoryRelativePath = NormalizeGitPath(relativeToRoot);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"the lock path is invalid: {exception.Message}";
            return false;
        }
    }

    public static bool Matches(
        PathLockSpecification specification,
        string lockedRepositoryPath,
        string changedRepositoryPath,
        string? previousRepositoryPath,
        string sourceAgentRepositoryPath,
        StringComparison comparison)
    {
        var sourcePath = NormalizeGitPath(sourceAgentRepositoryPath);
        var currentPath = NormalizeGitPath(changedRepositoryPath);
        var previousPath = string.IsNullOrWhiteSpace(previousRepositoryPath)
            ? null
            : NormalizeGitPath(previousRepositoryPath);

        // The policy file must remain editable, especially for STOP locks. Other vc.rules.md files
        // inside a locked directory are ordinary content and remain protected.
        var currentMatches = !currentPath.Equals(sourcePath, comparison)
            && MatchesPath(specification.Kind, lockedRepositoryPath, currentPath, comparison);
        var previousMatches = previousPath is not null
            && !previousPath.Equals(sourcePath, comparison)
            && MatchesPath(specification.Kind, lockedRepositoryPath, previousPath, comparison);
        return currentMatches || previousMatches;
    }

    public static string NormalizeGitPath(string path)
    {
        var normalized = path.Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return normalized.TrimEnd('/');
    }

    private static bool TryParse(
        string? ruleText,
        string prefix,
        PathLockKind kind,
        out PathLockSpecification specification)
    {
        specification = new PathLockSpecification(kind, string.Empty);
        if (string.IsNullOrWhiteSpace(ruleText))
        {
            return false;
        }

        // A rule occupies exactly one line of vc.rules.md. Text carrying a line break is written back
        // as two lines, and a second line starting with '#' closes the rules section — which
        // silently unenforces every rule below it in both the Git hook and the Rules page, because
        // they share a reader. Refused here rather than only at the API boundary so the writer and
        // the hook agree that such a lock does not parse at all.
        if (ruleText.AsSpan().ContainsAny('\r', '\n', '\0'))
        {
            return false;
        }

        var text = ruleText.Trim();
        if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var remainder = text[prefix.Length..].Trim();
        if (remainder.Length < 4 || remainder[0] != '(' || remainder[^1] != ')')
        {
            return false;
        }

        var argument = remainder[1..^1].Trim();
        if (argument.Length < 2 || argument[0] is not ('\'' or '"') || argument[^1] != argument[0])
        {
            return false;
        }

        var path = argument[1..^1].Trim();
        if (path.Length == 0)
        {
            return false;
        }

        specification = new PathLockSpecification(kind, path);
        return true;
    }

    private static bool MatchesPath(
        PathLockKind kind,
        string lockedRepositoryPath,
        string changedRepositoryPath,
        StringComparison comparison)
    {
        var lockedPath = NormalizeGitPath(lockedRepositoryPath);

        // An empty locked path is never a legitimate resolution — it can only come from a lock
        // that failed to resolve. Matching everything on it is how one malformed rule turns into
        // a violation on every file of every commit, so it matches nothing instead. "." is
        // different: that is a real Directory Lock('.') on the declaring directory.
        if (lockedPath.Length == 0)
        {
            return false;
        }

        if (kind == PathLockKind.File)
        {
            return changedRepositoryPath.Equals(lockedPath, comparison);
        }

        if (lockedPath == ".")
        {
            return true;
        }

        return changedRepositoryPath.Equals(lockedPath, comparison)
            || changedRepositoryPath.StartsWith(lockedPath + "/", comparison);
    }

    private static bool IsAbsoluteOnAnySupportedPlatform(string path) =>
        Path.IsPathRooted(path)
        || path.StartsWith('/')
        || path.StartsWith('\\')
        || (path.Length >= 3
            && char.IsLetter(path[0])
            && path[1] == ':'
            && path[2] is '/' or '\\');

    private static bool EscapesBase(string relativePath)
    {
        var normalized = NormalizeGitPath(relativePath);
        return normalized.Equals("..", StringComparison.Ordinal)
            || normalized.StartsWith("../", StringComparison.Ordinal)
            || Path.IsPathRooted(relativePath);
    }
}
