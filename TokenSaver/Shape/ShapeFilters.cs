using System.Text;

namespace TokenSaver.Shape;

/// <summary>
/// The token saver's shape-aware stage: rewrites a tool_result whose <see cref="CommandShape"/> is
/// known, hoisting the field every line repeats (the path, the status code) into a header once.
/// Three transforms, one per provable shape:
///
///   G. <see cref="CommandShape.GitStatus"/> — <c>XY path</c> lines become <c>XY:</c> groups with
///      paths indented 2 spaces, groups and paths sorted ordinally.
///   M. <see cref="CommandShape.GrepMatches"/> — <c>path:line:content</c> lines become <c>path:</c>
///      groups with <c>  line: content</c> beneath, in ORIGINAL order (see below).
///   P. <see cref="CommandShape.PathList"/> — paths become <c>directory:</c> groups with filenames
///      indented 2 spaces.
///
/// <see cref="CommandShape.DirectoryListing"/>, <see cref="CommandShape.GitLog"/> and
/// <see cref="CommandShape.GitDiff"/> are DELIBERATE no-ops in v1 — they return the same instance.
/// Their whitespace and ANSI are already handled by <see cref="Minify.OutputMinifier"/>, and beyond
/// that we do not yet have a compaction for them we can prove safe: <c>ls -l</c> columns, log
/// bodies and diff hunks are all free-form text where "the field every line repeats" is a guess, not
/// a shape. Do not invent one here — give it a CommandShape contract in
/// <see cref="CommandShapes"/> first, or it will mangle output nobody understood.
///
/// Like <see cref="Minify.OutputCondenser"/>, these transforms MOVE and INSERT text, so the output is
/// not a subsequence of the input. The invariants that hold instead — each pinned by a property test:
///
///   • Deterministic: a pure function of (input, shape). Every comparison, sort and search is
///     ordinal; there are no clocks and no randomness. The CLI re-sends its history every turn, so
///     the same tool_result must filter to the same bytes forever or upstream prompt caching thrashes.
///   • Never grows: grouping a SINGLE match is a loss (<c>a.cs:1:x</c> → <c>a.cs:\n  1: x</c>), and
///     hoisting <c>XY </c> to <c>  </c> only saves one char per line against a 4-char header. So the
///     grouped form is measured and DISCARDED for the original instance unless it is strictly
///     shorter — which also makes "unchanged → same instance" hold, as in the other two stages.
///   • Idempotent: Apply(Apply(x, s), s) == Apply(x, s). The keystone is
///     <see cref="Minify.OutputCondenser"/>'s: everything a transform EMITS is ineligible to be regrouped,
///     so a second pass finds zero groupable lines and returns the input untouched. Each proof is
///     structural, not hopeful:
///       G — a status line is <c>XY</c> + space + path, with X,Y from git's code alphabet and never
///           both spaces. An emitted entry is <c>"  "</c> + path: either path[0] is a space, making
///           the status field <c>"  "</c> (rejected — git never prints it, it means unmodified), or
///           it is not, and the char at index 2 is not a space (rejected). Headers are 3 chars, below
///           the 4-char minimum. Both forms are ineligible for EVERY path, including quoted and
///           space-led ones.
///       M — a match line must not start with a space (every emitted entry does) and must not start
///           with <c>digits:</c>. The path field is the text before the FIRST <c>:digits:</c>, so it
///           provably contains no <c>:digits:</c> itself (an earlier one would have been the first)
///           and cannot end in <c>:digits</c> (that too would have split earlier) — therefore the
///           emitted header, path + <c>:</c>, contains no <c>:digits:</c> anywhere and is ineligible.
///       P — a path line must not start with a space (every emitted entry does) and must not end
///           with <c>:</c> (every emitted header does).
///     Genuine lines that happen to land in an ineligible shape merely lose grouping — a savings
///     loss, never a correctness loss.
///   • Whole-string fail-open: input containing ESC, BEL, or any CR is returned untouched — the same
///     rule as <see cref="Minify.OutputCondenser"/> and for the same reason. Reordering lines around
///     an escape sequence, or splitting on a field separator that is actually inside one, edits text
///     whose rendered meaning we deliberately did not model. Do not "improve" this to
///     skip-and-continue.
///   • Never throws, and empty input returns the input.
///
/// Two ordering decisions, both deliberate. M does NOT sort its groups: the model's mental model
/// follows search order, and reordering results is a bigger semantic change than hoisting a repeated
/// prefix — the savings do not need it. G and P do sort, because their inputs have no meaningful
/// order to lose (git's index walk, find's filesystem walk) and a sorted payload is stable across
/// runs, which is worth cache hits. All three move lines they could not parse to the END, so a
/// warning that git printed above the status lines lands below them; that is the cost of grouping,
/// and it is why an unparsed payload (zero groupable lines) is returned completely untouched instead.
///
/// This is not on <see cref="Minify.OutputMinifier"/>'s hot path — it runs once per tool_result, on
/// payloads already known to be one command's output — so it favors plain lines over span
/// arithmetic.
/// </summary>
public static class ShapeFilters
{
    /// <summary>Git's porcelain status codes. Note <c>' '</c>: <c>" M path"</c> is worktree-modified.</summary>
    private const string GitStatusCodes = " MADRCUT?!";

    private const string Indent = "  ";

    /// <summary>
    /// Applies the filter for <paramref name="shape"/>, returning the original instance when the
    /// shape has no v1 filter, when nothing could be grouped, when a control character forced the
    /// fail-open path, or when the grouped form would not be strictly shorter.
    /// </summary>
    public static string Apply(string toolOutput, CommandShape shape)
    {
        if (string.IsNullOrEmpty(toolOutput))
            return toolOutput;

        // The whole-string fail-open documented in the class remarks.
        if (toolOutput.AsSpan().IndexOfAny('\x1b', '\a', '\r') >= 0)
            return toolOutput;

        var grouped = shape switch
        {
            CommandShape.GitStatus => GroupGitStatus(toolOutput),
            CommandShape.GrepMatches => GroupGrepMatches(toolOutput),
            CommandShape.PathList => GroupPathList(toolOutput),

            // None, plus the deliberate v1 no-ops (DirectoryListing/GitLog/GitDiff) and any enum
            // value from a newer caller than this switch: unknown shape = leave the bytes alone.
            _ => toolOutput,
        };

        // Never grows. Equal length counts as "not worth it": a same-size regrouping buys no tokens
        // and is pure churn, and returning the original keeps the same-instance contract.
        return grouped.Length >= toolOutput.Length ? toolOutput : grouped;
    }

    // ---------------------------------------------------------------------
    // G — git status
    // ---------------------------------------------------------------------

    private static string GroupGitStatus(string text)
    {
        var lines = SplitLines(text, out var count, out var trailingNewline);
        var groups = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        List<string>? passthrough = null;

        for (var i = 0; i < count; i++)
        {
            var line = lines[i];
            if (!TrySplitStatusLine(line, out var status, out var path))
            {
                (passthrough ??= []).Add(line);
                continue;
            }

            if (!groups.TryGetValue(status, out var paths))
                groups.Add(status, paths = []);
            paths.Add(path);
        }

        // Nothing recognized → the payload was not what we thought. Returning it whole (rather than
        // shuffling every line into the passthrough tail) is also the second-pass fixed point.
        if (groups.Count == 0)
            return text;

        var output = new StringBuilder(text.Length);
        foreach (var (status, paths) in groups)
        {
            paths.Sort(StringComparer.Ordinal);
            output.Append(status).Append(":\n");
            foreach (var path in paths)
                output.Append(Indent).Append(path).Append('\n');
        }

        return Finish(output, passthrough, trailingNewline);
    }

    /// <summary>
    /// Splits <c>XY path</c>. The status must be two real git codes and never two spaces — that is
    /// what makes every emitted entry ineligible (class remarks, proof G), and it also keeps
    /// <c>## branch...origin/branch</c> and prose out of the groups.
    /// </summary>
    private static bool TrySplitStatusLine(string line, out string status, out string path)
    {
        status = string.Empty;
        path = string.Empty;

        // 4 chars is the shortest possible: two codes, a space, and a one-char path.
        if (line.Length < 4 || line[2] != ' ')
            return false;
        if (GitStatusCodes.IndexOf(line[0]) < 0 || GitStatusCodes.IndexOf(line[1]) < 0)
            return false;
        if (line[0] == ' ' && line[1] == ' ')
            return false;

        status = line[..2];
        path = line[3..];
        return true;
    }

    // ---------------------------------------------------------------------
    // M — grep matches
    // ---------------------------------------------------------------------

    private static string GroupGrepMatches(string text)
    {
        var lines = SplitLines(text, out var count, out var trailingNewline);
        var entries = new Dictionary<string, List<(string Number, string Content)>>(StringComparer.Ordinal);
        var order = new List<string>();
        List<string>? passthrough = null;

        for (var i = 0; i < count; i++)
        {
            var line = lines[i];
            if (!TrySplitMatchLine(line, out var path, out var number, out var content))
            {
                (passthrough ??= []).Add(line);
                continue;
            }

            if (!entries.TryGetValue(path, out var matches))
            {
                entries.Add(path, matches = []);
                order.Add(path); // first-seen order: the model's mental model follows search order
            }
            matches.Add((number, content));
        }

        if (order.Count == 0)
            return text;

        var output = new StringBuilder(text.Length);
        foreach (var path in order)
        {
            output.Append(path).Append(":\n");
            foreach (var (number, content) in entries[path])
                output.Append(Indent).Append(number).Append(": ").Append(content).Append('\n');
        }

        return Finish(output, passthrough, trailingNewline);
    }

    /// <summary>
    /// Splits <c>path:line:content</c> at the FIRST <c>:digits:</c> — the split that proof M rests
    /// on, and the one that reads <c>src/a.cs:12:x:34:y</c> the way grep meant it (path <c>src/a.cs</c>,
    /// content <c>x:34:y</c>) rather than at the last colon.
    /// </summary>
    private static bool TrySplitMatchLine(string line, out string path, out string number, out string content)
    {
        path = string.Empty;
        number = string.Empty;
        content = string.Empty;

        // Ineligible: our own indented entries (proof M).
        if (line.Length == 0 || line[0] == ' ')
            return false;

        // Ineligible: grep and rg both OMIT the path when searching a single named file, leaving
        // "12:  content". Such a line has no path to group by, and if its content happens to hold a
        // ":<digits>:" we would split there and invent a file named "12:  content-up-to-it".
        if (StartsWithLineNumberField(line))
            return false;

        var from = 0;
        while (true)
        {
            var colon = line.IndexOf(':', from);
            if (colon < 0)
                return false;

            var digitsEnd = colon + 1;
            while (digitsEnd < line.Length && char.IsAsciiDigit(line[digitsEnd]))
                digitsEnd++;

            if (digitsEnd > colon + 1 && digitsEnd < line.Length && line[digitsEnd] == ':')
            {
                if (colon == 0)
                    return false; // empty path — the line is not ours to touch

                path = line[..colon];
                number = line[(colon + 1)..digitsEnd];
                content = line[(digitsEnd + 1)..];
                return true;
            }

            from = colon + 1;
        }
    }

    /// <summary>True for the path-less single-file form: a leading digit run followed by a colon.</summary>
    private static bool StartsWithLineNumberField(string line)
    {
        var i = 0;
        while (i < line.Length && char.IsAsciiDigit(line[i]))
            i++;
        return i > 0 && i < line.Length && line[i] == ':';
    }

    // ---------------------------------------------------------------------
    // P — path list
    // ---------------------------------------------------------------------

    private static string GroupPathList(string text)
    {
        var lines = SplitLines(text, out var count, out var trailingNewline);
        var groups = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        List<string>? passthrough = null;

        for (var i = 0; i < count; i++)
        {
            var line = lines[i];
            if (!TrySplitPath(line, out var directory, out var name))
            {
                (passthrough ??= []).Add(line);
                continue;
            }

            if (!groups.TryGetValue(directory, out var names))
                groups.Add(directory, names = []);
            names.Add(name);
        }

        if (groups.Count == 0)
            return text;

        var output = new StringBuilder(text.Length);
        foreach (var (directory, names) in groups)
        {
            names.Sort(StringComparer.Ordinal);
            output.Append(directory).Append(":\n");
            foreach (var name in names)
                output.Append(Indent).Append(name).Append('\n');
        }

        return Finish(output, passthrough, trailingNewline);
    }

    /// <summary>
    /// Splits a path at its last separator, keeping both halves byte-verbatim — the prototype this
    /// was ported from normalized <c>\</c> to <c>/</c> first, which rewrites a path the model may be
    /// about to hand back to a tool. Either separator ends a directory; we do not care which.
    /// </summary>
    private static bool TrySplitPath(string line, out string directory, out string name)
    {
        directory = string.Empty;
        name = string.Empty;

        // Ineligible: our own indented entries and our own headers (proof P).
        if (line.Length == 0 || line[0] == ' ' || line[^1] == ':')
            return false;

        var separator = line.AsSpan().LastIndexOfAny('/', '\\');
        if (separator < 0)
        {
            directory = ".";
            name = line;
            return true;
        }

        // A trailing separator names a directory, not a file in one: there is nothing to group under.
        if (separator == line.Length - 1)
            return false;

        // Keep the root's separator ("/usr" lives in "/", not in "").
        directory = separator == 0 ? line[..1] : line[..separator];
        name = line[(separator + 1)..];
        return true;
    }

    // ---------------------------------------------------------------------
    // Shared line model — \n only, because Apply already fail-opened on any CR.
    // ---------------------------------------------------------------------

    /// <summary>
    /// Splits <paramref name="text"/> (never empty here) into lines. A trailing newline terminates
    /// the last line rather than starting an empty one, so <paramref name="count"/> may be one less
    /// than the returned array's length.
    /// </summary>
    private static string[] SplitLines(string text, out int count, out bool trailingNewline)
    {
        var lines = text.Split('\n');
        trailingNewline = text[^1] == '\n';
        count = trailingNewline ? lines.Length - 1 : lines.Length;
        return lines;
    }

    /// <summary>
    /// Appends the lines no group claimed and restores the payload's own framing: every line above
    /// was written with a terminator, so an input that did not end in one gives its last line back.
    /// </summary>
    private static string Finish(StringBuilder output, List<string>? passthrough, bool trailingNewline)
    {
        if (passthrough is not null)
        {
            foreach (var line in passthrough)
                output.Append(line).Append('\n');
        }

        if (!trailingNewline)
            output.Length--; // safe: a group was emitted, so the buffer ends in '\n'

        return output.ToString();
    }
}
