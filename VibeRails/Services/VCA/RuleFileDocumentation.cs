namespace VibeRails.Services.VCA;

/// <summary>Identifies the policy file that is implicit in its own change documentation.</summary>
internal static class RuleFileDocumentation
{
    public static bool IsDeclaringFile(string changedPath, string sourceFile, string rootPath)
    {
        var root = Path.GetFullPath(rootPath);
        var changed = Path.GetFullPath(changedPath.Replace('\\', '/'), root);
        var source = Path.GetFullPath(sourceFile.Replace('\\', '/'), root);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        // Exempt only the file declaring this rule. A parent policy still governs nested rule
        // files, and an unrelated file named vc.rules.md is still ordinary changed content.
        return changed.Equals(source, comparison);
    }
}
