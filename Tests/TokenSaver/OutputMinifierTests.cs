using System.Text;
using TokenSaver.Minify;
using Xunit;

namespace Tests.TokenSaver;

/// <summary>
/// Unit tests for <see cref="OutputMinifier"/> — the four transforms of token_saving_plan.md §3,
/// plus the litmus properties of §1 (lossless / deterministic / idempotent / fail-open) that the
/// class doc promises. ESC is written as "\u001b" via the Esc const throughout (never \x1b — a following hex digit
/// would silently extend the escape in C# source).
/// </summary>
public class OutputMinifierTests
{
    private const string Esc = "\u001b";

    private static readonly MinifyFlags T1Only = new(
        CollapseCrRedraws: true, StripAnsiStyling: false, StripTrailingWhitespace: false,
        TrimBlankLineEdges: false, CollapseBlankLineRuns: false);

    private static readonly MinifyFlags T3Only = new(
        CollapseCrRedraws: false, StripAnsiStyling: false, StripTrailingWhitespace: true,
        TrimBlankLineEdges: false, CollapseBlankLineRuns: false);

    private static readonly MinifyFlags AllOn = new(
        CollapseCrRedraws: true, StripAnsiStyling: true, StripTrailingWhitespace: true,
        TrimBlankLineEdges: true, CollapseBlankLineRuns: true);

    // ---------------------------------------------------------------------
    // T1 — CR-redraw collapse
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("a\rb", "b")]
    [InlineData("a\r\rb", "b")]
    // Trailing bare \r = parked cursor on the FINAL (unterminated) line: the last
    // non-empty frame is what remained visible. (With a \n after it, the \r would be
    // CRLF framing instead — see the CRLF tests.)
    [InlineData("foo\r", "foo")]
    [InlineData("\r\r", "")]
    [InlineData("11%|##\r64%|####\rdone: 100%", "done: 100%")]
    [InlineData("load\r|\r/\r-\r\\\r|\rdone!", "done!")]
    public void T1_CrCollapse_KeepsLastNonEmptyFrame_WhenItCoversAllFrames(string input, string expected)
    {
        Assert.Equal(expected, OutputMinifier.Minify(input, T1Only));
        Assert.Equal(expected, OutputMinifier.Minify(input, MinifyFlags.Default));
    }

    [Theory]
    // \r doesn't erase: a real terminal shows "doneress 99%" — collapsing to "done"
    // would delete text the model was sent (adopted review finding, 2026-07-12).
    [InlineData("progress 99%\rdone")]
    // Even trailing spaces don't cover the 10 columns of the first frame.
    [InlineData("wide frame\rok   ")]
    // Tab jumps columns — width unprovable.
    [InlineData("ab\tcd\rxxxxxxxx")]
    // Cursor-forward CSI jumps columns — width unprovable.
    [InlineData("ab" + Esc + "[3Ccd\rxxxxxxxx")]
    // Styling in a preserved line stays too: partial deletions inside a multi-frame
    // line can make a mid-line \r line-final and break idempotency on the next pass.
    [InlineData("x " + Esc + "[31mlong text" + Esc + "[0m\rok")]
    public void T1_UncoveredFinalFrame_KeepsLineFullyVerbatim(string input)
    {
        Assert.Same(input, OutputMinifier.Minify(input, MinifyFlags.Default));
        Assert.Same(input, OutputMinifier.Minify(input, T1Only));
    }

    [Fact]
    public void T1_SgrIsZeroWidth_ForTheCoverageProof()
    {
        // "bad"(3 cols) is covered by "good"(4 cols) — the survivor's SGR codes carry no
        // columns, and the collapse then strips them as usual.
        Assert.Equal(
            "good",
            OutputMinifier.Minify("bad\r" + Esc + "[32mgood" + Esc + "[0m", MinifyFlags.Default));
    }

    [Fact]
    public void T1_TrailingSpacesInSurvivor_CountAsCover_ThenStripAsUsual()
    {
        // The survivor "done      " (10 cols incl. spaces) fully covers "progressXX" — the human
        // saw "done" and blanks. Collapse happens on the padded width; T3 then strips the pad.
        Assert.Equal("done", OutputMinifier.Minify("progressXX\rdone      ", MinifyFlags.Default));
    }

    [Fact]
    public void T1_Crlf_NormalizedToLf_WhenOn()
    {
        Assert.Equal("a\nb", OutputMinifier.Minify("a\r\nb", MinifyFlags.Default));
    }

    [Fact]
    public void T1_Crlf_PreservedByteVerbatim_WhenOff()
    {
        var input = "a\r\nb";
        var flags = MinifyFlags.Default with { CollapseCrRedraws = false };
        var result = OutputMinifier.Minify(input, flags);
        Assert.Same(input, result); // no other transform applies → original instance back
    }

    [Fact]
    public void T1_FinalLineWithoutNewline_KeepsContent()
    {
        var input = "first\nsecond";
        Assert.Same(input, OutputMinifier.Minify(input, MinifyFlags.Default));
    }

    [Fact]
    public void T1_BlankCrlfLineBetweenContent_CollapsesToLf()
    {
        Assert.Equal("a\n\nb", OutputMinifier.Minify("a\r\n\r\nb", MinifyFlags.Default));
    }

    // ---------------------------------------------------------------------
    // T2 — ANSI styling strip (SGR + OSC + BEL only; everything else verbatim)
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(Esc + "[1mA" + Esc + "[0m", "A")]                    // bold on/off
    [InlineData(Esc + "[38;2;10;20;30mX" + Esc + "[m", "X")]         // truecolor + empty-param SGR
    [InlineData(Esc + "]0;title\aX", "X")]                           // OSC, BEL terminator
    [InlineData(Esc + "]8;;http://x" + Esc + "\\X", "X")]            // OSC, ST (ESC \) terminator
    [InlineData("ding\adong", "dingdong")]                           // bare BEL
    public void T2_DropsSgrOscAndBel(string input, string expected)
    {
        Assert.Equal(expected, OutputMinifier.Minify(input, MinifyFlags.Default));
    }

    [Theory]
    [InlineData(Esc + "[2J")]     // erase display
    [InlineData(Esc + "[K")]      // erase to end of line
    [InlineData(Esc + "[10;5H")]  // cursor position
    public void T2_NonSgrCsi_KeptVerbatim(string input)
    {
        Assert.Same(input, OutputMinifier.Minify(input, MinifyFlags.Default));
    }

    [Theory]
    [InlineData(Esc + "(B")]      // charset designation
    [InlineData(Esc + "=")]       // application keypad
    [InlineData("abc" + Esc)]     // lone ESC at end of input
    public void T2_UnknownEscForms_KeptVerbatim(string input)
    {
        Assert.Same(input, OutputMinifier.Minify(input, MinifyFlags.Default));
    }

    [Fact]
    public void T2_KeptCsiFollowedByDroppedSgr_StripsOnlyTheSgr()
    {
        Assert.Equal(Esc + "[2J", OutputMinifier.Minify(Esc + "[2J" + Esc + "[m", MinifyFlags.Default));
    }

    [Theory]
    [InlineData(Esc + "[")]                    // CSI truncated at EOF
    [InlineData(Esc + "[12;")]                 // CSI params truncated at EOF
    [InlineData(Esc + "]0;title")]             // OSC with no terminator
    [InlineData(Esc + "[1" + Esc + "[2m")]     // CSI with embedded control char
    public void T2_MalformedEscape_AbortsWholeString(string input)
    {
        var stats = new MinifyStats();
        var result = OutputMinifier.Minify(input, MinifyFlags.Default, ref stats);

        Assert.Same(input, result); // fail-open: original instance, untouched
        Assert.True(stats.AbortedMalformedEscape);
        Assert.False(stats.Changed);
        AssertAllCountersZero(stats);
    }

    [Fact]
    public void T2_MalformedAbort_EvenWhenRestOfStringHasSafeSavings()
    {
        // Trailing whitespace and a well-formed SGR exist, but the malformed CSI on the
        // same string aborts everything — the WHOLE string comes back untouched.
        var input = "ok  \n" + Esc + "[31mred" + Esc + "[0m\n" + Esc + "[";
        var stats = new MinifyStats();
        var result = OutputMinifier.Minify(input, MinifyFlags.Default, ref stats);

        Assert.Same(input, result);
        Assert.True(stats.AbortedMalformedEscape);
        AssertAllCountersZero(stats); // counters from earlier lines are discarded, not leaked
    }

    // The two idempotency-fabrication aborts from the class doc. Splicing instead of
    // aborting would fabricate a new well-formed sequence a second pass would strip.

    [Fact]
    public void T2_Abort_MalformedCsiThatWouldFabricateSgr()
    {
        // "\e[" + "\e[0m" + "m" — splicing out the inner SGR would fabricate "\e[m".
        var input = Esc + "[" + Esc + "[0mm";
        var stats = new MinifyStats();
        var result = OutputMinifier.Minify(input, MinifyFlags.Default, ref stats);

        Assert.Same(input, result);
        Assert.True(stats.AbortedMalformedEscape);
    }

    [Fact]
    public void T2_Abort_DropAfterKeptBareEsc()
    {
        // ESC + BEL + "[0m" — dropping the BEL would butt the kept ESC against "[0m",
        // fabricating "\e[0m". The whole string must come back unchanged.
        var input = Esc + "\a[0m";
        var stats = new MinifyStats();
        var result = OutputMinifier.Minify(input, MinifyFlags.Default, ref stats);

        Assert.Same(input, result);
        Assert.True(stats.AbortedMalformedEscape);
        AssertAllCountersZero(stats);
    }

    [Fact]
    public void T2_Abort_OscWithEmbeddedEsc_NeverDeletesVisibleText()
    {
        // A real terminal EXITS the OSC state at the embedded ESC and renders "PASS" in green;
        // scanning on to the later BEL would have deleted rendered text (adversarial-review
        // finding, 2026-07-12). Must abort the whole string instead.
        var input = Esc + "]0;building" + Esc + "[32mPASS\a done";
        var stats = new MinifyStats();
        var result = OutputMinifier.Minify(input, MinifyFlags.Default, ref stats);

        Assert.Same(input, result);
        Assert.True(stats.AbortedMalformedEscape);
        AssertAllCountersZero(stats);
    }

    // ---------------------------------------------------------------------
    // Third abort rule — bare CR with T1 off
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(" \r")]          // T3 shields the space behind the \r; edge trim then eats the \r
    [InlineData("a \r")]         // pass 1 "a " → pass 2 "a" without the abort
    [InlineData(" \r \n")]       // T3 makes the mid-line \r line-final → reclassified as CRLF
    [InlineData("\n\n\a\r\a\nx")] // BEL strip leaves a lone-\r line → pass 2 sees a blank CRLF line
    public void T1Off_BareCr_AbortsWholeString(string input)
    {
        // With CR-collapse bisected off, a bare CR's meaning depends on what FOLLOWS it — which
        // other transforms can delete. The only provably idempotent move is not touching the
        // string at all (adversarial-review finding, 2026-07-12).
        var flags = MinifyFlags.Default with { CollapseCrRedraws = false };
        var stats = new MinifyStats();
        var result = OutputMinifier.Minify(input, flags, ref stats);

        Assert.Same(input, result);
        Assert.True(stats.AbortedMalformedEscape);
        AssertAllCountersZero(stats);

        // And the T1 kill switch is honest: with it off, no CR is ever removed.
        Assert.Contains('\r', result);
    }

    [Fact]
    public void T1Off_CrlfOnlyStrings_StillMinify()
    {
        // The bare-CR abort must not disable the other transforms for well-formed CRLF text.
        var flags = MinifyFlags.Default with { CollapseCrRedraws = false };
        Assert.Equal("a\r\nb", OutputMinifier.Minify("a  \r\nb\n", flags));
    }

    // ---------------------------------------------------------------------
    // T3 — trailing whitespace strip
    // ---------------------------------------------------------------------

    [Fact]
    public void T3_TrailingSpaces_Stripped()
    {
        Assert.Equal("text\n", OutputMinifier.Minify("text   \n", T3Only));
        Assert.Equal("text", OutputMinifier.Minify("text   \n", MinifyFlags.Default)); // + edge trim
    }

    [Fact]
    public void T3_TrailingTabs_Stripped()
    {
        Assert.Equal("text\n", OutputMinifier.Minify("text\t\t\n", T3Only));
    }

    [Fact]
    public void T3_RunsAfterSgrRemoval()
    {
        // The spaces only become trailing once the SGR after them is stripped.
        Assert.Equal("text", OutputMinifier.Minify("text  " + Esc + "[0m", MinifyFlags.Default));
    }

    [Fact]
    public void T3_InternalWhitespace_Untouched()
    {
        var input = "a  b";
        Assert.Same(input, OutputMinifier.Minify(input, MinifyFlags.Default));
    }

    // ---------------------------------------------------------------------
    // T4a — blank-line edge trim / T4b — interior blank-run collapse
    // ---------------------------------------------------------------------

    [Fact]
    public void T4a_EdgeBlankLines_Trimmed()
    {
        Assert.Equal("body", OutputMinifier.Minify("\n\nbody\n\n", MinifyFlags.Default));
    }

    [Fact]
    public void T4a_SingleTrailingNewline_Dropped()
    {
        Assert.Equal("a", OutputMinifier.Minify("a\n", MinifyFlags.Default));
    }

    [Fact]
    public void T4b_InteriorBlankRun_CollapsedToTwo_WhenFlagOn()
    {
        var flags = MinifyFlags.Default with { CollapseBlankLineRuns = true };
        Assert.Equal("a\n\n\nb", OutputMinifier.Minify("a\n\n\n\n\nb", flags)); // 4 blanks → 2
    }

    [Fact]
    public void T4b_InteriorBlankRun_LeftAlone_ByDefault()
    {
        var input = "a\n\n\n\n\nb"; // interior run, no edge effects → Default is a full no-op
        Assert.Same(input, OutputMinifier.Minify(input, MinifyFlags.Default));
    }

    // ---------------------------------------------------------------------
    // MinifyStats
    // ---------------------------------------------------------------------

    [Fact]
    public void Stats_HandComputedCounters()
    {
        // "\n"                        → leading blank edge            BlankEdge  +1
        // "a\rb\r\n"                  → "a\rb"→"b" (+2), CRLF \r (+1) CrRedraw   +3
        // "\e[1mbold\e[0m  \n"        → SGR 4+4 (+8), 2 spaces (+2)   Ansi +8, TrailingWs +2
        // "c\n"                       → kept
        // "\n\n\n\n"                  → 4 blanks: keep 2, skip 2      BlankRun   +2
        // "d\n"                       → kept
        // "\n"                        → trailing blank edge (with d's own \n) BlankEdge +2
        var input = "\na\rb\r\n" + Esc + "[1mbold" + Esc + "[0m  \nc\n\n\n\n\nd\n\n";

        var stats = new MinifyStats();
        var result = OutputMinifier.Minify(input, AllOn, ref stats);

        Assert.Equal("b\nbold\nc\n\n\nd", result);
        Assert.Equal(3, stats.CrRedrawChars);
        Assert.Equal(8, stats.AnsiChars);
        Assert.Equal(2, stats.TrailingWhitespaceChars);
        Assert.Equal(3, stats.BlankEdgeChars);
        Assert.Equal(2, stats.BlankRunChars);
        Assert.True(stats.Changed);
        Assert.False(stats.AbortedMalformedEscape);

        // Cross-check: the counters must fully account for every removed char.
        var counted = stats.CrRedrawChars + stats.AnsiChars + stats.TrailingWhitespaceChars
            + stats.BlankEdgeChars + stats.BlankRunChars;
        Assert.Equal(input.Length - result.Length, counted);
    }

    [Fact]
    public void Stats_NotPolluted_WhenStringUnchanged()
    {
        var stats = new MinifyStats();
        var input = "plain text, nothing to minify";
        var result = OutputMinifier.Minify(input, MinifyFlags.Default, ref stats);

        Assert.Same(input, result);
        Assert.False(stats.Changed);
        Assert.False(stats.AbortedMalformedEscape);
        AssertAllCountersZero(stats);
    }

    [Fact]
    public void Stats_AccumulateAcrossCalls()
    {
        var stats = new MinifyStats();
        OutputMinifier.Minify("a \n", MinifyFlags.Default, ref stats); // TrailingWs 1, BlankEdge 1
        OutputMinifier.Minify("b \n", MinifyFlags.Default, ref stats);

        Assert.Equal(2, stats.TrailingWhitespaceChars);
        Assert.Equal(2, stats.BlankEdgeChars);
        Assert.True(stats.Changed);
    }

    // ---------------------------------------------------------------------
    // API contract
    // ---------------------------------------------------------------------

    [Fact]
    public void Minify_ReturnsSameInstance_WhenUnchanged()
    {
        var input = "git status\nOn branch main\nnothing to commit";
        Assert.Same(input, OutputMinifier.Minify(input, MinifyFlags.Default));
        Assert.Same(string.Empty, OutputMinifier.Minify(string.Empty, MinifyFlags.Default));
    }

    [Fact]
    public void TryMinify_DestinationSmallerThanInput_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
        {
            var stats = new MinifyStats();
            OutputMinifier.TryMinify("abc".AsSpan(), MinifyFlags.Default, new char[2], out _, ref stats);
        });
        Assert.Equal("destination", ex.ParamName);
    }

    // ---------------------------------------------------------------------
    // Property tests — all 32 flag combinations × adversarial corpus.
    // Hand-rolled (no property-testing package): the point is idempotency,
    // determinism, and never-grows for every combo; plus a conservation check.
    // ---------------------------------------------------------------------

    // Shared with PipelineGoldenFixtureTests' cross-stage idempotency property.
    internal static readonly string[] AdversarialInputs =
    [
        "",
        "a",
        "a\r",
        "a\r\rb",
        "\r",
        "\r\n",
        "\n\r",
        "a\r\n\r\nb",
        Esc + "[",
        Esc + "[m",
        Esc + "[0;1;2m",
        Esc + "]0;t\a",
        Esc + "]0;t" + Esc + "\\",
        Esc + "\a[0m",
        Esc + "[" + Esc + "[0mm",
        Esc + "[2J" + Esc + "[m",
        "x \t \n",
        "\n\n\n",
        "a\n\n\n\n\nb",
        "héllo\r\nwörld",
        "\U0001D11E surrogate\r\U0001D11E",
        Esc + "(Btext",
        Esc,
        "npm  " + Esc + "[32m✓" + Esc + "[0m done  \r\n",
        "load\r|\r/\r-\r\\\r|\rdone!",
        // Whitespace-adjacent bare CRs: with T1 off, deletions after a mid-line \r used to make it
        // line-final and a second pass reclassified it (adversarial-review finding, 2026-07-12).
        // The bare-CR abort rule now covers these; they stay here to pin idempotency forever.
        " \r",
        "a \r",
        " \r \n",
        "\n\n\a\r\a\nx",
        // OSC with an embedded ESC that is not part of ST: must abort, never over-delete.
        Esc + "]0;building" + Esc + "[32mPASS\a done",
        // Uncovered final frames: preserved fully verbatim (T1 coverage rule).
        "progress 99%\rdone",
        "wide frame\rok   ",
        "x " + Esc + "[31mlong text" + Esc + "[0m\rok",
        "ab\tcd\rxxxxxxxx",
    ];

    public static TheoryData<int> AllFlagCombos()
    {
        var data = new TheoryData<int>();
        for (var bits = 0; bits < 32; bits++)
            data.Add(bits);
        return data;
    }

    private static MinifyFlags FlagsFromBits(int bits) => new(
        CollapseCrRedraws: (bits & 1) != 0,
        StripAnsiStyling: (bits & 2) != 0,
        StripTrailingWhitespace: (bits & 4) != 0,
        TrimBlankLineEdges: (bits & 8) != 0,
        CollapseBlankLineRuns: (bits & 16) != 0);

    [Theory]
    [MemberData(nameof(AllFlagCombos))]
    public void Properties_IdempotentDeterministicNeverGrows(int bits)
    {
        var flags = FlagsFromBits(bits);
        foreach (var input in AdversarialInputs)
        {
            var stats = new MinifyStats();
            var once = OutputMinifier.Minify(input, flags, ref stats);
            var aborted = stats.AbortedMalformedEscape;

            // (1) idempotency: Minify(Minify(x)) == Minify(x)
            var twice = OutputMinifier.Minify(once, flags);
            Assert.True(once == twice,
                $"Not idempotent for flags={bits} input=\"{Show(input)}\": " +
                $"once=\"{Show(once)}\" twice=\"{Show(twice)}\"");

            // (2) determinism: a second independent run is byte-identical
            var again = OutputMinifier.Minify(input, flags);
            Assert.True(once == again,
                $"Not deterministic for flags={bits} input=\"{Show(input)}\"");

            // (3) deletion-only: output never grows
            Assert.True(once.Length <= input.Length,
                $"Output grew for flags={bits} input=\"{Show(input)}\": " +
                $"{input.Length} → {once.Length}");

            // (4) conservation: every output char comes from the input, in order.
            //     With T1 on, the only rewrite is CRLF→LF, so the output is a subsequence
            //     of the CRLF-normalized input. With T1 off (or on the abort fail-open
            //     path, which returns the RAW input — possibly containing \r\n — verbatim)
            //     the reference is the raw input itself.
            var reference = flags.CollapseCrRedraws && !aborted
                ? input.Replace("\r\n", "\n")
                : input;
            Assert.True(IsSubsequence(once, reference),
                $"Output not a subsequence of reference for flags={bits} " +
                $"input=\"{Show(input)}\" output=\"{Show(once)}\"");
        }
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private static void AssertAllCountersZero(in MinifyStats stats)
    {
        Assert.Equal(0, stats.CrRedrawChars);
        Assert.Equal(0, stats.AnsiChars);
        Assert.Equal(0, stats.TrailingWhitespaceChars);
        Assert.Equal(0, stats.BlankEdgeChars);
        Assert.Equal(0, stats.BlankRunChars);
    }

    private static bool IsSubsequence(string candidate, string reference)
    {
        var r = 0;
        foreach (var c in candidate)
        {
            while (r < reference.Length && reference[r] != c)
                r++;
            if (r == reference.Length)
                return false;
            r++;
        }
        return true;
    }

    /// <summary>Renders control characters visibly for assertion failure messages.</summary>
    private static string Show(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            sb.Append(c switch
            {
                '\u001b' => "\\e",
                '\r' => "\\r",
                '\n' => "\\n",
                '\t' => "\\t",
                '\a' => "\\a",
                _ => c.ToString(),
            });
        }
        return sb.ToString();
    }
}
