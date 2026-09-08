namespace VibeRails.Services.VCA;

/// <summary>Lists on-disk scope without letting nested policies override their parents.</summary>
internal static class RuleFileScope
{
    public static List<string> ListFiles(string sourceFile, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = new FileInfo(Path.GetFullPath(sourceFile));
        if (!source.Exists || !source.Name.Equals("vc.rules.md", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose an existing vc.rules.md file.");
        if (IsLink(source))
            throw new ArgumentException("Linked rule files cannot be used to list scope.");

        var scope = source.Directory!;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        // Check the starting path too: a requested file can itself sit beneath a linked directory.
        for (var parent = scope; parent is not null; parent = parent.Parent)
        {
            if (IsLink(parent) || parent.Name.Equals(".git", comparison))
                throw new ArgumentException("Choose a rule file outside linked directories and Git metadata.");
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            AttributesToSkip = 0,
            IgnoreInaccessible = false
        };
        var pending = new Stack<DirectoryInfo>();
        pending.Push(scope);
        var files = new List<string>();
        while (pending.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsLink(directory)) continue;
            foreach (var entry in directory.EnumerateFileSystemInfos("*", options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                // .git is a directory in a normal checkout and a file in linked worktrees.
                if (entry.Name.Equals(".git", comparison) || IsLink(entry)) continue;
                if (entry is DirectoryInfo child)
                {
                    pending.Push(child);
                }
                else if (entry is FileInfo && !entry.FullName.Equals(source.FullName, comparison))
                {
                    files.Add(Path.GetRelativePath(scope.FullName, entry.FullName).Replace('\\', '/'));
                }
            }
        }

        files.Sort(StringComparer.Ordinal);
        return files;
    }

    private static bool IsLink(FileSystemInfo entry)
    {
        entry.Refresh();
        return entry.LinkTarget is not null || (entry.Attributes & FileAttributes.ReparsePoint) != 0;
    }
}
