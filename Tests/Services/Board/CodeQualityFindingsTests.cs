using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services.Board;
using VibeRails.Services.GitPreflight;
using Xunit;

namespace Tests.Services.Board;

/// <summary>
/// VIBE-362: a saved Code quality check's comment and report name the files that need work and their
/// worst problems, ranked as the Quality page's findings.js ranks them, instead of bare counts and an id.
/// The fixture is the check the owner quoted: a vendored, minified xterm.js add-on rated At risk. It is
/// a report from before VIBE-274, with the worst value of each metric and no grade.
/// VIBE-274: every file carries a letter grade, every declaration past a limit is listed with how to fix
/// it, and a failed minimum always names the files that hold the grade down (<see cref="Graded"/>).
/// </summary>
public sealed class CodeQualityFindingsTests
{
    private const string CheckId = "748d63dfc2ec441eb1d6ac690bd8355b";

    private static MintLintMetricResponse Metric(string name, double value, double warn, double critical,
        string? source = null, int? line = null, bool higherIsBetter = false, double score = 100) =>
        new(name, value, score, warn, critical, higherIsBetter, source, line, Direction: higherIsBetter ? "HB" : "LB");

    private static MintLintCategoryResponse Category(string name, double weight, params MintLintMetricResponse[] metrics) =>
        new(name, 0, weight, 0, [.. metrics]);

    private static MintLintFileReportResponse Minified() => new("VibeRails/wwwroot/assets/xterm/addon-canvas.js", 80, "AtRisk",
    [
        Category("Complexity", 1, Metric("cyclomatic_complexity", 70, 10, 20, "_drawToCache", 1), Metric("cognitive_complexity", 115, 15, 30, "anonymous", 1),
            Metric("npath_complexity", 1_000_000_000, 1000, 100_000, "_drawToCache", 1), Metric("nesting_depth", 4, 4, 6, "_drawToCache", 1, score: 50)),
        Category("Size", 0.7, Metric("lines_of_code", 1, 300, 600, score: 0.2), Metric("method_count", 34, 15, 30, "i", 1),
            Metric("field_count", 3, 10, 20, "anonymous", 1, score: 15), Metric("parameter_count", 11, 4, 7, "constructor", 1)),
        Category("Cohesion", 1, Metric("lack_of_cohesion", 1, 2, 4, "anonymous", 1, score: 25)),
        Category("Coupling", 0.8, Metric("fan_out", 0, 15, 30, score: 0), Metric("hard_coded_dependencies", 148, 3, 8)),
        Category("Testability", 1, Metric("hard_coded_dependencies", 148, 3, 8), Metric("ambient_dependencies", 38, 3, 10)),
        Category("Duplication", 0.6, Metric("duplication", 0, 0.1, 0.3, score: 0)),
        Category("Maintainability", 0.8, Metric("maintainability_index", 0, 50, 25, higherIsBetter: true), Metric("halstead_difficulty", 561.97, 20, 40, "anonymous", 1))
    ], ReferencedByCount: 1858, Priority: 341.5);

    private static MintLintFileReportResponse Clean() => new("VibeRails/wwwroot/js/modules/terminal-multitab.js", 8.8, "Clean",
    [
        Category("Complexity", 1, Metric("cyclomatic_complexity", 2, 10, 20, "anonymous: forEach", 375, score: 10)),
        Category("Maintainability", 0.8, Metric("maintainability_index", 61.4, 50, 25, higherIsBetter: true, score: 0)),
        Category("Testability", 1, Metric("ambient_dependencies", 1, 3, 10, score: 16.7))
    ], ReferencedByCount: 1004, Priority: 35.2);

    /// <summary>A report as it is saved now: an F for one function at its critical limit, with every
    /// declaration past a limit listed and the value the function had before the change.</summary>
    private static MintLintFileReportResponse Graded(string file = "src/Orders.cs", double lines = 212) => new(file, 60, "AtRisk",
    [
        Category("Complexity", 1, Metric("cyclomatic_complexity", 25, 10, 20, "ProcessBatch", 380)),
        Category("Size", 0.7, Metric("lines_of_code", lines, 300, 600, score: 35.3))
    ], Grade: "F", Findings:
    [
        new("cyclomatic_complexity", "critical", 25, 10, 20, false, "function", "ProcessBatch", 380, 455, Previous: 22),
        new("cyclomatic_complexity", "warning", 12, 10, 20, false, "function", "Validate", 40, 80),
        new("nesting_depth", "warning", 5, 4, 6, false, "function")
    ]);

    /// <summary>A file inside every limit: grade B, nothing to list, but its nearest measurement is known.</summary>
    private static MintLintFileReportResponse Near() => new("src/Parser.cs", 13.5, "Clean",
    [
        Category("Complexity", 1, Metric("cyclomatic_complexity", 9, 10, 20, "Parse", 3, score: 45)),
        Category("Size", 0.7, Metric("lines_of_code", 40, 300, 600, score: 6.7))
    ], Grade: "B", Findings: []);

    private static string ResultJson(params MintLintFileReportResponse[] files)
    {
        var report = new MintLintReportResponse(44.4, "Okay", files.Length, 2, [.. files]);
        var result = new GitPreflightStepResult(MintLintPreflightStep.Id, "MintLint code health", GitPreflightStepStatus.Warning, "found concerns", [], 0, false,
            new Dictionary<string, string> { [MintLintReportFactory.DetailsKey] = MintLintReportFactory.ToJson(report) });
        return JsonSerializer.Serialize(result, AppJsonSerializerContext.Default.GitPreflightStepResult);
    }

    private static BoardCheckRecord Check(string status = "Findings", int analyzed = 2, int findings = 1, string? resultJson = null,
        IReadOnlyList<BoardCheckSkippedFile>? notReviewed = null) =>
        new(CheckId, "card_1", "run_1", "action_1", "Code quality", status, "card", "a", "b", null, null, "1.0.0.0",
            DateTime.UtcNow, DateTime.UtcNow, $"{analyzed} files analyzed; {findings} files with code-quality findings; 4 skipped. Advisory analysis.",
            analyzed + (notReviewed?.Count ?? 0), analyzed, notReviewed?.Count ?? 0, findings, [], resultJson ?? ResultJson(Minified(), Clean()),
            NotReviewed: notReviewed);

    private static readonly BoardCheckSkippedFile[] QuotedSkips =
    [
        new("Tests/wwwroot/js/xterm-canvas-dispose.test.mjs", "Hidden by local Code quality ignore settings."),
        new("UITests/AGENTS.md", "Not a supported source language."),
        new("UITests/tests/terminal-board-card.spec.js", "Hidden by local Code quality ignore settings."),
        new("VibeRails/wwwroot/AGENTS.md", "Not a supported source language.")
    ];

    [Fact]
    public void ProblemsAreRankedAsTheQualityPageRanksThem()
    {
        var problems = CodeQualityFindings.Problems(Minified());
        // Critical before warning, overshoot capped at twice the warning-to-critical span, then category
        // weight, then label; the composite maintainability index last. Within-limit metrics are not problems.
        Assert.Equal(["Ambient dependencies", "Cognitive complexity", "Cyclomatic complexity", "Hard-coded dependencies",
            "NPath execution paths", "Halstead difficulty", "Parameters per function", "Methods per type", "Maintainability index"],
            problems.Select(problem => problem.Label));
        Assert.All(problems, problem => Assert.Equal(CodeQualityFindings.Critical, problem.Level));
        var npath = problems.Single(problem => problem.Metric == "npath_complexity");
        Assert.Equal(("1B (cap)", "critical at 100,000", "_drawToCache", 1), (npath.Value, npath.Limit, npath.Source, npath.Line));
        Assert.Equal("562", problems.Single(problem => problem.Metric == "halstead_difficulty").Value);
        Assert.Null(problems.Single(problem => problem.Metric == "ambient_dependencies").Source);
        Assert.Empty(CodeQualityFindings.Problems(Clean()));
        var warning = CodeQualityFindings.Problems(new("a.cs", 30, "Okay", [Category("Complexity", 1, Metric("nesting_depth", 5, 4, 6, null, 3))])).Single();
        Assert.Equal((CodeQualityFindings.Warning, "warning above 4", "top-level code"), (warning.Level, warning.Limit, warning.Source));
    }

    [Fact]
    public void TheCommentNamesTheFileThatNeedsWorkItsWorstProblemsAndWhatWasNotChecked()
    {
        var receipt = BoardCheckService.CheckReceipt(Check(notReviewed: QuotedSkips));

        Assert.Equal($"""
            **Findings** · feedback only · [Open the full report](check:{CheckId})

            **1 file grades below C** (2 checked)
            `VibeRails/wwwroot/assets/xterm/addon-canvas.js` · **F** · health 20/100
            - Ambient dependencies **38** in the whole file (critical at 10)
            - Cognitive complexity **115** in `anonymous`, line 1 (critical at 30)
            - Cyclomatic complexity **70** in `_drawToCache`, line 1 (critical at 20)
            - and 6 more problems

            Not checked: 4 files (2 hidden by your ignore list, 2 not a supported language). Scope: the card's linked commits.
            """.ReplaceLineEndings("\n"), receipt);
        Assert.DoesNotContain("review_check_", receipt);
    }

    [Fact]
    public void ACleanCheckSaysSoInOneLine()
    {
        var receipt = BoardCheckService.CheckReceipt(Check("Passed", analyzed: 1, findings: 0, resultJson: ResultJson(Clean())));
        Assert.Equal($"""
            **Passed** · feedback only · [Open the full report](check:{CheckId})

            No file grades below C (1 checked).

            Scope: the card's linked commits.
            """.ReplaceLineEndings("\n"), receipt);
    }

    [Fact]
    public void ManyFilesStayShortAndPointAtTheReport()
    {
        var files = Enumerable.Range(1, 5).Select(i => Minified() with { File = $"src/f{i}.js", Priority = 10 - i }).ToArray();
        var receipt = BoardCheckService.CheckReceipt(Check(analyzed: 5, findings: 5, resultJson: ResultJson(files)));
        Assert.Contains("**5 files grade below C** (5 checked)", receipt);
        Assert.Contains("`src/f3.js`", receipt);
        Assert.DoesNotContain("src/f4.js", receipt);
        Assert.Contains("2 more files grade below C; the full report lists them with how to fix each.", receipt);
    }

    [Fact]
    public void TheReportListsEveryProblemFileAndTheFilesThatWereFine()
    {
        var report = BoardCheckService.CheckReport(Check(notReviewed: QuotedSkips)).Findings!;
        Assert.Contains("Files below C (1 of 2 checked), in the order to fix them:", report);
        Assert.Contains("F VibeRails/wwwroot/assets/xterm/addon-canvas.js (health 20/100)", report);
        Assert.Contains("  - NPath execution paths 1B (cap) in _drawToCache, line 1 (critical at 100,000). Fix: Break the function into steps", report);
        Assert.Contains("  - Maintainability index 0 in the whole file (critical at 25). Fix: It rises when the code gets shorter", report);
        Assert.Contains("Other files checked:\n- VibeRails/wwwroot/js/modules/terminal-multitab.js · A · health 91.2/100 · no measurement past a limit",
            report.ReplaceLineEndings("\n"));
        Assert.Contains("Not checked (4 files in scope):", report);
        Assert.Contains($"read_board_check checkId={CheckId}", report);
        Assert.DoesNotContain("concern score", report);
    }

    [Fact]
    public void EveryDeclarationPastALimitIsListedWithWhereItIsAndHowToFixIt()
    {
        var lines = CodeQualityFixList.FixLines(CodeQualityFindings.FromReport(new(60, "AtRisk", 1, 0, [Graded()])), null, maxFiles: 5, maxProblems: 3);

        // The grade first, then each function by name and line. A fix is spelled out the first time its
        // metric appears; "was 22" says the change made an existing function worse.
        Assert.Equal(
        [
            "F src/Orders.cs (health 40/100)",
            "  - Cyclomatic complexity 25 in ProcessBatch, line 380 (critical at 20; was 22 before this change). Fix: Split it into smaller functions that each do one job. "
                + "Return early instead of nesting else blocks, and replace long if/else or switch chains with a lookup table.",
            "  - Cyclomatic complexity 12 in Validate, line 40 (warning above 10).",
            "  - Nesting depth 5 in top-level code (warning above 4). Fix: Invert conditions and return early, and move the innermost block into its own function."
        ], lines);
    }

    [Fact]
    public void FilesAreListedInTheOrderThatRaisesTheGradeFastest()
    {
        // The overall grade is weighted by lines: a 200-line D is 10 below C over 200 lines, a 10-line F
        // 30 below over 10. Fixing the large D moves the grade more, so it lists first.
        var files = CodeQualityFindings.FromReport(new(40, "NeedsWork", 3, 0,
        [
            Graded("src/small.cs", lines: 10),
            Graded("src/large.cs", lines: 200) with { Score = 40, Rating = "NeedsWork", Grade = "D" },
            Near()
        ]));

        Assert.Equal(["src/large.cs", "src/small.cs"], CodeQualityFixList.ToFix(files, null).Select(file => file.Path));
        var lines = CodeQualityFixList.FixLines(files, null, maxFiles: 1, maxProblems: 1);
        Assert.Equal("D src/large.cs (health 60/100)", lines[0]);
        Assert.Equal("  - and 2 more in this file", lines[2]);
        Assert.Equal("and 1 more file below C", lines[^1]);
    }

    [Fact]
    public void AMinimumAboveCAlsoNamesFilesWithNothingPastALimit()
    {
        // A minimum of A can fail on a file graded B, which by construction has no problem to list.
        // It still gets a line: the measurement nearest its limit.
        var files = CodeQualityFindings.FromReport(new(13.5, "Clean", 1, 0, [Near()]));

        Assert.Empty(CodeQualityFixList.FixLines(files, 'B', 5, 3));
        Assert.Equal(
        [
            "B src/Parser.cs (health 86.5/100)",
            "  - No measurement is past a limit. Closest: Cyclomatic complexity 9 in Parse, line 3 (warning above 10)."
        ], CodeQualityFixList.FixLines(files, 'A', 5, 3));
    }

    [Fact]
    public void ACheckThatFailsItsMinimumNamesWhatToFixInTheCommentAndTheReport()
    {
        var failed = Check(findings: 1, resultJson: ResultJson(Graded(), Near())) with
        {
            MinimumGrade = "B", MinimumGradeResult = BoardCheckRecord.GradeNotMet,
            GradeSummary = "Overall Code quality grade F (health 47.4/100) is below minimum B (80/100)."
        };

        var receipt = BoardCheckService.CheckReceipt(failed);
        Assert.StartsWith($"**Findings** · this step failed · [Open the full report](check:{CheckId})\n\nOverall Code quality grade F (health 47.4/100) is below minimum B (80/100).\n\n"
            + "**1 file grades below B** (2 checked)\n`src/Orders.cs` · **F** · health 40/100\n- Cyclomatic complexity **25** in `ProcessBatch`, line 380 (critical at 20; was 22 before this change)\n", receipt);

        var report = BoardCheckService.CheckReport(failed).Findings!.ReplaceLineEndings("\n");
        Assert.Contains("Minimum grade B: not met, so this step failed.", report);
        Assert.Contains("Files below B (1 of 2 checked), in the order to fix them:\nF src/Orders.cs (health 40/100)\n  - Cyclomatic complexity 25 in ProcessBatch, line 380", report);
        Assert.Contains("Other files checked:\n- src/Parser.cs · B · health 86.5/100 · no measurement past a limit", report);
    }

    [Fact]
    public void NamesFromAnalysedCodeArePrintedOnOneLine()
    {
        var file = Graded() with { Findings = [new("cyclomatic_complexity", "critical", 25, 10, 20, false, "function", "Bad`Name\n  two\u202E", 7, 9)] };
        var problem = Assert.Single(CodeQualityFindings.Problems(file));
        Assert.Equal("Cyclomatic complexity **25** in `Bad'Name two`, line 7 (critical at 20)", CodeQualityFixList.Describe(problem, markdown: true));
    }

    [Fact]
    public void LabelsMatchTheQualityPage()
    {
        var labels = GuideEntries(@"^\s{4}(\w+): \{ label: '([^']+)'", RegexOptions.Multiline);
        Assert.NotEmpty(labels);
        Assert.Equal(labels.OrderBy(pair => pair.Key), CodeQualityFindings.Labels.OrderBy(pair => pair.Key));
    }

    [Fact]
    public void FixesMatchTheQualityPage()
    {
        // The fixes an agent reads in a failed check are the Quality page's, word for word.
        var fixes = GuideEntries(@"^\s{4}(\w+): \{ label: .*?^\s{8}fix: '([^']+)'", RegexOptions.Multiline | RegexOptions.Singleline);
        Assert.Equal(CodeQualityFindings.Labels.Count, fixes.Count);
        Assert.Equal(fixes.OrderBy(pair => pair.Key), CodeQualityFindings.Fixes.OrderBy(pair => pair.Key));
    }

    /// <summary>One value per metric from the GUIDE table of the Quality page's findings.js.</summary>
    private static Dictionary<string, string> GuideEntries(string pattern, RegexOptions options)
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(SourceFile())!);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "VibeRails.slnx"))) directory = directory.Parent;
        Assert.NotNull(directory);
        var findings = File.ReadAllText(Path.Combine(directory.FullName, "VibeRails", "wwwroot", "js", "modules", "code-report", "findings.js"));
        return Regex.Matches(findings, pattern, options).ToDictionary(match => match.Groups[1].Value, match => match.Groups[2].Value);
    }

    private static string SourceFile([CallerFilePath] string path = "") => path;
}
