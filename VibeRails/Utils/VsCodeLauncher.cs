using System.Diagnostics;

namespace VibeRails.Utils;

internal static class VsCodeLauncher
{
    internal static ProcessStartInfo CreateStartInfo(string projectDirectory, string? filePath,
        string? executable = null)
    {
        var root = Path.GetFullPath(projectDirectory);
        string? target = null;
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            if (filePath.StartsWith("\\\\", StringComparison.Ordinal)
                || filePath.StartsWith("//", StringComparison.Ordinal))
                throw new ArgumentException("Choose a local file in this project.");

            target = Path.GetFullPath(filePath, root);
            var relative = Path.GetRelativePath(root, target);
            if (Path.IsPathRooted(relative) || relative == ".."
                || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || !File.Exists(target))
                throw new ArgumentException("Choose an existing file in this project.");

            // A link under the project must not turn this file-opening action into an
            // arbitrary outside-project target. Check every component, including the root.
            for (var current = target; current != null; current = Path.GetDirectoryName(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new ArgumentException("Linked paths cannot be opened from this action.");
                if (Path.GetRelativePath(root, current) == ".") break;
            }
        }

        var info = new ProcessStartInfo
        {
            FileName = executable ?? ResolveExecutable(),
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        // The browser opens a workspace with the requested file. An embedded VS Code
        // webview uses its extension bridge to open a tab in the current window instead.
        info.ArgumentList.Add("--new-window");
        info.ArgumentList.Add(root);
        if (target != null)
        {
            info.ArgumentList.Add("--goto");
            info.ArgumentList.Add(target);
        }
        info.Environment.Remove("ELECTRON_RUN_AS_NODE");
        return info;
    }

    private static string ResolveExecutable()
    {
        if (!OperatingSystem.IsWindows()) return "code";

        // Invoke the application directly: passing filenames through code.cmd would
        // introduce cmd.exe interpretation of otherwise valid filename characters.
        var searchDirectories = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var directory in searchDirectories)
        {
            var bin = directory.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(bin)) continue;
            var direct = Path.Combine(bin, "Code.exe");
            if (File.Exists(direct)) return direct;
            if (File.Exists(Path.Combine(bin, "code.cmd")))
            {
                var application = Path.GetFullPath(Path.Combine(bin, "..", "Code.exe"));
                if (File.Exists(application)) return application;
            }
        }
        throw new InvalidOperationException("VS Code was not found. Install it with the 'code' command in PATH.");
    }
}
