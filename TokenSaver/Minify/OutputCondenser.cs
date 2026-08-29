using System.Buffers;

namespace TokenSaver.Minify;

/// <summary>
/// The token saver's lossy second stage, run per tool_result string AFTER
/// <see cref="OutputMinifier"/>: <c>condense(minify(text))</c>. Two line-oriented transforms:
///
///   D. Consecutive-duplicate collapse — a run of ≥3 identical lines becomes one instance with a
///      <c>" [xN]"</c> suffix marker (<c>warning: EOL package [x37]</c>).
///   T. Head/tail truncation — a payload whose middle (after the first 150 and last 50 lines) is
///      both ≥10 lines and ≥4096 chars has that middle replaced by one
///      <c>[... N lines elided ...]</c> marker line. Runs after D, so lossless collapse gets first
///      shot at shrinking the payload below the elision threshold. The 150/50 budget widens to
///      1200/200 when the producing command reads file contents
///      (<see cref="CondenseOptions.PreserveVerbatimFileContents"/>): T cannot tell a 450-line
///      source dump from 450 lines of build spew, and cutting the middle out of source the model is
///      about to quote back is a correctness bug, not a lost saving.
///
/// Both transforms INSERT marker text, so unlike the minifier the output is not a subsequence of
/// the input — which is exactly why they are not <see cref="MinifyFlags"/> members (that would
/// void the minifier's deletion-only proof). The invariants that hold here instead, each pinned by
/// property tests:
///
///   • Deterministic: a pure function of the string. The CLI re-sends its original history every
///     turn, so the same tool_result must condense to the same bytes forever or upstream prompt
///     caching thrashes.
///   • Idempotent: condense(condense(x)) == condense(x). D's keystone is marker-shaped lines being
///     ineligible to start or join a run — every line a collapse emits ends in the marker shape, so
///     no second-pass run can form (naive run-collapse is NOT idempotent: two adjacent genuine
///     <c>"x [x3]"</c> lines followed by three <c>"x"</c> lines would re-collapse). T's is
///     arithmetic: truncated output has exactly keepHead+1+keepTail lines, so a re-pass AT THE SAME
///     BUDGET sees a 1-line middle and never fires — budget-independent, and the budget is a pure
///     function of the producing command, which is itself stable across turns.
///     Genuine lines that happen to end in marker shape merely lose eligibility —
///     a savings loss, never a correctness loss.
///   • Never grows: D collapses only when strictly profitable; T removes ≥4096 chars to insert a
///     ~30-char line.
///   • Whole-string fail-open: input containing ESC, BEL, or any CR is returned untouched. This
///     composes with the minifier's abort rules (anything IT aborted on still carries those bytes),
///     keeps line edits away from escape sequences and CR frames the minifier deliberately kept
///     verbatim (deleting or inserting next to them is the same fabricate-a-new-sequence /
///     reclassify-a-CR trap its abort rules close), and leaves this class a trivial \n-only line
///     model. Every condense-enabled preset also enables CR-collapse (which normalizes CRLF), so a
///     surviving CR here is pathological input. Do not "improve" this to skip-and-continue.
/// </summary>
public static class OutputCondenser
{
    /// <summary>Minimum identical-line run that may collapse. MUST stay ≥3: at 2, deleting the
    /// duplicate can make two previously separated copies adjacent on a later pass.</summary>
    internal const int MinRunLength = 3;

    internal const int KeepHeadLines = 150;
    internal const int KeepTailLines = 50;

    /// <summary>
    /// T's budget when the producing command reads file contents
    /// (<see cref="CondenseOptions.PreserveVerbatimFileContents"/>). Wider, not infinite: T's real
    /// job is catastrophic-payload defence, and <c>cat</c> of a 50k-line generated file still has to
    /// be capped or it blows the context window T exists to protect.
    ///
    /// 1200/200 is calibrated from measured traffic, not taste. Across 8 000 proxied requests the
    /// file-read payloads that were being cut ran 194–967 elided lines, i.e. 394–1167 lines total;
    /// a 1400-line budget passes every one of them through verbatim while still capping beyond that.
    /// Re-derive from captures before changing it — see runbooks/token_saver/truncation_file_reads.md.
    /// </summary>
    internal const int VerbatimKeepHeadLines = 1200;
    internal const int VerbatimKeepTailLines = 200;

    /// <summary>The middle must be at least this many lines AND this many chars to be elided —
    /// jointly, so a huge single-line payload (one JSON blob) is never cut.</summary>
    internal const int MinElidedLines = 10;
    internal const int MinElidedChars = 4096;

    private const string DedupMarkerPrefix = " [x";
    private const string ElisionPrefix = "[... ";
    private const string ElisionSuffix = " lines elided ...]";

    /// <summary>
    /// Condenses <paramref name="toolOutput"/>, returning the original instance when nothing
    /// changed (or when a control character forced the fail-open path).
    /// </summary>
    public static string Condense(string toolOutput, CondenseOptions options)
    {
        var stats = new CondenseStats();
        return Condense(toolOutput, options, ref stats);
    }

    /// <summary>Overload that accumulates counters across calls (one request = many strings).</summary>
    public static string Condense(string toolOutput, CondenseOptions options, ref CondenseStats stats)
    {
        if (toolOutput.Length == 0 || options.IsNoOp)
            return toolOutput;

        var rented = ArrayPool<char>.Shared.Rent(toolOutput.Length);
        try
        {
            return TryCondense(toolOutput, options, rented, out var written, ref stats)
                ? new string(rented, 0, written)
                : toolOutput;
        }
        finally
        {
            ArrayPool<char>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Writes the condensed form of <paramref name="input"/> into <paramref name="destination"/>
    /// (which must be at least <c>input.Length</c> long — every applied action strictly shrinks).
    /// Returns true when the output differs from the input; false means "unchanged or aborted —
    /// use the original" and <paramref name="written"/> is 0.
    /// </summary>
    public static bool TryCondense(
        ReadOnlySpan<char> input,
        CondenseOptions options,
        Span<char> destination,
        out int written,
        ref CondenseStats stats)
    {
        written = 0;
        if (input.IsEmpty || options.IsNoOp)
            return false;
        if (destination.Length < input.Length)
            throw new ArgumentException(
                "Destination must be at least input.Length chars (condensing never grows).",
                nameof(destination));

        // The whole-string fail-open documented in the class remarks.
        if (input.IndexOfAny('\x1b', '\a', '\r') >= 0)
        {
            stats.AbortedControlChars = true;
            return false;
        }

        var pending = stats;

        // D writes into destination; with D off, a verbatim copy keeps T a single in-place path.
        int length;
        if (options.DedupeConsecutiveLines)
        {
            length = Dedupe(input, destination, ref pending);
        }
        else
        {
            input.CopyTo(destination);
            length = input.Length;
        }

        if (options.TruncateLongOutput)
        {
            var (head, tail) = options.PreserveVerbatimFileContents
                ? (VerbatimKeepHeadLines, VerbatimKeepTailLines)
                : (KeepHeadLines, KeepTailLines);
            length = TruncateInPlace(destination, length, head, tail, ref pending);
        }

        // Every applied action strictly shrinks, byte-identical otherwise — so length alone
        // decides "changed" (same contract as the minifier).
        if (length == input.Length)
        {
            written = 0;
            return false;
        }

        written = length;
        pending.Changed = true;
        stats = pending;
        return true;
    }

    /// <summary>
    /// Transform D: streaming single pass over \n-separated lines (the final line may be
    /// unterminated), collapsing each profitable run of ≥<see cref="MinRunLength"/> identical
    /// eligible lines to one marker-suffixed instance. Returns the output length.
    /// </summary>
    private static int Dedupe(ReadOnlySpan<char> input, Span<char> destination, ref CondenseStats stats)
    {
        var dstPos = 0;
        var pos = 0;

        // The current run: byte extent in input (for verbatim flushes), one line's content, count,
        // and whether the run's last line carried a \n.
        var runStart = 0;
        var runEnd = 0;
        var runCount = 0;
        var runTerminated = false;
        ReadOnlySpan<char> runLine = default;

        while (pos < input.Length)
        {
            var lineStart = pos;
            var remaining = input[pos..];
            var nl = remaining.IndexOf('\n');
            var hasNewline = nl >= 0;
            var content = hasNewline ? remaining[..nl] : remaining;
            pos += hasNewline ? nl + 1 : remaining.Length;

            if (runCount > 0 && content.SequenceEqual(runLine))
            {
                runCount++;
                runEnd = pos;
                runTerminated = hasNewline;
                continue;
            }

            FlushRun(input, destination, ref dstPos, runStart, runEnd, runLine, runCount,
                runTerminated, ref stats);

            if (IsDedupEligible(content))
            {
                runStart = lineStart;
                runEnd = pos;
                runLine = content;
                runCount = 1;
                runTerminated = hasNewline;
            }
            else
            {
                // Ineligible lines (blank, or marker-shaped — see class remarks) pass through
                // verbatim and break any run.
                runCount = 0;
                input[lineStart..pos].CopyTo(destination[dstPos..]);
                dstPos += pos - lineStart;
            }
        }

        FlushRun(input, destination, ref dstPos, runStart, runEnd, runLine, runCount,
            runTerminated, ref stats);
        return dstPos;
    }

    private static void FlushRun(
        ReadOnlySpan<char> input,
        Span<char> destination,
        ref int dstPos,
        int runStart,
        int runEnd,
        ReadOnlySpan<char> runLine,
        int runCount,
        bool runTerminated,
        ref CondenseStats stats)
    {
        if (runCount == 0)
            return;

        // Chars saved = (K-1)*(lineLen+1) - markerLen, for terminated and unterminated tails alike.
        // Collapse only when strictly positive — "a\na\na" (5 chars) must not become "a [x3]" (6).
        var markerLength = DedupMarkerPrefix.Length + Digits(runCount) + 1;
        var saved = (runCount - 1) * (runLine.Length + 1) - markerLength;
        if (runCount < MinRunLength || saved <= 0)
        {
            input[runStart..runEnd].CopyTo(destination[dstPos..]);
            dstPos += runEnd - runStart;
            return;
        }

        runLine.CopyTo(destination[dstPos..]);
        dstPos += runLine.Length;
        DedupMarkerPrefix.CopyTo(destination[dstPos..]);
        dstPos += DedupMarkerPrefix.Length;
        runCount.TryFormat(destination[dstPos..], out var digits);
        dstPos += digits;
        destination[dstPos++] = ']';
        if (runTerminated)
            destination[dstPos++] = '\n';

        stats.DedupRunsCollapsed++;
        stats.DedupNetChars += saved;
    }

    /// <summary>
    /// A line may start or join a dedup run unless it is blank (blank runs belong to the
    /// minifier's T4 transforms; a marker on a blank line would fabricate content) or it already
    /// carries either marker shape — the rule that makes collapse output a fixed point.
    /// </summary>
    private static bool IsDedupEligible(ReadOnlySpan<char> line) =>
        !line.IsEmpty && !EndsWithDedupMarker(line) && !IsElisionMarkerLine(line);

    /// <summary>Matches the <c>" [x" digits "]"</c> suffix, scanned right-to-left.</summary>
    private static bool EndsWithDedupMarker(ReadOnlySpan<char> line)
    {
        if (line.Length < DedupMarkerPrefix.Length + 2 || line[^1] != ']')
            return false;

        var i = line.Length - 2;
        var digits = 0;
        while (i >= 0 && char.IsAsciiDigit(line[i]))
        {
            i--;
            digits++;
        }

        return digits > 0
            && i >= 2
            && line[i] == 'x'
            && line[i - 1] == '['
            && line[i - 2] == ' ';
    }

    /// <summary>Matches a whole line of the form <c>"[... " digits " lines elided ...]"</c>.</summary>
    private static bool IsElisionMarkerLine(ReadOnlySpan<char> line)
    {
        if (line.Length < ElisionPrefix.Length + 1 + ElisionSuffix.Length
            || !line.StartsWith(ElisionPrefix)
            || !line.EndsWith(ElisionSuffix))
        {
            return false;
        }

        var digits = line[ElisionPrefix.Length..^ElisionSuffix.Length];
        foreach (var c in digits)
        {
            if (!char.IsAsciiDigit(c))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Transform T, in place: replaces the middle of <paramref name="destination"/>'s first
    /// <paramref name="length"/> chars with the elision marker line when the thresholds fire.
    /// In-place is safe: the marker end stays short of the tail start (middle ≥4096 chars, marker
    /// ~30), and the tail copy moves chars strictly leftward (memmove semantics). Returns the new
    /// length (or <paramref name="length"/> unchanged).
    ///
    /// <paramref name="keepHead"/>/<paramref name="keepTail"/> are the budget, not constants, so a
    /// file-read payload can keep more (see <see cref="VerbatimKeepHeadLines"/>). The idempotency
    /// argument is budget-independent and stays intact: output is exactly
    /// <c>keepHead + 1 + keepTail</c> lines, so a re-pass at the SAME budget computes a 1-line middle
    /// and declines. It is per-payload budget STABILITY that the proof needs, and that holds because
    /// the budget is a pure function of the producing command, which the CLI re-sends verbatim every
    /// turn — the same requirement prompt caching already imposes on every other stage.
    /// </summary>
    private static int TruncateInPlace(
        Span<char> destination, int length, int keepHead, int keepTail, ref CondenseStats stats)
    {
        var text = (ReadOnlySpan<char>)destination[..length];

        // First scan: total \n count, plus the head boundary (position after the keepHead'th \n).
        var newlines = 0;
        var headEnd = -1;
        var pos = 0;
        while (true)
        {
            var nl = text[pos..].IndexOf('\n');
            if (nl < 0)
                break;
            pos += nl + 1;
            newlines++;
            if (newlines == keepHead)
                headEnd = pos;
        }

        // An unterminated tail segment is a line; a trailing \n does not start one.
        var totalLines = newlines + (text[^1] != '\n' ? 1 : 0);
        var middleLines = totalLines - keepHead - keepTail;
        if (headEnd < 0 || middleLines < MinElidedLines)
            return length;

        // Second scan: the tail boundary — the start of line index (totalLines - keepTail),
        // i.e. the position after that many \n's. Resumes from the head boundary.
        var tailStart = headEnd;
        var seen = keepHead;
        var target = totalLines - keepTail;
        while (seen < target)
        {
            var nl = text[tailStart..].IndexOf('\n');
            tailStart += nl + 1;
            seen++;
        }

        if (tailStart - headEnd < MinElidedChars)
            return length;

        var w = headEnd;
        ElisionPrefix.CopyTo(destination[w..]);
        w += ElisionPrefix.Length;
        middleLines.TryFormat(destination[w..], out var digits);
        w += digits;
        ElisionSuffix.CopyTo(destination[w..]);
        w += ElisionSuffix.Length;
        destination[w++] = '\n';

        destination[tailStart..length].CopyTo(destination[w..]);
        var newLength = w + (length - tailStart);

        stats.TruncationsApplied++;
        stats.TruncationNetChars += length - newLength;
        return newLength;
    }

    private static int Digits(int value)
    {
        var digits = 1;
        while (value >= 10)
        {
            value /= 10;
            digits++;
        }

        return digits;
    }
}
