using VibeRails.DTOs;
using VibeRails.Services;

namespace VibeRails.Services.Jobs;

public interface IJobExecutableResolver
{
    string? Resolve(LLM llm);
    JobExecutable? Resolve(JobScriptRuntime runtime);
}

public sealed record JobExecutable(string Path, IReadOnlyList<string> PrefixArguments);

public sealed class JobExecutableResolver : IJobExecutableResolver
{
    public string? Resolve(LLM llm)
    {
        var command = llm switch
        {
            LLM.Codex => "codex",
            LLM.Claude => "claude",
            LLM.Antigravity => "agy",
            LLM.Copilot => "copilot",
            LLM.Grok46 => "grok",
            LLM.OpenCode or LLM.Glm52 or LLM.Glm53 => "opencode",
            _ => null
        };
        if (command is null)
            return null;

        foreach (var directory in CandidateDirectories())
        {
            foreach (var name in CandidateNames(command))
            {
                try
                {
                    var candidate = Path.GetFullPath(Path.Combine(directory, name));
                    if (File.Exists(candidate))
                        return candidate;
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    // Ignore malformed PATH entries and continue to the next candidate.
                }
            }
        }

        return null;
    }

    public JobExecutable? Resolve(JobScriptRuntime runtime)
    {
        if (runtime == JobScriptRuntime.Bash && OperatingSystem.IsWindows())
        {
            foreach (var candidate in WindowsGitBashCandidates())
            {
                if (File.Exists(candidate))
                    return new JobExecutable(candidate, []);
            }
        }

        var commands = runtime switch
        {
            JobScriptRuntime.PowerShell => new[] { "pwsh" },
            JobScriptRuntime.Bash => new[] { "bash" },
            JobScriptRuntime.Python when OperatingSystem.IsWindows() => new[] { "python", "python3", "py" },
            JobScriptRuntime.Python => new[] { "python3", "python" },
            _ => []
        };

        foreach (var command in commands)
        {
            var path = ResolveCommand(command);
            if (path is null)
                continue;

            // System32\bash.exe is the legacy WSL bridge. A Windows path handed to it has
            // different semantics from Git Bash and can silently execute in another filesystem.
            if (runtime == JobScriptRuntime.Bash
                && OperatingSystem.IsWindows()
                && path.Contains(@"\Windows\System32\", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var prefix = runtime == JobScriptRuntime.Python
                && Path.GetFileNameWithoutExtension(path).Equals("py", StringComparison.OrdinalIgnoreCase)
                    ? (IReadOnlyList<string>)["-3"]
                    : [];
            return new JobExecutable(path, prefix);
        }

        return null;
    }

    private static string? ResolveCommand(string command)
    {
        foreach (var directory in CandidateDirectories())
        {
            foreach (var name in CandidateNames(command))
            {
                try
                {
                    var candidate = Path.GetFullPath(Path.Combine(directory, name));
                    if (File.Exists(candidate))
                        return candidate;
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    // Ignore malformed PATH entries and continue to the next candidate.
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> CandidateDirectories()
    {
        var seen = new HashSet<string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var raw in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var directory = raw.Trim('"');
            if (directory.Length > 0 && seen.Add(directory))
                yield return directory;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var extras = OperatingSystem.IsWindows()
            ? new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"),
                Path.Combine(home, ".local", "bin")
            }
            : new[]
            {
                "/opt/homebrew/bin",
                "/usr/local/bin",
                "/usr/bin",
                Path.Combine(home, ".local", "bin"),
                Path.Combine(home, ".npm-global", "bin")
            };

        foreach (var directory in extras)
        {
            if (directory.Length > 0 && seen.Add(directory))
                yield return directory;
        }
    }

    private static IEnumerable<string> CandidateNames(string command)
    {
        if (!OperatingSystem.IsWindows())
        {
            yield return command;
            yield break;
        }

        // .cmd/.bat are intentionally excluded: they can only be launched through cmd.exe, whose
        // parser re-interprets the escaped argv and would let prompt text inject commands (see
        // JobRunExecutor.ConfigureScriptShim). Native installs ship a real .exe; npm/pnpm/yarn ship
        // a .ps1 shim that pwsh runs safely.
        yield return $"{command}.exe";
        yield return $"{command}.ps1";
        yield return command;
    }

    private static IEnumerable<string> WindowsGitBashCandidates()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
            yield return Path.Combine(programFiles, "Git", "bin", "bash.exe");

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
            yield return Path.Combine(localAppData, "Programs", "Git", "bin", "bash.exe");

        var git = ResolveCommand("git");
        if (git is null)
            yield break;

        var gitDirectory = Path.GetDirectoryName(git);
        if (string.IsNullOrWhiteSpace(gitDirectory))
            yield break;

        // Normal Git for Windows layout: cmd\git.exe next to bin\bash.exe.
        yield return Path.GetFullPath(Path.Combine(gitDirectory, "..", "bin", "bash.exe"));
    }
}
