using System.Buffers;

namespace TokenSaver.Shape;

/// <summary>
/// The output shapes <see cref="ShapeFilters"/> recognizes. A shape is a claim about the LINES a
/// command emits, not about the command itself — <see cref="GrepMatches"/> means "every interesting
/// line is exactly <c>path:line:content</c>", and the filter is allowed to act on that.
/// </summary>
public enum CommandShape
{
    /// <summary>Unknown / unprovable — filters must leave the output alone.</summary>
    None = 0,

    /// <summary><c>XY path</c> lines (git status --short/--porcelain).</summary>
    GitStatus,

    /// <summary>git log. Carries NO line-shape contract — see <see cref="CommandShapes"/>.</summary>
    GitLog,

    /// <summary>git diff/show. Carries NO line-shape contract — see <see cref="CommandShapes"/>.</summary>
    GitDiff,

    /// <summary>ls/dir/tree. Carries NO line-shape contract — see <see cref="CommandShapes"/>.</summary>
    DirectoryListing,

    /// <summary><c>path:line:content</c> lines (grep/rg with line numbers on).</summary>
    GrepMatches,

    /// <summary>One path per line (find).</summary>
    PathList,

    /// <summary>A recognised test-runner invocation (pytest, dotnet/go/cargo test, the JS
    /// runners and their package-manager test scripts). Unlike the shapes above this is a
    /// per-LINE contract, not a whole-payload one — see <see cref="CommandShapes"/> remarks.</summary>
    TestRun,
}

/// <summary>
/// Maps a shell command string to the <see cref="CommandShape"/> of the output it will produce, or
/// <see cref="CommandShape.None"/> when we cannot prove what that output looks like. Pure, ordinal,
/// never throws.
///
/// This classifier IS the safety boundary for <see cref="ShapeFilters"/>: everything downstream
/// assumes the payload really is (say) <c>path:line:content</c> lines and nothing else. A false None
/// costs savings on one tool result; a false shape mangles output the model then reasons about. So
/// every rule fails toward None:
///
///   • Whole-command rule — any shell metacharacter (<c>| &lt; &gt; &amp; ; ` $ ( )</c> or a newline)
///     returns None, wherever it appears, INCLUDING inside quotes. We only classify when the string
///     we were handed IS the command whose output we are about to filter; one <c>| head</c>, one
///     <c>2&gt;&amp;1</c>, one <c>&amp;&amp; ls</c> and the payload is no longer that command's
///     output at all. Quote-aware parsing would rescue <c>rg -n "a|b" src/</c>, at the cost of being
///     a shell parser; declining is free and cannot be wrong. <c>$</c> goes the same way: an
///     expansion is invisible to us and could carry any flag (<c>rg -n $FLAGS x</c>).
///   • Flags are ALLOWLISTED per command, never denylisted. An unknown flag returns None, so a flag
///     we have never heard of — or one upstream adds next release — can never silently change the
///     shape out from under us. Value-taking short flags stop the scan of their cluster the way
///     getopt does (<c>-tcs</c> is <c>--type cs</c>, not the flags t, c and s).
///   • Tokens are unquoted before the flag check, so <c>rg -n "-l" src/</c> is None: the shell
///     strips those quotes and rg sees the flag, so we must see it too.
///   • The first token must BE the command. <c>sudo ls</c>, <c>FOO=1 ls</c>, <c>/usr/bin/git status
///     -s</c> and <c>git -C x status -s</c> are all None. Each skip/normalize rule is another chance
///     to be wrong about which binary actually ran (<c>sudo -u x ls</c> vs <c>sudo ls</c>), for a
///     handful of invocations agents rarely emit.
///
/// <see cref="CommandShape.GitLog"/>, <see cref="CommandShape.GitDiff"/> and
/// <see cref="CommandShape.DirectoryListing"/> are classified on the command NAME alone and carry no
/// line-shape contract, because <see cref="ShapeFilters"/> deliberately treats all three as no-ops in
/// v1. Anyone adding a real filter for them must tighten the flag handling HERE first: <c>git log</c>
/// vs <c>--oneline</c> vs <c>--graph</c> vs <c>--format=…</c>, and <c>ls</c> vs <c>ls -l</c> vs
/// PowerShell's <c>ls</c> (an alias for Get-ChildItem, whose output is a table) share a name and
/// nothing else.
///
/// <see cref="CommandShape.TestRun"/> is the deliberate exception to the flag-allowlist rule, because
/// its contract is WEAKER than the others': it does not claim every line has a shape, only that a
/// line matching one of a few narrow per-runner patterns (<c>path::name PASSED</c>, <c>✓ name</c>,
/// <c>--- PASS: name</c>, …) reports an individual passing test. No runner flag can make an
/// unrelated line match those patterns; the one real leak is a test's own captured stdout printed
/// inline (e.g. <c>pytest -s</c>), where a print that impersonates a result line gets counted into
/// the filter's <c>[... N passed ...]</c> marker instead of shown — an accepted best-effort loss,
/// never a parse of output we misunderstood. The whole-command metacharacter rule still applies
/// unchanged, so piped or redirected test runs are None like everything else. Wrappers
/// (<c>docker compose run app pytest</c>, <c>sudo pytest</c>) are None by the first-token rule —
/// classifying through a wrapper means guessing which binary actually ran.
/// </summary>
public static class CommandShapes
{
    /// <summary>
    /// Anything that means the payload may not be this one command's output. Backtick and <c>$</c>
    /// cover both command-substitution forms; <c>(</c>/<c>)</c> cover subshells and process
    /// substitution; <c>&amp;</c> covers both <c>&amp;&amp;</c> and backgrounding. Tabs are absent on
    /// purpose — they separate tokens, they do not compose commands.
    /// </summary>
    private static readonly SearchValues<char> ShellMetacharacters =
        SearchValues.Create("|<>&;`$()\n\r");

    private static readonly char[] TokenSeparators = [' ', '\t'];

    /// <summary>
    /// git status flags that keep the output at <c>XY path</c> lines. <c>--porcelain=v2</c> is
    /// absent on purpose (a completely different format), as is <c>-z</c> (NUL separators destroy the
    /// line model), <c>--long</c>/bare <c>git status</c> (prose), and <c>-b</c>/<c>--branch</c> (adds
    /// a <c>## branch</c> line).
    /// </summary>
    private static readonly HashSet<string> GitStatusFlags = new(StringComparer.Ordinal)
    {
        "--", "-u", "-uall", "-uno", "-unormal", "--untracked-files=all", "--untracked-files=no",
        "--untracked-files=normal", "--no-renames", "--ignored",
    };

    /// <summary>The git status flags that actually SELECT the <c>XY path</c> format.</summary>
    private static readonly HashSet<string> GitStatusPorcelainFlags = new(StringComparer.Ordinal)
    {
        "-s", "--short", "--porcelain", "--porcelain=v1",
    };

    /// <summary>
    /// grep-family long flags that leave the output at <c>path:line:content</c>. Compared by name, so
    /// both <c>--type cs</c> and <c>--type=cs</c> hit. Deliberately absent, each because it changes
    /// the shape: <c>--files-with-matches</c>/<c>--count</c>/<c>--only-matching</c> (not
    /// path:line:content at all), <c>--heading</c> (rg's grouped form), <c>--column</c> and
    /// <c>--vimgrep</c> (an extra colon field), <c>--json</c>, <c>--multiline</c> (a match spans
    /// lines, so the continuation lines carry no path), <c>--context</c>/<c>--after-context</c>/
    /// <c>--before-context</c> (context lines use <c>-</c> separators and would be reordered away
    /// from their match), <c>--no-filename</c> and <c>--no-line-number</c> (drop a field), and
    /// <c>--null</c>. <c>--color</c> is allowed with any value: colored output carries ESC, and the
    /// filter fail-opens on ESC.
    /// </summary>
    private static readonly HashSet<string> GrepLongFlags = new(StringComparer.Ordinal)
    {
        "--", "--line-number", "--numbers", "--ignore-case", "--case-sensitive", "--smart-case",
        "--word-regexp", "--line-regexp", "--fixed-strings", "--extended-regexp", "--basic-regexp",
        "--perl-regexp", "--pcre2", "--invert-match", "--recursive", "--norecurse", "--no-recurse",
        "--with-filename", "--no-heading", "--hidden", "--no-ignore", "--no-ignore-vcs", "--follow",
        "--text", "--no-messages", "--no-config", "--no-color", "--color", "--colour", "--regexp",
        "--file", "--type", "--type-not", "--glob", "--iglob", "--include", "--exclude",
        "--exclude-dir", "--max-count", "--max-depth", "--maxdepth", "--threads", "--sort",
        "--binary-files",
    };

    /// <summary>
    /// Long options whose value is the next token when it is not attached with <c>=</c>. The value
    /// may itself start with <c>-</c> (for example <c>rg --regexp -n file</c> searches for the
    /// literal pattern <c>-n</c>), so failing to consume it can turn data back into an option and
    /// falsely assert a line-numbered output shape.
    /// </summary>
    private static readonly HashSet<string> RipgrepLongFlagsWithValue = new(StringComparer.Ordinal)
    {
        "--color", "--colour", "--regexp", "--file", "--type", "--type-not", "--glob",
        "--iglob", "--max-count", "--max-depth", "--threads", "--sort",
    };

    private static readonly HashSet<string> GrepLongFlagsWithValue = new(StringComparer.Ordinal)
    {
        "--regexp", "--file", "--include", "--exclude", "--exclude-dir", "--max-count",
        "--max-depth", "--maxdepth", "--binary-files",
    };

    /// <summary>
    /// grep-family short flags that leave the output at <c>path:line:content</c>. Absent (shape
    /// changers): l, L, c, o, A, B, C, U, z, N, h, I, p, q, M, b.
    /// </summary>
    private const string GrepShortFlags = "nifwxvFEPsSHuaerRtTgmj";

    /// <summary>
    /// Short flags whose VALUE is the rest of the cluster (or the next token). Scanning must stop at
    /// these or <c>-tcs</c> ("--type cs") reads as the flags t, c and s — and c is a shape changer.
    /// </summary>
    private const string RipgrepShortFlagsWithValue = "efEtTgmjr";
    private const string GrepShortFlagsWithValue = "efm";
    private const string SilverSearcherShortFlagsWithValue = "gm";

    /// <summary>
    /// find predicates that leave the output at one path per line. The <c>-print</c> variants are the
    /// shape changers and are all absent: <c>-print0</c> (NUL), <c>-printf</c>/<c>-fprintf</c>
    /// (arbitrary), <c>-ls</c> (long format), plus <c>-exec</c>/<c>-execdir</c>/<c>-ok</c> (another
    /// command's output entirely) and <c>-delete</c>.
    /// </summary>
    private static readonly HashSet<string> FindPredicates = new(StringComparer.Ordinal)
    {
        "-L", "-H", "-P", "-type", "-name", "-iname", "-path", "-ipath", "-regex", "-iregex",
        "-lname", "-ilname", "-maxdepth", "-mindepth", "-size", "-empty", "-newer", "-mtime",
        "-mmin", "-ctime", "-cmin", "-atime", "-amin", "-user", "-group", "-uid", "-gid", "-perm",
        "-readable", "-writable", "-executable", "-follow", "-print", "-prune", "-not", "-and",
        "-or", "-a", "-o",
    };

    /// <summary>
    /// Classifies <paramref name="command"/>. Returns <see cref="CommandShape.None"/> for null,
    /// empty, unrecognized, or anything whose output shape is not provable from the string alone.
    /// </summary>
    public static CommandShape Classify(string? command)
    {
        if (string.IsNullOrEmpty(command) || command.AsSpan().IndexOfAny(ShellMetacharacters) >= 0)
            return CommandShape.None;

        var tokens = command.Split(TokenSeparators, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
            return CommandShape.None;

        return Unquote(tokens[0]) switch
        {
            "git" => ClassifyGit(tokens),
            // No line-shape contract — these are ShapeFilters no-ops in v1 (see class remarks).
            "ls" or "dir" or "tree" => CommandShape.DirectoryListing,
            "grep" or "rg" or "ripgrep" or "ag" => ClassifyGrep(tokens),
            "find" => ClassifyFind(tokens),
            // Test runners: no flag scan, per the weaker per-line contract (class remarks).
            "pytest" or "jest" or "vitest" or "mocha" => CommandShape.TestRun,
            "dotnet" or "go" or "cargo" or "playwright" =>
                SecondTokenIs(tokens, "test") ? CommandShape.TestRun : CommandShape.None,
            "npm" or "pnpm" or "yarn" or "bun" => ClassifyPackageManagerTest(tokens),
            "npx" or "bunx" => ClassifyRunnerLauncher(tokens),
            "python" or "python3" => ClassifyPythonModule(tokens),
            "uv" => ClassifyUvRun(tokens),
            _ => CommandShape.None,
        };
    }

    private static bool SecondTokenIs(string[] tokens, string subcommand) =>
        tokens.Length >= 2 && Unquote(tokens[1]) == subcommand;

    /// <summary>
    /// <c>npm test</c> / <c>npm t</c> / <c>npm run test…</c> and the pnpm/yarn/bun equivalents
    /// (which also run scripts without <c>run</c>). The script itself can be anything, but the
    /// per-line drop patterns only ever match real runner output, so a mislabeled script costs a
    /// no-op, not a mangle.
    /// </summary>
    private static CommandShape ClassifyPackageManagerTest(string[] tokens)
    {
        if (tokens.Length < 2)
            return CommandShape.None;

        var second = Unquote(tokens[1]);
        if (IsTestScriptName(second))
            return CommandShape.TestRun;
        if (second is "run" or "run-script" && tokens.Length >= 3 && IsTestScriptName(Unquote(tokens[2])))
            return CommandShape.TestRun;
        return CommandShape.None;
    }

    private static bool IsTestScriptName(string script) =>
        script is "test" or "t" || script.StartsWith("test:", StringComparison.Ordinal);

    private static CommandShape ClassifyRunnerLauncher(string[] tokens)
    {
        if (tokens.Length < 2)
            return CommandShape.None;

        return Unquote(tokens[1]) switch
        {
            "jest" or "vitest" or "mocha" => CommandShape.TestRun,
            "playwright" => tokens.Length >= 3 && Unquote(tokens[2]) == "test"
                ? CommandShape.TestRun
                : CommandShape.None,
            _ => CommandShape.None,
        };
    }

    private static CommandShape ClassifyPythonModule(string[] tokens) =>
        tokens.Length >= 3 && Unquote(tokens[1]) == "-m" && Unquote(tokens[2]) == "pytest"
            ? CommandShape.TestRun
            : CommandShape.None;

    private static CommandShape ClassifyUvRun(string[] tokens) =>
        tokens.Length >= 3 && Unquote(tokens[1]) == "run" && Unquote(tokens[2]) == "pytest"
            ? CommandShape.TestRun
            : CommandShape.None;

    private static CommandShape ClassifyGit(string[] tokens)
    {
        // tokens[1] must BE the subcommand: `git -C x status -s` and `git --no-pager log` are None
        // (see the "first token must be the command" rule — same reasoning, one level down).
        if (tokens.Length < 2)
            return CommandShape.None;

        return Unquote(tokens[1]) switch
        {
            "status" => ClassifyGitStatus(tokens),
            "log" => CommandShape.GitLog,
            "diff" or "show" => CommandShape.GitDiff,
            _ => CommandShape.None,
        };
    }

    /// <summary>
    /// GitStatus only for the porcelain/short forms. Bare <c>git status</c> is the long format —
    /// prose paragraphs, not <c>XY path</c> lines — and must stay None.
    /// </summary>
    private static CommandShape ClassifyGitStatus(string[] tokens)
    {
        var porcelain = false;
        for (var i = 2; i < tokens.Length; i++)
        {
            var token = Unquote(tokens[i]);
            if (token == "--")
                break; // every remaining token is a pathspec, even when it starts with '-'
            if (token.Length == 0 || token[0] != '-')
                continue; // a pathspec: narrows WHICH paths are listed, not how they are printed

            if (GitStatusPorcelainFlags.Contains(token))
                porcelain = true;
            else if (!GitStatusFlags.Contains(token))
                return CommandShape.None;
        }

        return porcelain ? CommandShape.GitStatus : CommandShape.None;
    }

    /// <summary>
    /// GrepMatches only when the output will really be <c>path:line:content</c>. Line numbers must be
    /// requested EXPLICITLY: rg (and ag) default to line numbers only when stdout is a tty, and a
    /// tool-result payload never is.
    /// </summary>
    private static CommandShape ClassifyGrep(string[] tokens)
    {
        var tool = Unquote(tokens[0]);
        var ripgrep = tool is "rg" or "ripgrep";

        // The silver searcher's -n is --norecurse, NOT line numbers (that is --numbers). Reading it
        // as grep's -n would classify `ag -n TODO src/` as GrepMatches and then regroup output that
        // has no line-number field at all.
        var silverSearcher = tool is "ag";

        var lineNumbers = false;
        for (var i = 1; i < tokens.Length; i++)
        {
            var token = Unquote(tokens[i]);
            if (token == "--")
                break; // end of options: patterns/paths beginning with '-' are data from here on
            if (token.Length < 2 || token[0] != '-')
                continue; // the pattern, or a path to search

            if (token[1] == '-')
            {
                var name = LongFlagName(token);
                if (!GrepLongFlags.Contains(name))
                    return CommandShape.None;
                if (name == (silverSearcher ? "--numbers" : "--line-number"))
                    lineNumbers = true;

                var takesValue = (ripgrep ? RipgrepLongFlagsWithValue : GrepLongFlagsWithValue)
                    .Contains(name);
                if (takesValue && token.IndexOf('=') < 0)
                {
                    // A required value in the next token belongs to this option even if it looks
                    // like another flag. Missing values describe an invalid command, whose output
                    // is error prose rather than a shape we can prove.
                    if (++i >= tokens.Length)
                        return CommandShape.None;
                }
                continue;
            }

            for (var c = 1; c < token.Length; c++)
            {
                var flag = token[c];
                if (GrepShortFlags.IndexOf(flag) < 0)
                    return CommandShape.None;
                if (flag == 'n' && !silverSearcher)
                    lineNumbers = true;

                // rg's -r is --replace (value-taking); grep's and ag's -r is --recursive. Getting
                // this backwards would break the scan of `grep -rn TODO .` — the most common
                // invocation there is.
                var valueFlags = ripgrep
                    ? RipgrepShortFlagsWithValue
                    : silverSearcher
                        ? SilverSearcherShortFlagsWithValue
                        : GrepShortFlagsWithValue;
                if (valueFlags.IndexOf(flag) >= 0)
                {
                    // getopt consumes the remainder of the cluster as the value, or the next
                    // token when the value-taking flag is last. In the latter case the value may
                    // start with '-', and must never be revisited as an option.
                    if (c == token.Length - 1 && ++i >= tokens.Length)
                        return CommandShape.None;
                    break;
                }
            }
        }

        return lineNumbers ? CommandShape.GrepMatches : CommandShape.None;
    }

    /// <summary>
    /// PathList only when at least one GNU-style predicate is present. <c>find .</c> emits a fine
    /// path list, but on Windows <c>find</c> is find.exe — a grep-alike whose output
    /// (<c>---------- FILE.TXT</c> banners) is nothing like a path list — and it takes no
    /// <c>-predicate</c> arguments. Requiring one is a cheap, sound discriminator; the cost is
    /// declining the bare form.
    /// </summary>
    private static CommandShape ClassifyFind(string[] tokens)
    {
        var predicate = false;
        for (var i = 1; i < tokens.Length; i++)
        {
            var token = Unquote(tokens[i]);
            if (token.Length < 2 || token[0] != '-')
                continue; // the start directory, or a predicate's value

            if (!FindPredicates.Contains(token))
                return CommandShape.None;
            predicate = true;
        }

        return predicate ? CommandShape.PathList : CommandShape.None;
    }

    /// <summary>Splits <c>--name=value</c> down to <c>--name</c>; other tokens pass through.</summary>
    private static string LongFlagName(string token)
    {
        var equals = token.IndexOf('=');
        return equals < 0 ? token : token[..equals];
    }

    /// <summary>
    /// Strips one leading and one trailing quote. Naive whitespace tokenizing splits <c>"foo bar"</c>
    /// into <c>"foo</c> and <c>bar"</c>, so this is deliberately per-token and not a quote parser: its
    /// only job is to stop a quoted flag from hiding from the allowlist.
    /// </summary>
    private static string Unquote(string token)
    {
        var start = 0;
        var end = token.Length;
        if (end > start && token[start] is '"' or '\'')
            start++;
        if (end > start && token[end - 1] is '"' or '\'')
            end--;
        return start == 0 && end == token.Length ? token : token[start..end];
    }
}
