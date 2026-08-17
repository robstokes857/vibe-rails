using VibeRails.Services;

namespace VibeRails.Services.VCA.Validators;

/// <summary>Legacy per-file validator for exact-file and recursive-directory locks.</summary>
public sealed class PathLockValidator(PathLockKind kind) : IRuleValidator
{
    public Rule SupportedRule => kind == PathLockKind.File
        ? Rule.FileLock
        : Rule.DirectoryLock;

    public Task<RuleValidationResult> ValidateAsync(
        string filePath,
        RuleWithEnforcement rule,
        string sourceFile,
        string rootPath,
        ValidationContext? context = null,
        CancellationToken ct = default)
    {
        // A lock that will not parse or resolve is inert, not violated: it has no path to compare
        // this file against, so reporting it invalid here would flag every file in every commit.
        if (!PathLockRule.TryParse(rule.RuleText, out var specification)
            || specification.Kind != kind)
        {
            return Task.FromResult(new RuleValidationResult(
                true,
                $"Path lock ignored - invalid {kind.ToString().ToLowerInvariant()} lock syntax"));
        }

        if (!PathLockRule.TryResolveRepositoryPath(
                specification,
                sourceFile,
                rootPath,
                out var lockedPath,
                out var error))
        {
            return Task.FromResult(new RuleValidationResult(true, $"Path lock ignored - {error}"));
        }

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var sourcePath = Path.GetRelativePath(rootPath, sourceFile).Replace('\\', '/');
        var isLocked = PathLockRule.Matches(
            specification,
            lockedPath,
            filePath,
            previousRepositoryPath: null,
            sourcePath,
            comparison);

        return Task.FromResult(isLocked
            ? new RuleValidationResult(false, $"Locked path changed: {filePath}")
            : new RuleValidationResult(true));
    }
}
