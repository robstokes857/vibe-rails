namespace VibeRails.Services.VCA;

/// <summary>Restricts rule-file operations to ordinary vc.rules.md files in the current repository.</summary>
internal static class RuleFilePath
{
    public static string Resolve(string root, string path)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(path)
            || !Path.IsPathFullyQualified(path) || path.Any(char.IsControl)
            || path.StartsWith(@"\\", StringComparison.Ordinal))
            throw new ArgumentException("Choose an absolute vc.rules.md path in this repository.");

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullPath = Path.GetFullPath(path);
        var rootPrefix = Path.EndsInDirectorySeparator(rootFull) ? rootFull : rootFull + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootPrefix, comparison)
            || !Path.GetFileName(fullPath).Equals("vc.rules.md", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose a vc.rules.md file in this repository.");

        // Check every component, including the repository's ancestors and the final file.
        // A lexical containment check alone follows a junction/symlink outside the repository.
        for (FileSystemInfo? entry = new FileInfo(fullPath); entry is not null;
             entry = entry is FileInfo file ? file.Directory : ((DirectoryInfo)entry).Parent)
        {
            if (entry.Name.Equals(".git", comparison)
                || (OperatingSystem.IsWindows() && entry.FullName != Path.GetPathRoot(entry.FullName) && (entry.Name.Contains(':')
                    || entry.Name.EndsWith('.') || entry.Name.EndsWith(' '))))
                throw new ArgumentException("Rule files cannot use Git metadata or ambiguous path components.");
            entry.Refresh();
            if (entry.LinkTarget is not null
                || (entry.Exists && (entry.Attributes & FileAttributes.ReparsePoint) != 0))
                throw new ArgumentException("Linked rule files and directories are not supported.");
        }

        return fullPath;
    }
}
