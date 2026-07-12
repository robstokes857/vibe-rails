using System.Buffers;

namespace TokenSaver.Minify;

/// <summary>
/// The token saver's core lint pass: removes only bytes a real terminal would have discarded from
/// a shell tool's output (token_saving_plan.md §3). Four transforms, fused into one forward
/// line-oriented scan:
///
///   1. CR-redraw collapse — within each line, keep only the final progress-bar frame, and only
///      when it provably covers every earlier frame (a \r returns the cursor to column 0, it does
///      NOT erase — a shorter final frame leaves earlier text visible); normalize CRLF line
///      endings to LF. Unproven lines pass through fully verbatim.
///   2. ANSI styling strip — drop SGR (<c>ESC[…m</c>), OSC (title/hyperlink/…), and BEL. All other
///      escape sequences (cursor moves, erase, DCS, charset, …) are copied byte-verbatim.
///   3. Trailing whitespace strip — spaces/tabs at end of line.
///   4. Blank-line trim — leading/trailing blank lines of the whole payload; optionally collapse
///      runs of ≥3 interior blank lines down to 2.
///
/// Every transform is deletion-only, deterministic, and idempotent — including in combination.
/// Idempotency is load-bearing (the CLI re-sends history every turn; a drifting transform would
/// thrash prompt caching), and two rules make it provable rather than hoped-for:
///
///   • A malformed/truncated escape sequence aborts the WHOLE string (returns it untouched).
///     Splicing around a partial sequence can fabricate a brand-new well-formed sequence that a
///     second pass would then strip (e.g. <c>"\x1b[" + "\x1b[0m" + "m"</c> → <c>"\x1b[m"</c>).
///     This includes an OSC with an embedded ESC that isn't part of the ST terminator: a real
///     terminal EXITS the OSC state on that ESC and renders what follows, so scanning past it to
///     a later BEL would delete visible text.
///   • A deletion immediately after a kept bare ESC also aborts. A kept ESC followed by a dropped
///     BEL/OSC/SGR would butt the ESC against whatever comes next, again fabricating a new
///     sequence (e.g. <c>ESC + BEL + "[0m"</c> → <c>ESC[0m</c>).
///   • With CR-collapse OFF, a string containing a bare CR (a <c>\r</c> not immediately followed
///     by <c>\n</c>) aborts. With T1 disabled, bare CRs are inert content — but T2/T3 deletions
///     after a mid-line CR can make it line-final, and a second pass would then reclassify it as
///     CRLF framing (or edge-trim would eat it), stripping more (e.g. <c>" \r"</c> → <c>" "</c> →
///     <c>""</c>). Aborting also makes the T1 kill switch honest: with it off, NO CR is ever
///     removed, so flag bisection actually isolates T1.
///
/// These cases only occur in pathological input or bisection flag states; aborting costs savings
/// on that one string, never correctness. Do not "improve" any of these rules to
/// copy-and-continue.
/// </summary>
public static class OutputMinifier
{
    private const char Esc = '\x1b';
    private const char Bel = '\a';

    /// <summary>
    /// Minifies <paramref name="toolOutput"/>, returning the original instance when nothing
    /// changed (or when a malformed escape forced the fail-open path). Allocates only when the
    /// output actually shrank.
    /// </summary>
    public static string Minify(string toolOutput, MinifyFlags flags)
    {
        var stats = new MinifyStats();
        return Minify(toolOutput, flags, ref stats);
    }

    /// <summary>Overload that accumulates removal counters across calls (one request = many strings).</summary>
    public static string Minify(string toolOutput, MinifyFlags flags, ref MinifyStats stats)
    {
        if (toolOutput.Length == 0 || flags.IsNoOp)
            return toolOutput;

        var rented = ArrayPool<char>.Shared.Rent(toolOutput.Length);
        try
        {
            return TryMinify(toolOutput, flags, rented, out var written, ref stats)
                ? new string(rented, 0, written)
                : toolOutput;
        }
        finally
        {
            ArrayPool<char>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Writes the minified form of <paramref name="input"/> into <paramref name="destination"/>
    /// (which must be at least <c>input.Length</c> long — every transform is deletion-only, so the
    /// output never grows). Returns true when the output differs from the input; false means
    /// "unchanged or aborted — use the original" and <paramref name="written"/> is 0.
    /// </summary>
    public static bool TryMinify(
        ReadOnlySpan<char> input,
        MinifyFlags flags,
        Span<char> destination,
        out int written,
        ref MinifyStats stats)
    {
        written = 0;
        if (input.IsEmpty || flags.IsNoOp)
            return false;
        if (destination.Length < input.Length)
            throw new ArgumentException(
                "Destination must be at least input.Length chars (transforms are deletion-only).",
                nameof(destination));

        // Third abort rule (see class remarks): bare CRs with T1 off are only idempotent if we
        // never touch the string at all.
        if (!flags.CollapseCrRedraws && ContainsBareCr(input))
        {
            stats.AbortedMalformedEscape = true;
            return false;
        }

        var dstPos = 0;
        var pos = 0;
        var blankRun = 0;
        var pending = stats;

        while (pos < input.Length)
        {
            var remaining = input[pos..];
            var nl = remaining.IndexOf('\n');
            var hasNewline = nl >= 0;
            var rawLine = hasNewline ? remaining[..nl] : remaining;
            pos += hasNewline ? nl + 1 : remaining.Length;

            // The \r of a \r\n pair is line-ending framing, never a redraw; a trailing \r on the
            // FINAL (unterminated) line is a bare CR — a parked cursor — and stays with the content.
            var hasCrLf = hasNewline && rawLine.Length > 0 && rawLine[^1] == '\r';
            var content = hasCrLf ? rawLine[..^1] : rawLine;

            // T1 — CR-redraw collapse. \r only returns the cursor to column 0 — it does NOT erase.
            // A shorter final frame leaves the tail of an earlier frame visible ("progress
            // 99%\rdone" shows "doneress 99%"), so collapsing is only allowed when the surviving
            // frame PROVABLY covers every discarded one. Otherwise the whole line is kept FULLY
            // verbatim — bare \r's, styling, and trailing whitespace included: T2/T3 deletions
            // inside a multi-frame line could make a mid-line \r line-final and a second pass
            // would re-classify it (the same trap the T1-off abort rule closes), so an unproven
            // line must not be touched at all. The keep decision itself is stable across passes
            // because the line's bytes are unchanged.
            var chosen = content;
            var keepLineVerbatim = false;
            if (flags.CollapseCrRedraws && content.Contains('\r'))
            {
                if (TrySelectCoveringSurvivor(content, out var survivor))
                {
                    chosen = survivor;
                    pending.CrRedrawChars += content.Length - chosen.Length;
                }
                else
                {
                    keepLineVerbatim = true;
                }
            }

            // T2 — ANSI styling strip (with the abort rules documented above).
            var lineStart = dstPos;
            if (!keepLineVerbatim && flags.StripAnsiStyling && chosen.IndexOfAny(Esc, Bel) >= 0)
            {
                if (!CopyStrippingAnsi(chosen, destination, ref dstPos, ref pending))
                {
                    stats.AbortedMalformedEscape = true;
                    written = 0;
                    return false;
                }
            }
            else
            {
                chosen.CopyTo(destination[dstPos..]);
                dstPos += chosen.Length;
            }

            // T3 — trailing whitespace: backtrack the write position over spaces/tabs.
            if (!keepLineVerbatim && flags.StripTrailingWhitespace)
            {
                while (dstPos > lineStart && destination[dstPos - 1] is ' ' or '\t')
                {
                    dstPos--;
                    pending.TrailingWhitespaceChars++;
                }
            }

            // T4b — collapse interior blank-line runs: from the 3rd consecutive blank line on,
            // skip the line entirely (runs of ≥3 → exactly 2).
            var isBlank = dstPos == lineStart;
            blankRun = isBlank ? blankRun + 1 : 0;
            if (flags.CollapseBlankLineRuns && isBlank && blankRun >= 3 && hasNewline)
            {
                // A skipped CRLF blank drops two chars: the \r is either T1's (normalization owns
                // it) or this transform's (T1 off would have re-emitted it) — count accordingly so
                // the diagnostic counters always sum to exactly the chars removed.
                if (hasCrLf && flags.CollapseCrRedraws)
                    pending.CrRedrawChars += 1;
                else if (hasCrLf)
                    pending.BlankRunChars += 1;
                pending.BlankRunChars += 1;
                continue;
            }

            if (hasNewline)
            {
                // T1 owns CRLF→LF normalization; with it off, the original ending is restored.
                if (hasCrLf && !flags.CollapseCrRedraws)
                    destination[dstPos++] = '\r';
                else if (hasCrLf)
                    pending.CrRedrawChars += 1;
                destination[dstPos++] = '\n';
            }
        }

        // T4a — edge trim: drop blank lines (bare \n / \r\n) at both ends of the whole payload.
        var start = 0;
        if (flags.TrimBlankLineEdges)
        {
            while (dstPos > start && destination[dstPos - 1] is '\n' or '\r')
            {
                dstPos--;
                pending.BlankEdgeChars++;
            }

            while (start < dstPos && destination[start] is '\n' or '\r')
            {
                start++;
                pending.BlankEdgeChars++;
            }
        }

        var length = dstPos - start;

        // Deletion-only transforms: any change shows up as a shorter output, byte-identical
        // otherwise — so length alone decides "changed".
        if (length == input.Length)
        {
            written = 0;
            return false;
        }

        if (start > 0)
            destination.Slice(start, length).CopyTo(destination);

        written = length;
        pending.Changed = true;
        stats = pending;
        return true;
    }

    /// <summary>
    /// Picks the frame a terminal would actually display: the text after the last bare CR — unless
    /// that frame is empty (a spinner parked at column 0, e.g. <c>"foo\r"</c>), in which case the
    /// last non-empty frame is what remained visible.
    /// </summary>
    private static ReadOnlySpan<char> SelectFinalFrame(ReadOnlySpan<char> content)
    {
        var end = content.Length;
        while (true)
        {
            var lastCr = content[..end].LastIndexOf('\r');
            var frame = content[(lastCr + 1)..end];
            if (!frame.IsEmpty || lastCr < 0)
                return frame;
            end = lastCr;
        }
    }

    /// <summary>
    /// True only when the survivor frame is provably at least as wide as EVERY frame on the line —
    /// i.e. the final overwrite covered all earlier text and nothing a human could still see is
    /// being dropped. Widths are visible-char counts with well-formed SGR/OSC/BEL treated as zero
    /// width (they carry no columns and T2 may strip them). Anything that makes column arithmetic
    /// unprovable — cursor-move CSI, tabs, other control chars, unknown ESC forms — fails the
    /// proof and the caller keeps the line verbatim. (Known approximation: widths are UTF-16 char
    /// counts, so a double-width CJK glyph counts 1 — real progress output is overwhelmingly
    /// monospace-ASCII, and the failure mode is keeping a few stale chars a terminal also showed.)
    /// </summary>
    private static bool TrySelectCoveringSurvivor(
        ReadOnlySpan<char> content, out ReadOnlySpan<char> survivor)
    {
        survivor = SelectFinalFrame(content);
        if (!TryVisibleWidth(survivor, out var survivorWidth))
            return false;

        var remaining = content;
        while (true)
        {
            var cr = remaining.IndexOf('\r');
            var frame = cr < 0 ? remaining : remaining[..cr];
            if (!TryVisibleWidth(frame, out var width) || width > survivorWidth)
                return false;
            if (cr < 0)
                return true;
            remaining = remaining[(cr + 1)..];
        }
    }

    /// <summary>
    /// Counts a frame's visible columns (see <see cref="TrySelectCoveringSurvivor"/> for the width
    /// model). Returns false when the frame contains anything whose width is not provable.
    /// </summary>
    private static bool TryVisibleWidth(ReadOnlySpan<char> frame, out int width)
    {
        width = 0;
        var i = 0;
        while (i < frame.Length)
        {
            var c = frame[i];
            if (c == Esc)
            {
                if (i + 1 >= frame.Length)
                    return false; // truncated

                int seqLength;
                if (frame[i + 1] == '[')
                {
                    seqLength = MeasureCsi(frame[i..], out var isSgr);
                    if (seqLength < 0 || !isSgr)
                        return false; // truncated, or a cursor/erase command that jumps columns
                }
                else if (frame[i + 1] == ']')
                {
                    seqLength = MeasureOsc(frame[i..], out _);
                    if (seqLength < 0)
                        return false;
                }
                else
                {
                    return false; // unknown ESC family — width unprovable
                }

                i += seqLength;
                continue;
            }

            if (c == Bel)
            {
                i++;
                continue;
            }

            if (char.IsControl(c))
                return false; // tabs & friends jump columns — unprovable

            width++;
            i++;
        }

        return true;
    }

    /// <summary>
    /// Copies <paramref name="line"/> into <paramref name="destination"/>, dropping SGR sequences,
    /// OSC sequences (either terminator form), and bare BEL; everything else — including non-SGR
    /// CSI and unrecognized ESC forms — is copied byte-verbatim. Returns false to abort the whole
    /// string (malformed sequence, or a drop directly after a kept ESC — see class remarks).
    /// </summary>
    private static bool CopyStrippingAnsi(
        ReadOnlySpan<char> line,
        Span<char> destination,
        ref int dstPos,
        ref MinifyStats stats)
    {
        var i = 0;
        while (i < line.Length)
        {
            // Fast path: bulk-copy up to the next candidate control character.
            var next = line[i..].IndexOfAny(Esc, Bel);
            if (next < 0)
            {
                line[i..].CopyTo(destination[dstPos..]);
                dstPos += line.Length - i;
                return true;
            }

            line.Slice(i, next).CopyTo(destination[dstPos..]);
            dstPos += next;
            i += next;

            if (line[i] == Bel)
            {
                if (EndsWithEsc(destination, dstPos))
                    return false;
                stats.AnsiChars += 1;
                i += 1;
                continue;
            }

            // ESC with nothing after it, or introducing a sequence family we don't handle
            // (DCS, charset designation, single-char ESC commands, …): copy the ESC verbatim and
            // keep scanning — v1 only owns SGR/OSC/BEL.
            if (i + 1 >= line.Length || (line[i + 1] != '[' && line[i + 1] != ']'))
            {
                destination[dstPos++] = line[i];
                i += 1;
                continue;
            }

            var seqLength = line[i + 1] == '['
                ? MeasureCsi(line[i..], out var isSgr)
                : MeasureOsc(line[i..], out isSgr);
            if (seqLength < 0)
                return false; // malformed/truncated → abort the whole string

            if (isSgr)
            {
                if (EndsWithEsc(destination, dstPos))
                    return false;
                stats.AnsiChars += seqLength;
            }
            else
            {
                line.Slice(i, seqLength).CopyTo(destination[dstPos..]);
                dstPos += seqLength;
            }

            i += seqLength;
        }

        return true;
    }

    private static bool EndsWithEsc(ReadOnlySpan<char> destination, int dstPos) =>
        dstPos > 0 && destination[dstPos - 1] == Esc;

    /// <summary>True when the input contains a CR that is not the first half of a CRLF pair.</summary>
    private static bool ContainsBareCr(ReadOnlySpan<char> input)
    {
        var i = 0;
        while (true)
        {
            var cr = input[i..].IndexOf('\r');
            if (cr < 0)
                return false;
            i += cr + 1;
            if (i >= input.Length || input[i] != '\n')
                return true;
            i++;
        }
    }

    /// <summary>
    /// Measures the CSI sequence at the start of <paramref name="text"/> (which begins <c>ESC[</c>).
    /// <paramref name="drop"/> is true only for SGR (final byte <c>m</c>). Returns -1 when the
    /// sequence is truncated or contains a byte outside the CSI grammar.
    /// </summary>
    private static int MeasureCsi(ReadOnlySpan<char> text, out bool drop)
    {
        drop = false;
        var i = 2; // skip ESC [
        while (i < text.Length)
        {
            var c = text[i];
            if (c >= '\x40' && c <= '\x7e')
            {
                drop = c == 'm';
                return i + 1;
            }

            if (c < '\x20' || c > '\x3f')
                return -1; // not a valid parameter/intermediate byte

            i++;
        }

        return -1; // ran out of input before a final byte
    }

    /// <summary>
    /// Measures the OSC sequence at the start of <paramref name="text"/> (which begins <c>ESC]</c>),
    /// including its BEL or <c>ESC\</c> terminator. OSC never survives, so <paramref name="drop"/>
    /// is always true on success. Returns -1 (abort) when no terminator arrives (truncated) OR when
    /// an embedded ESC isn't part of the ST terminator — a real terminal exits the OSC state on
    /// that ESC and renders what follows, so scanning past it to a later BEL would silently delete
    /// visible text.
    /// </summary>
    private static int MeasureOsc(ReadOnlySpan<char> text, out bool drop)
    {
        drop = true;
        var i = 2; // skip ESC ]
        while (i < text.Length)
        {
            var c = text[i];
            if (c == Bel)
                return i + 1;
            if (c == Esc)
                return i + 1 < text.Length && text[i + 1] == '\\' ? i + 2 : -1;
            i++;
        }

        return -1;
    }
}
