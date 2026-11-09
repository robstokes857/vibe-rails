using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TokenSaver.Shape;

/// <summary>
/// A bounded shell subset, not a shell interpreter. Exactly one runner, preceded by literal
/// setup and followed by stdin-only filters/status echoes, may claim the per-line TestRun contract.
/// Quotes protect separators, but expansion, substitution, file redirects and unknown syntax fail
/// closed. The other command shapes never use this parser.
/// </summary>
internal static class TestRunCommands
{
    private sealed record Segment(string Before, List<string> Words);

    internal static bool IsTestRun(string? command)
    {
        if (string.IsNullOrWhiteSpace(command) || command.Length > 32768
            || !TrySplit(command, out var segments))
            return false;

        var runner = false;
        var echoed = false;
        foreach (var segment in segments)
        {
            var words = segment.Words;
            if (!runner)
            {
                if (segment.Before == "|") return false;
                if (IsSetup(words)) continue;
                if (!IsRunner(words)) return false;
                runner = true;
            }
            else if (segment.Before == "|" && !echoed && IsFilter(words)) { }
            else if (segment.Before is ";" or "&&" && IsEcho(words)) echoed = true;
            else return false;
        }
        return runner;
    }

    private static bool TrySplit(string command, out List<Segment> segments)
    {
        segments = [];
        var words = new List<string>();
        var word = new StringBuilder();
        var started = false;
        var quote = '\0';
        var before = "";
        void Flush()
        {
            if (started) words.Add(word.ToString());
            word.Clear();
            started = false;
        }
        for (var i = 0; i < command.Length; i++)
        {
            var c = command[i];
            if (c is '\r' or '\n' or '`' or '\0' or '“' or '”' or '‘' or '’') return false;
            // Backslash escaping shell syntax has different meanings in Bash and PowerShell.
            // Plain Windows path separators are fine; ambiguous escapes are not.
            if (c == '\\' && i + 1 < command.Length
                && (quote == '\0' && "\"'`$;&|<>() \t".Contains(command[i + 1])
                    || quote == '"' && "\"$`".Contains(command[i + 1])))
                return false;
            if (quote != '\0')
            {
                if (c == quote)
                {
                    // Adjacent quoted fragments/doubled quotes disagree between shell dialects.
                    // Accept whole quoted words and flag="value", not ambiguous concatenation.
                    if (i + 1 < command.Length && !" \t;&|".Contains(command[i + 1])) return false;
                    quote = '\0';
                }
                else word.Append(c);
                continue;
            }
            if (c is '\'' or '"') { quote = c; started = true; continue; }
            if (c is ' ' or '\t') { Flush(); continue; }
            if (!started && (command.AsSpan(i).StartsWith("2>&1") || command.AsSpan(i).StartsWith("*>&1")))
            {
                if (i + 4 < command.Length && !" \t;|&".Contains(command[i + 4])) return false;
                words.Add(command.Substring(i, 4));
                i += 3;
                continue;
            }
            if (c is '<' or '>' or '(' or ')') return false;
            if (c is ';' or '|' or '&')
            {
                Flush();
                if (c == '&' && (i + 1 == command.Length || command[i + 1] != '&'))
                {
                    if (words.Count != 0) return false;
                    words.Add("&");
                    continue;
                }
                if (c == '|' && i + 1 < command.Length && command[i + 1] == '|') return false;
                if (words.Count == 0) return false;
                segments.Add(new Segment(before, words));
                words = [];
                before = c == '&' ? "&&" : c.ToString();
                if (c == '&') i++;
                continue;
            }
            word.Append(c);
            started = true;
        }
        if (quote != '\0') return false;
        Flush();
        if (words.Count == 0) return false;
        segments.Add(new Segment(before, words));
        return segments.Count <= 32;
    }

    private static bool IsSetup(List<string> w)
    {
        if (w.Count == 0) return false;
        if (w[0] is "cd" or "pushd" || w[0].Equals("Set-Location", StringComparison.OrdinalIgnoreCase))
            return NoExpansion(w) && (w.Count == 2 && !w[1].StartsWith('-')
                || w.Count == 3 && (w[1].Equals("-LiteralPath", StringComparison.OrdinalIgnoreCase)
                    || w[1].Equals("-Path", StringComparison.OrdinalIgnoreCase)));
        if (w[0] == "export") return w.Count > 1 && w.Skip(1).All(IsAssignment);
        // Only the variable's literal $env: prefix is allowed, never expansion in its value.
        return w[0].StartsWith("$env:", StringComparison.OrdinalIgnoreCase)
            && Match(string.Join(" ", w), @"^\$env:[A-Za-z_][A-Za-z0-9_]*\s*=\s*[^$]*$");
    }

    private static bool IsAssignment(string value) =>
        Match(value, @"^[A-Za-z_][A-Za-z0-9_]*=[^$]*$");

    private static bool NoExpansion(IEnumerable<string> words) => words.All(x => !x.Contains('$'));

    private static bool IsRunner(List<string> source, int depth = 0)
    {
        if (depth > 4 || !NoExpansion(source)) return false;
        var w = new List<string>(source);
        if (w.Count > 0 && w[^1] is "2>&1" or "*>&1") w.RemoveAt(w.Count - 1);
        if (w.Any(x => x is "2>&1" or "*>&1")) return false;
        while (w.Count > 0 && IsAssignment(w[0])) w.RemoveAt(0);
        if (w.Count >= 2 && w[0] == "timeout")
        {
            if (!Match(w[1], @"^[0-9]+(?:\.[0-9]+)?[smhd]?$") || w[1][0] == '0') return false;
            w.RemoveRange(0, 2);
        }
        if (w.Count > 0 && w[0] == "&") w.RemoveAt(0);
        if (w.Count == 0 || w.Contains("&")) return false;
        var executable = w[0].Replace('\\', '/').Split('/')[^1];
        if (executable.Equals("Tests.exe", StringComparison.OrdinalIgnoreCase)
            || executable.EndsWith(".Tests.exe", StringComparison.OrdinalIgnoreCase)
            || executable.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase)) return true;
        var name = executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? executable[..^4] : executable;
        bool At(int i, string value) => w.Count > i && w[i] == value;
        switch (name)
        {
            case "pytest": case "jest": case "vitest": case "mocha": case "gotestsum": return true;
            case "node": return IsNodeTest(w);
            case "dotnet": return At(1, "test") || IsIntegrationRun(w);
            case "go": case "playwright": case "deno": case "hatch": return At(1, "test");
            case "cargo": return At(1, "test") || At(1, "nextest") && At(2, "run");
            case "python": case "python3": return At(1, "-m") && (At(2, "pytest") || At(2, "unittest"));
            case "uv": case "poetry": case "pipenv":
                return At(1, "run") && w.Count > 2 && IsRunner(w.GetRange(2, w.Count - 2), depth + 1);
            case "npx": case "bunx":
                return w.Count > 1 && w[1] is "jest" or "vitest" or "mocha"
                    || At(1, "playwright") && At(2, "test");
            case "npm": case "pnpm": case "yarn": case "bun": return IsPackageTest(w);
            default: return false;
        }
    }

    private static bool IsNodeTest(List<string> w)
    {
        var test = false;
        for (var i = 1; i < w.Count; i++)
        {
            var arg = w[i];
            if (arg == "--" || !arg.StartsWith('-')) break;
            if (arg is "--eval" or "--print" or "-e" or "-p"
                || arg.StartsWith("--eval=", StringComparison.Ordinal)
                || arg.StartsWith("--print=", StringComparison.Ordinal)) return false;
            if (arg == "--test") { test = true; continue; }
            var flag = arg.Split('=')[0];
            if (flag is "--test-name-pattern" or "--test-skip-pattern" or "--test-reporter"
                or "--test-reporter-destination" or "--test-concurrency" or "--test-timeout"
                or "--test-shard" or "--import" or "--require" or "-r")
            {
                if (!arg.Contains('=') && ++i >= w.Count) return false;
            }
            else if (flag is not ("--test-only" or "--test-force-exit" or "--experimental-test-coverage"
                or "--no-warnings" or "--experimental-strip-types")) return false;
        }
        return test;
    }

    private static bool IsPackageTest(List<string> w)
    {
        var i = 1;
        while (i < w.Count && w[i].StartsWith('-'))
        {
            var flag = w[i].Split('=')[0];
            if (flag is "--prefix" or "--dir" or "--cwd" or "-C" or "--workspace" or "-w")
            {
                if (!w[i].Contains('=') && ++i >= w.Count) return false;
            }
            else if (flag is not ("--silent" or "-s")) return false;
            i++;
        }
        if (i < w.Count && w[i] is "run" or "run-script") i++;
        return i < w.Count && (w[i] is "test" or "t" || w[i].StartsWith("test:", StringComparison.Ordinal));
    }

    private static bool IsIntegrationRun(List<string> w)
    {
        if (w.Count < 4 || w[1] != "run" || w[2] != "--project") return false;
        var project = w[3].Replace('\\', '/').TrimEnd('/').Split('/')[^1];
        return project is "IntegrationTest" or "IntegrationTest.csproj"
            && !w.Skip(4).Any(x => x is "-p" or "--project" || x.StartsWith("--project=", StringComparison.Ordinal));
    }

    private static bool IsEcho(List<string> w) => w.Count > 0
        && (w[0] == "echo" || w[0].Equals("Write-Output", StringComparison.OrdinalIgnoreCase))
        && w.Skip(1).All(x => !x.Replace("$?", "").Contains('$'));

    private static bool IsFilter(List<string> w)
    {
        if (w.Count == 0 || !NoExpansion(w)) return false;
        if (w[0] is "tail" or "head")
            return w.Count == 1 || w.Count == 2 && Match(w[1], @"^-[0-9]+$")
                || w.Count == 3 && w[1] == "-n" && IsCount(w[2]);
        if (w[0].Equals("Select-Object", StringComparison.OrdinalIgnoreCase))
            return w.Count == 3 && (w[1].Equals("-Last", StringComparison.OrdinalIgnoreCase)
                || w[1].Equals("-First", StringComparison.OrdinalIgnoreCase)) && IsCount(w[2]);
        if (w[0].Equals("Select-String", StringComparison.OrdinalIgnoreCase))
        {
            var args = w.Skip(1).ToList();
            if (args.Count > 0 && args[0].Equals("-Pattern", StringComparison.OrdinalIgnoreCase)) args.RemoveAt(0);
            return args.Count == 1 && !args[0].StartsWith('-');
        }
        if (w[0] is not ("grep" or "findstr")) return false;
        var pattern = false;
        for (var i = 1; i < w.Count; i++)
        {
            var arg = w[i];
            if (w[0] == "grep")
            {
                if (Match(arg, @"^-[EiFivxw]+$") || Match(arg, @"^-[ABC][0-9]+$")) continue;
                if (arg is "-A" or "-B" or "-C")
                {
                    if (++i >= w.Count || !IsCount(w[i])) return false;
                    continue;
                }
            }
            if (!pattern && w[0] == "findstr" && arg is "/R" or "/I" or "/L") continue;
            if (pattern || arg.StartsWith('-') || w[0] == "findstr" && arg.StartsWith('/')) return false;
            pattern = true;
        }
        return pattern;
    }

    private static bool IsCount(string s) => int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0;
    private static bool Match(string s, string pattern) => Regex.IsMatch(s, pattern, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
}
