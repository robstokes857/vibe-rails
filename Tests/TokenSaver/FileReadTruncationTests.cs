using System.Text;
using TokenSaver.Minify;
using TokenSaver.Pipeline;
using TokenSaver.Shape;
using Xunit;

namespace Tests.TokenSaver;

/// <summary>
/// Pins the file-read truncation fix (2026-08-29). See
/// <c>runbooks/token_saver/truncation_file_reads.md</c> for the capture evidence that motivated it.
///
/// The bug: <c>scope-read</c> ships off because rewriting file contents breaks the model's
/// <c>old_string</c> matching — but <c>scope-shell</c> is on, and <c>cat</c>/<c>sed</c>/
/// <c>Get-Content</c> through a shell tool IS a file read. <c>truncate-long</c> counts lines and has
/// no idea what produced them, so it cut 250+ line holes in the middle of source files. On 2026-08-28
/// that path was 71% of every char the saver removed from Claude's context.
///
/// The fix widens T's keep budget for file-read commands rather than declining outright, so the
/// catastrophic-payload guard survives. The tests below are grouped by the thing that could regress:
/// the permissive detector, the budget arithmetic, the preserved invariants, and the pipeline wiring.
/// </summary>
public class FileReadTruncationTests
{
    private static readonly CondenseOptions TruncateOnly = new(
        DedupeConsecutiveLines: false, TruncateLongOutput: true);

    private static readonly CondenseOptions TruncatePreserving = new(
        DedupeConsecutiveLines: false, TruncateLongOutput: true, PreserveVerbatimFileContents: true);

    /// <summary>Source-shaped lines: long enough that 450 of them clear MinElidedChars (4096).</summary>
    private static string SourceDump(int lines)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < lines; i++)
            builder.Append($"{i,6}\tpublic void Method{i:D4}(int argument) {{ return; }}").Append('\n');
        return builder.ToString();
    }

    // ---------------------------------------------------------------------
    // The detector. Every "true" case below is a VERBATIM command from the
    // captures that exposed the bug — do not tidy them into synthetic shapes.
    // ---------------------------------------------------------------------

    [Theory]
    // POSIX shell, compound — the dominant real-world form.
    [InlineData("cat -n /c/src/backend/services/ingestion.py; echo \"=== b ===\"; cat -n /c/src/b.py")]
    [InlineData("cat /c/src/frontend/src/styles.css && echo \"=== APP.JSX ===\" && cat -n /c/src/App.jsx")]
    [InlineData("for f in backend/db/models/*.py; do echo \"=== $f ===\"; cat -n \"$f\"; done")]
    [InlineData("sed -n '1,112p' /c/src/scoring.py; echo \"===== lexicons.py =====\"; cat -n /c/src/lex.py")]
    [InlineData("ls /c/src/domain/matching; echo ====; cat /c/src/domain/matching/asset.py")]
    [InlineData("wc -l /c/src/event_types/*.py; echo \"=====\"; cat -n /c/src/canonicalize.py")]
    // PowerShell.
    [InlineData("$lines = Get-Content VibeRails/wwwroot/js/modules/rule-controller.js")]
    [InlineData("Get-Content backend\\domain\\matching.py; Get-Content backend\\tests\\test_x.py")]
    [InlineData("gc AGENTS.md")]
    // Codex code-mode: a real command buried in JavaScript.
    [InlineData("const r = await tools.exec_command({\"cmd\":\"Get-Content -Raw backend\\\\matching.py\"})")]
    [InlineData("const cmds = [[\"matching\", \"Get-Content backend\\\\domain\\\\matching.py\"]]")]
    // Plain single commands.
    [InlineData("cat README.md")]
    [InlineData("head -200 build/generated.ts")]
    [InlineData("tail -n 5000 src/main.rs")]
    // ripgrep-as-cat: `^` matches every line, so this is `cat -n` with extra steps. The single
    // largest remaining category before it was covered (989 KB across the sample window).
    [InlineData("rg -n '^' frontend/src/App.jsx")]
    // Verbatim from a capture: the shell command lives inside a JS string literal, so the pattern
    // arrives backslash-escaped rather than bare.
    [InlineData("tools.exec_command({cmd:\"rg -n \\\"^\\\" frontend/src/styles.css\"})")]
    [InlineData("$files=@('a.jsx','b.jsx'); foreach($f in $files){ Write-Output \"F $f\"; rg -n '^' $f }")]
    [InlineData("rg -n '.*' backend/main.py")]
    public void ReadsFileContents_True(string command) =>
        Assert.True(CommandShapes.ReadsFileContents(command));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("dotnet build VibeRails.slnx")]
    [InlineData("npm run build")]
    [InlineData("dotnet test --filter Session_8458cd22")]
    [InlineData("git status --short")]
    // A real search is still a search: grep only counts as a file dump with a match-everything
    // pattern. These payloads can span a whole tree and grep-group already reshapes them.
    [InlineData("rg -n 'TODO' src/")]
    [InlineData("rg -n -C 15 'loadAgents|loadCodeQuality' VibeRails/wwwroot/js/")]
    [InlineData("grep -rn \"error\" logs/")]
    // Bare-word equality, not substring: a path or identifier that merely CONTAINS a command name
    // must not match, or every build log under /var/catalog/ would stop condensing.
    [InlineData("ls /var/catalog/entries")]
    [InlineData("dotnet run --project ./concatenate/Concatenate.csproj")]
    [InlineData("node scripts/typecheck.js")]
    public void ReadsFileContents_False(string? command) =>
        Assert.False(CommandShapes.ReadsFileContents(command));

    /// <summary>
    /// The reason the detector cannot just reuse <see cref="CommandShapes.Classify"/>. Classify's
    /// whole-command metacharacter rule returns None for every real offender — correct for a
    /// classifier that authorises a REWRITE, useless for a signal that only suppresses one. If this
    /// test ever goes red because Classify learned these forms, re-read both doc comments before
    /// "simplifying" one into the other: they have opposite safety polarities on purpose.
    /// </summary>
    [Theory]
    [InlineData("cat -n a.py; echo ===; cat -n b.py")]
    [InlineData("for f in *.py; do cat -n \"$f\"; done")]
    [InlineData("$lines = Get-Content a.py")]
    public void Classify_DeclinesTheCommandsReadsFileContentsCatches(string command)
    {
        Assert.Equal(CommandShape.None, CommandShapes.Classify(command));
        Assert.True(CommandShapes.ReadsFileContents(command));
    }

    // ---------------------------------------------------------------------
    // The budget
    // ---------------------------------------------------------------------

    /// <summary>The regression itself: a 454-line multi-file dump, the median observed offender.</summary>
    [Fact]
    public void RealWorldSourceDump_TruncatesByDefault_SurvivesWhenPreserving()
    {
        var input = SourceDump(454);

        var truncated = OutputCondenser.Condense(input, TruncateOnly);
        Assert.NotEqual(input, truncated);
        Assert.Contains("lines elided ...]", truncated);
        Assert.Equal(201, truncated.TrimEnd('\n').Split('\n').Length); // 150 + marker + 50

        Assert.Equal(input, OutputCondenser.Condense(input, TruncatePreserving));
    }

    /// <summary>
    /// The widest payload the captures showed (967 elided lines ⇒ 1167 total) must survive, and a
    /// genuinely oversized one must not — that pair is what pins 1200/200 as a real number rather
    /// than a vibe.
    ///
    /// The cap does not bite at exactly 1401 lines: <see cref="OutputCondenser.MinElidedChars"/>
    /// (4096) still gates every elision, so at these ~56-char lines the middle needs ~74 lines
    /// before T fires at all. 1470 is below that joint threshold, 1480 is above it.
    /// </summary>
    [Theory]
    [InlineData(1167, false)]
    [InlineData(1400, false)]
    [InlineData(1470, false)]
    [InlineData(1480, true)]
    [InlineData(4000, true)]
    public void PreservingBudget_CapsOnlyBeyondTheWidenedBudget(int lines, bool expectTruncated)
    {
        var input = SourceDump(lines);
        var output = OutputCondenser.Condense(input, TruncatePreserving);
        Assert.Equal(expectTruncated, output != input);
        if (expectTruncated)
            Assert.Contains("lines elided ...]", output);
    }

    /// <summary>
    /// Preserving is a budget, not an off switch. A pathological dump still gets capped, because T's
    /// real job is stopping one tool result from eating the context window T exists to protect.
    /// </summary>
    [Fact]
    public void CatastrophicDump_StillTruncatedWhilePreserving()
    {
        var output = OutputCondenser.Condense(SourceDump(50_000), TruncatePreserving);
        Assert.Contains("lines elided ...]", output);
        Assert.Equal(1401, output.TrimEnd('\n').Split('\n').Length); // 1200 + marker + 200
    }

    // ---------------------------------------------------------------------
    // The invariants, restated at the wide budget. These are the ones that
    // would thrash prompt caching if the budget change broke them.
    // ---------------------------------------------------------------------

    [Fact]
    public void PreservingBudget_IsIdempotent()
    {
        var once = OutputCondenser.Condense(SourceDump(4000), TruncatePreserving);
        Assert.Equal(once, OutputCondenser.Condense(once, TruncatePreserving));
    }

    [Fact]
    public void PreservingBudget_IsDeterministic()
    {
        var input = SourceDump(4000);
        Assert.Equal(
            OutputCondenser.Condense(input, TruncatePreserving),
            OutputCondenser.Condense(input, TruncatePreserving));
    }

    [Fact]
    public void PreservingBudget_NeverGrows()
    {
        foreach (var lines in new[] { 10, 201, 454, 1400, 1401, 4000 })
        {
            var input = SourceDump(lines);
            Assert.True(OutputCondenser.Condense(input, TruncatePreserving).Length <= input.Length);
        }
    }

    /// <summary>
    /// Preserving can only ever keep MORE. Pinned across the interesting sizes so a future budget
    /// edit cannot accidentally make the file-read path the aggressive one.
    /// </summary>
    [Fact]
    public void PreservingNeverRemovesMoreThanTheDefaultBudget()
    {
        foreach (var lines in new[] { 201, 454, 1167, 1401, 4000, 50_000 })
        {
            var input = SourceDump(lines);
            Assert.True(
                OutputCondenser.Condense(input, TruncatePreserving).Length
                    >= OutputCondenser.Condense(input, TruncateOnly).Length,
                $"preserving removed more than the default budget at {lines} lines");
        }
    }

    /// <summary>The flag is a budget selector, not a transform: with T off it must do nothing.</summary>
    [Fact]
    public void PreserveFlag_IsInertWhenTruncationIsDisabled()
    {
        var input = SourceDump(4000);
        var options = new CondenseOptions(
            DedupeConsecutiveLines: false, TruncateLongOutput: false,
            PreserveVerbatimFileContents: true);
        Assert.True(options.IsNoOp);
        Assert.Equal(input, OutputCondenser.Condense(input, options));
    }

    /// <summary>
    /// <c>default(CondenseOptions)</c> bypasses the primary constructor, so the new parameter's
    /// default must be the SAFE value — false, i.e. the original 150/50 budget.
    /// </summary>
    [Fact]
    public void DefaultOptions_DoNotPreserve()
    {
        Assert.False(default(CondenseOptions).PreserveVerbatimFileContents);
        Assert.False(new CondenseOptions(true, true).PreserveVerbatimFileContents);
    }

    // ---------------------------------------------------------------------
    // Pipeline wiring — the fix is worthless if Run doesn't thread the flag.
    // ---------------------------------------------------------------------

    /// <summary>
    /// Asserted on line survival rather than string equality: the same plan also runs the lossless
    /// minifier, and <c>blank-edges</c> legitimately drops the payload's trailing newline. That is
    /// not the transform under test, and pinning it here would make this test fail for the wrong
    /// reason the next time a cleanup stage changes.
    /// </summary>
    [Fact]
    public void Pipeline_KeepsEveryFileReadLine_ButTruncatesTheSameBytesOtherwise()
    {
        var input = SourceDump(454);
        var plan = CompressionCatalog.Resolve(null);

        var asFileRead = RunPipeline(input, plan, readsFileContents: true);
        var asSpew = RunPipeline(input, plan, readsFileContents: false);

        Assert.DoesNotContain("lines elided ...]", asFileRead);
        Assert.Equal(454, asFileRead.TrimEnd('\n').Split('\n').Length);
        Assert.Contains("Method0000(", asFileRead);
        Assert.Contains("Method0227(", asFileRead); // the middle: exactly what the bug removed
        Assert.Contains("Method0453(", asFileRead);

        Assert.Contains("lines elided ...]", asSpew);
        Assert.DoesNotContain("Method0227(", asSpew);
    }

    /// <summary>
    /// The trace contract: every catalog stage appears exactly once on every call, including when a
    /// widened budget made truncation decline. A file read must read as NoChange, never as absent.
    /// </summary>
    [Fact]
    public void Pipeline_StillTracesTruncateLong_WhenTheWiderBudgetDeclines()
    {
        var trace = new List<StageTrace>();
        RunPipeline(SourceDump(454), CompressionCatalog.Resolve(null), true, trace);

        var truncate = Assert.Single(trace, t => t.StageId == CompressionCatalog.TruncateLong);
        Assert.Equal(StageOutcome.NoChange, truncate.Outcome);
    }

    private static string RunPipeline(
        string input, CompressionPlan plan, bool readsFileContents,
        ICollection<StageTrace>? trace = null)
    {
        using var scratch = new PipelineScratch(Math.Max(input.Length, 64));
        var minifyStats = new MinifyStats();
        var condenseStats = new CondenseStats();
        return CompressionPipeline
            .Run(input, plan, CommandShape.None, readsFileContents, scratch, out _,
                ref minifyStats, ref condenseStats, trace)
            .ToString();
    }
}
