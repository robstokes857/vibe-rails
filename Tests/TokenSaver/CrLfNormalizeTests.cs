using System.Text;
using TokenSaver.Minify;
using TokenSaver.Pipeline;
using TokenSaver.Shape;
using Xunit;

namespace Tests.TokenSaver;

/// <summary>
/// Pins the bug that made the token saver look dead on Windows: CRLF→LF normalization used to be
/// bundled into <c>cr-collapse</c>, which ships OFF, so the minifier restored every <c>\r</c> it
/// split on. Every stage from the shape phase down fails open on a surviving CR, so on output from
/// rg, PowerShell and dotnet — the three tools that emit CRLF — six of the eleven stages no-opped
/// and only whitespace trimming ever fired. Measured over 1,940 real captures: LF payloads saved
/// 28-42%, CRLF payloads 0.2-2.6%.
///
/// The fix is a standalone <c>crlf-normalize</c> stage. The redraw guard is deliberately untouched:
/// a payload carrying a genuine bare CR must still be returned verbatim.
/// </summary>
public sealed class CrLfNormalizeTests
{
    [Fact]
    public void CuratedDefaults_EnableCrLfNormalize_ButNotRedrawCollapse()
    {
        var plan = CompressionCatalog.Resolve(null);

        Assert.True(plan.Flags.NormalizeCrLf);
        Assert.False(plan.Flags.CollapseCrRedraws);
    }

    [Fact]
    public void Minify_PureCrLfPayload_NormalizesLineEndings()
    {
        var flags = new MinifyFlags(
            CollapseCrRedraws: false,
            StripAnsiStyling: false,
            StripTrailingWhitespace: false,
            TrimBlankLineEdges: false,
            CollapseBlankLineRuns: false,
            NormalizeCrLf: true);

        var stats = new MinifyStats();
        var result = OutputMinifier.Minify("one\r\ntwo\r\nthree\r\n", flags, ref stats);

        Assert.Equal("one\ntwo\nthree\n", result);
        Assert.Equal(3, stats.CrLfChars);
        // The counter stays distinct from redraw collapse: "stripped Windows line endings" and
        // "collapsed a progress bar" must never be the same number in the log line.
        Assert.Equal(0, stats.CrRedrawChars);
    }

    [Fact]
    public void Minify_PayloadWithBareCr_IsReturnedVerbatim()
    {
        var flags = new MinifyFlags(
            CollapseCrRedraws: false,
            StripAnsiStyling: false,
            StripTrailingWhitespace: true,
            TrimBlankLineEdges: false,
            CollapseBlankLineRuns: false,
            NormalizeCrLf: true);

        // A CRLF log with one redraw frame in it. Normalizing the pair while leaving the bare CR
        // would hand the later stages a string whose characters are not what the user saw.
        const string input = "building\r\nprogress 99%\rdone   \r\nfinished\r\n";
        var stats = new MinifyStats();
        var result = OutputMinifier.Minify(input, flags, ref stats);

        Assert.Equal(input, result);
        Assert.True(stats.AbortedMalformedEscape);
        Assert.Equal(0, stats.CrLfChars);
    }

    [Fact]
    public void Minify_IsIdempotent_OverCrLfInput()
    {
        var flags = CompressionCatalog.Resolve(null).Flags;
        const string input = "alpha  \r\n\r\n\r\n\r\nbeta\r\n";

        var once = OutputMinifier.Minify(input, flags);
        var twice = OutputMinifier.Minify(once, flags);

        Assert.Equal(once, twice);
        Assert.DoesNotContain('\r', once);
    }

    /// <summary>
    /// The end-to-end shape of the bug. A CRLF test run under the curated defaults must now reach
    /// the shape and condense phases; before the split it came out 3 chars shorter than it went in.
    /// </summary>
    [Fact]
    public void Pipeline_CrLfTestRun_ReachesTheShapePhase()
    {
        var plan = CompressionCatalog.Resolve(null);
        var input = BuildTestRunLog("\r\n");

        var trace = new List<StageTrace>();
        var output = RunPipeline(input, plan, CommandShape.TestRun, trace);

        Assert.Contains("[... 400 passed ...]", output);
        Assert.Equal(
            StageOutcome.Applied,
            trace.Single(s => s.StageId == CompressionCatalog.CrLfNormalize).Outcome);
        Assert.Equal(
            StageOutcome.Applied,
            trace.Single(s => s.StageId == CompressionCatalog.ElidePassedTests).Outcome);
        // truncate-long legitimately has nothing left to do: elide runs first by design and has
        // already taken 402 lines down to 3, far under its 201-line threshold.
        Assert.Equal(
            StageOutcome.NoChange,
            trace.Single(s => s.StageId == CompressionCatalog.TruncateLong).Outcome);

        // The same payload with LF endings was always compressed; line endings no longer decide
        // whether the saver works.
        var lfOutput = RunPipeline(BuildTestRunLog("\n"), plan, CommandShape.TestRun, []);
        Assert.Equal(lfOutput, output);
    }

    /// <summary>
    /// The condense phase has its own CR fail-open, so reaching the shape phase does not prove it
    /// was reached too. Unshaped output (no recognised command) isolates it: truncate-long is then
    /// the only stage that can fire.
    /// </summary>
    [Fact]
    public void Pipeline_CrLfUnshapedOutput_ReachesTheCondensePhase()
    {
        var plan = CompressionCatalog.Resolve(null);
        var builder = new StringBuilder();
        for (var i = 0; i < 400; i++)
            builder.Append($"C:/source/vibe-rails/File{i:D3}.cs:{i}: match").Append("\r\n");

        var trace = new List<StageTrace>();
        var output = RunPipeline(builder.ToString(), plan, CommandShape.None, trace);

        Assert.Contains("[... 200 lines elided ...]", output);
        Assert.Equal(
            StageOutcome.Applied,
            trace.Single(s => s.StageId == CompressionCatalog.TruncateLong).Outcome);
    }

    [Fact]
    public void Pipeline_CrLfTestRunWithARedrawFrame_StillFailsOpen()
    {
        var plan = CompressionCatalog.Resolve(null);
        var input = "spinner |\rspinner /\r\n" + BuildTestRunLog("\r\n");

        var trace = new List<StageTrace>();
        var output = RunPipeline(input, plan, CommandShape.TestRun, trace);

        Assert.Equal(input, output);
        Assert.Equal(
            StageOutcome.Aborted,
            trace.Single(s => s.StageId == CompressionCatalog.CrLfNormalize).Outcome);
    }

    private static string BuildTestRunLog(string newline)
    {
        var builder = new StringBuilder();
        builder.Append("Determining projects to restore...").Append(newline);
        for (var i = 0; i < 400; i++)
            builder.Append($"  Passed VibeRails.Tests.Case{i:D3} [3 ms]").Append(newline);
        builder.Append("Failed! - Failed: 1, Passed: 400").Append(newline);
        return builder.ToString();
    }

    private static string RunPipeline(
        string input, CompressionPlan plan, CommandShape shape, ICollection<StageTrace> trace,
        bool readsFileContents = false)
    {
        using var scratch = new PipelineScratch(Math.Max(input.Length, 64));
        var minifyStats = new MinifyStats();
        var condenseStats = new CondenseStats();
        return CompressionPipeline
            .Run(input, plan, shape, readsFileContents, scratch, out _, ref minifyStats,
                ref condenseStats, trace)
            .ToString();
    }
}
