using System.Text;
using TokenSaver.Minify;
using Xunit;

namespace Tests.TokenSaver;

/// <summary>
/// Unit tests for <see cref="OutputCondenser"/> — the lossy second stage. The bar here is the
/// class doc's restated invariants (deterministic / idempotent / never grows / fail-open), NOT the
/// minifier's subsequence conservation: the condenser inserts marker text by design. The two
/// non-recollapse pins are load-bearing — they are why marker-shaped lines must stay ineligible
/// and why MinRunLength must stay ≥3.
/// </summary>
public class OutputCondenserTests
{
    private const string Esc = "\u001b";

    private static readonly CondenseOptions DedupOnly = new(
        DedupeConsecutiveLines: true, TruncateLongOutput: false);

    private static readonly CondenseOptions TruncateOnly = new(
        DedupeConsecutiveLines: false, TruncateLongOutput: true);

    private static readonly CondenseOptions BothOn = new(
        DedupeConsecutiveLines: true, TruncateLongOutput: true);

    // ---------------------------------------------------------------------
    // D — consecutive-duplicate-line collapse
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("spam\nspam\nspam\n", "spam [x3]\n")]
    [InlineData("spam\nspam\nspam\nrest\n", "spam [x3]\nrest\n")]
    [InlineData("a\na\na\na", "a [x4]")] // saved = 3*2 - 5 = 1: barely profitable
    [InlineData("xy\nxy\nxy", "xy [x3]")] // unterminated tail: no trailing \n on the marker line
    [InlineData("aaa\naaa\naaa\nb\naaa\naaa\naaa", "aaa [x3]\nb\naaa [x3]")]
    public void D_CollapsesProfitableRuns(string input, string expected)
    {
        Assert.Equal(expected, OutputCondenser.Condense(input, DedupOnly));
    }

    [Fact]
    public void D_MultiDigitCount()
    {
        var input = string.Concat(Enumerable.Repeat("log line\n", 12));
        Assert.Equal("log line [x12]\n", OutputCondenser.Condense(input, DedupOnly));
    }

    [Theory]
    [InlineData("spam\nspam\n")] // run of 2: below MinRunLength
    [InlineData("a\na\na")] // saved = 2*2 - 5 = -1: unprofitable, must stay verbatim
    [InlineData("\n\n\n\n")] // blank lines are ineligible (the minifier's T4 owns blank runs)
    [InlineData("one\ntwo\nthree\n")] // no runs at all
    public void D_LeavesIneligibleOrUnprofitableRunsVerbatim(string input)
    {
        Assert.Same(input, OutputCondenser.Condense(input, DedupOnly));
    }

    [Fact]
    public void D_MarkerSuffixedLines_NeverRecollapse()
    {
        // Three identical lines that already end in the dedup-marker shape: ineligible, so the
        // output of a collapse can never itself collapse (idempotency keystone, pin #1).
        var input = "x [x3]\nx [x3]\nx [x3]";
        Assert.Same(input, OutputCondenser.Condense(input, DedupOnly));
    }

    [Fact]
    public void D_MarkerAdjacentRun_CollapsesOnceThenReachesFixedPoint()
    {
        // Pin #2: genuine marker-suffixed lines followed by a fresh run. The fresh run collapses,
        // producing a third identical marker line adjacent to the first two — which must NOT
        // re-collapse on a second pass (this is the case that makes naive run-collapse
        // non-idempotent).
        var input = "abc [x3]\nabc [x3]\nabc\nabc\nabc";
        var once = OutputCondenser.Condense(input, DedupOnly);
        Assert.Equal("abc [x3]\nabc [x3]\nabc [x3]", once);
        Assert.Same(once, OutputCondenser.Condense(once, DedupOnly));
    }

    [Fact]
    public void D_ForgedElisionMarkerLines_AreIneligible()
    {
        var input = "[... 5 lines elided ...]\n[... 5 lines elided ...]\n[... 5 lines elided ...]\n";
        Assert.Same(input, OutputCondenser.Condense(input, DedupOnly));
    }

    [Theory]
    [InlineData("x [xa]")] // not digits
    [InlineData("x [x]")] // no digits
    [InlineData("x[x3]")] // no space before the bracket
    [InlineData("x [x3] y")] // marker not line-final
    public void D_NearMissMarkerShapes_StayEligible(string line)
    {
        var input = $"{line}\n{line}\n{line}\n";
        var expected = $"{line} [x3]\n";
        Assert.Equal(expected, OutputCondenser.Condense(input, DedupOnly));
    }

    // ---------------------------------------------------------------------
    // T — head/tail truncation
    // ---------------------------------------------------------------------

    private static string Lines(int count, int width, bool terminated = true)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < count; i++)
        {
            // Distinct lines (the index suffix) so dedup can never interfere with T tests.
            sb.Append(new string('-', width)).Append(' ').Append(i);
            if (terminated || i < count - 1)
                sb.Append('\n');
        }
        return sb.ToString();
    }

    [Theory]
    [InlineData(200)] // exactly head+tail: middle is 0 lines
    [InlineData(209)] // middle is 9 lines: below MinElidedLines
    public void T_BelowLineThreshold_NoOp(int lineCount)
    {
        var input = Lines(lineCount, width: 500);
        Assert.Same(input, OutputCondenser.Condense(input, TruncateOnly));
    }

    [Fact]
    public void T_MiddleTooSmallInChars_NoOp()
    {
        // 300 short lines: middle is 100 lines but far under MinElidedChars.
        var input = Lines(300, width: 4);
        Assert.Same(input, OutputCondenser.Condense(input, TruncateOnly));
    }

    [Fact]
    public void T_ElidesMiddle_ExactMarkerAndBoundaries()
    {
        var input = Lines(210, width: 500);
        var result = OutputCondenser.Condense(input, TruncateOnly);

        var inputLines = input.Split('\n');
        var expected = string.Join('\n', inputLines.Take(150))
            + "\n[... 10 lines elided ...]\n"
            + string.Join('\n', inputLines.Skip(160));
        Assert.Equal(expected, result);
    }

    [Fact]
    public void T_UnterminatedFinalLine_CountsAsALine()
    {
        var terminated = Lines(210, width: 500);
        var unterminated = Lines(210, width: 500, terminated: false);

        // Both are 210 lines; both must fire and keep the same 50-line tail (modulo the final \n).
        var a = OutputCondenser.Condense(terminated, TruncateOnly);
        var b = OutputCondenser.Condense(unterminated, TruncateOnly);
        Assert.Equal(a, b + "\n");
    }

    [Fact]
    public void T_TruncatedOutput_IsAFixedPoint()
    {
        var once = OutputCondenser.Condense(Lines(400, width: 500), TruncateOnly);
        Assert.Same(once, OutputCondenser.Condense(once, TruncateOnly));
    }

    [Fact]
    public void T_RunsAfterD_LosslessCollapseGetsFirstShot()
    {
        // 1000 identical lines would trip T on their own, but D collapses them to one line first —
        // the content-lossy transform must only fire on what dedup could not save.
        var input = string.Concat(Enumerable.Repeat("the same spammy line\n", 1000));
        Assert.Equal("the same spammy line [x1000]\n", OutputCondenser.Condense(input, BothOn));
    }

    // ---------------------------------------------------------------------
    // Fail-open abort — ESC/BEL/CR
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(Esc + "[31mred\u001b[0m\nspam\nspam\nspam\n")]
    [InlineData("bell\a\nspam\nspam\nspam\n")]
    [InlineData("progress\rdone\nspam\nspam\nspam\n")]
    [InlineData("crlf\r\nspam\nspam\nspam\n")]
    public void Abort_ControlCharacters_LeaveWholeStringUntouched(string input)
    {
        var stats = new CondenseStats();
        var result = OutputCondenser.Condense(input, BothOn, ref stats);
        Assert.Same(input, result);
        Assert.True(stats.AbortedControlChars);
        Assert.False(stats.Changed);
        Assert.Equal(0, stats.DedupRunsCollapsed + stats.TruncationsApplied);
    }

    // ---------------------------------------------------------------------
    // Stats
    // ---------------------------------------------------------------------

    [Fact]
    public void Stats_HandComputedCounters()
    {
        // "spam\nspam\nspam\nrest\n" (20 chars) → "spam [x3]\nrest\n" (15 chars):
        // one run collapsed, net 5 chars.
        var stats = new CondenseStats();
        var result = OutputCondenser.Condense("spam\nspam\nspam\nrest\n", DedupOnly, ref stats);

        Assert.Equal("spam [x3]\nrest\n", result);
        Assert.Equal(1, stats.DedupRunsCollapsed);
        Assert.Equal(5, stats.DedupNetChars);
        Assert.Equal(0, stats.TruncationsApplied);
        Assert.Equal(0, stats.TruncationNetChars);
        Assert.True(stats.Changed);
        Assert.False(stats.AbortedControlChars);
    }

    [Fact]
    public void Stats_TruncationCounters()
    {
        var input = Lines(210, width: 500);
        var stats = new CondenseStats();
        var result = OutputCondenser.Condense(input, TruncateOnly, ref stats);

        Assert.Equal(1, stats.TruncationsApplied);
        Assert.Equal(input.Length - result.Length, stats.TruncationNetChars);
        Assert.Equal(0, stats.DedupRunsCollapsed);
        Assert.True(stats.Changed);
    }

    // ---------------------------------------------------------------------
    // API contract
    // ---------------------------------------------------------------------

    [Fact]
    public void Condense_ReturnsSameInstance_WhenUnchangedOrNoOp()
    {
        var input = "one\ntwo\nthree";
        Assert.Same(input, OutputCondenser.Condense(input, BothOn));
        Assert.Same(input, OutputCondenser.Condense(input, default));
        Assert.Same(string.Empty, OutputCondenser.Condense(string.Empty, BothOn));
    }

    [Fact]
    public void TryCondense_DestinationSmallerThanInput_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
        {
            var stats = new CondenseStats();
            OutputCondenser.TryCondense("abc".AsSpan(), BothOn, new char[2], out _, ref stats);
        });
        Assert.Equal("destination", ex.ParamName);
    }

    // ---------------------------------------------------------------------
    // Property tests — all 4 option combinations × adversarial corpus.
    // Hand-rolled, mirroring OutputMinifierTests: idempotency, determinism,
    // never-grows, and the net-chars conservation law. NOT subsequence
    // conservation — markers are inserted by design.
    // ---------------------------------------------------------------------

    // Shared with PipelineGoldenFixtureTests' cross-stage idempotency property.
    internal static IEnumerable<string> AdversarialInputs()
    {
        // Control-character inputs (all must hit the abort path).
        yield return "a\r";
        yield return "\r\n";
        yield return Esc + "[0;1;2m";
        yield return "bell\a";
        yield return "spam\nspam\nspam\r\n";

        // Plain edges.
        yield return "";
        yield return "a";
        yield return "\n";
        yield return "\n\n\n";
        yield return "a\n\n\n\n\nb";
        yield return "héllo\nwörld";
        yield return "\U0001D11E\n\U0001D11E\n\U0001D11E\n\U0001D11E";

        // Runs around every boundary.
        yield return "x\nx";
        yield return "x\nx\nx";
        yield return "a\na\na"; // unprofitable at K=3
        yield return "a\na\na\na"; // barely profitable at K=4
        yield return "long enough line\nlong enough line\nlong enough line";
        yield return string.Concat(Enumerable.Repeat("spam line\n", 500));

        // Marker collisions and adjacency.
        yield return "x [x3]\nx [x3]\nx [x3]";
        yield return "abc [x3]\nabc [x3]\nabc\nabc\nabc";
        yield return "[... 12 lines elided ...]\n[... 12 lines elided ...]\n[... 12 lines elided ...]";
        yield return "x [x]\nx [x]\nx [x]"; // near-miss shape: eligible
        yield return "spam\nspam\nspam\nspam [x4]\nspam\nspam\nspam";

        // Truncation-sized payloads (with and without a terminating \n, with embedded runs).
        var wide = new StringBuilder();
        for (var i = 0; i < 300; i++)
            wide.Append(new string('=', 60)).Append(' ').Append(i).Append('\n');
        yield return wide.ToString();
        yield return wide.ToString().TrimEnd('\n');

        var mixed = new StringBuilder();
        for (var i = 0; i < 180; i++)
            mixed.Append("head ").Append(new string('#', 55)).Append(' ').Append(i).Append('\n');
        mixed.Append(string.Concat(Enumerable.Repeat("repeated middle spam line\n", 200)));
        for (var i = 0; i < 60; i++)
            mixed.Append("tail ").Append(new string('#', 55)).Append(' ').Append(i).Append('\n');
        yield return mixed.ToString();
    }

    public static TheoryData<bool, bool> AllOptionCombos()
    {
        var data = new TheoryData<bool, bool>();
        for (var bits = 0; bits < 4; bits++)
            data.Add((bits & 1) != 0, (bits & 2) != 0);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllOptionCombos))]
    public void Properties_IdempotentDeterministicNeverGrows(bool dedupe, bool truncate)
    {
        var options = new CondenseOptions(dedupe, truncate);
        foreach (var input in AdversarialInputs())
        {
            var stats = new CondenseStats();
            var once = OutputCondenser.Condense(input, options, ref stats);

            // (1) idempotency: Condense(Condense(x)) == Condense(x)
            var twice = OutputCondenser.Condense(once, options);
            Assert.True(once == twice,
                $"Not idempotent for dedupe={dedupe} truncate={truncate} input=\"{Show(input)}\": " +
                $"once=\"{Show(once)}\" twice=\"{Show(twice)}\"");

            // (2) determinism: a second independent run is byte-identical
            var again = OutputCondenser.Condense(input, options);
            Assert.True(once == again,
                $"Not deterministic for dedupe={dedupe} truncate={truncate} input=\"{Show(input)}\"");

            // (3) never grows
            Assert.True(once.Length <= input.Length,
                $"Output grew for dedupe={dedupe} truncate={truncate} input=\"{Show(input)}\": " +
                $"{input.Length} → {once.Length}");

            // (4) conservation: the net counters account exactly for the shrinkage (zero when
            //     unchanged or aborted).
            Assert.True(
                stats.DedupNetChars + stats.TruncationNetChars == input.Length - once.Length,
                $"Net counters drifted for dedupe={dedupe} truncate={truncate} " +
                $"input=\"{Show(input)}\": dedup={stats.DedupNetChars} " +
                $"trunc={stats.TruncationNetChars} shrinkage={input.Length - once.Length}");
        }
    }

    /// <summary>Renders control characters visibly for assertion failure messages.</summary>
    private static string Show(string s)
    {
        if (s.Length > 120)
            s = s[..120] + "…";
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
