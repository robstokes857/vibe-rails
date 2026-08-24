using VibeRails.Services;

namespace VibeRails.Services.Jobs;

public interface IJobExecutableResolver
{
    string? Resolve(LLM llm);
}

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
}
