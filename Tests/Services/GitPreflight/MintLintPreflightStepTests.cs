using MintLint;
using VibeRails.Services.GitPreflight;
using VibeRails.Services.VCA.Hooks;
using Xunit;

namespace Tests.Services.GitPreflight;

public sealed class MintLintPreflightStepTests
{
    [Fact]
    public async Task ExecuteAsync_ScansSupportedIndexContent_AndReportsSkippedFiles()
    {
        var snapshot = new GitStagedSnapshot(
            Directory.GetCurrentDirectory(),
            [
                File("src/demo.cs", "public class Demo { public void Run() { if (true) { } } }"),
                File("README.md", "not supported"),
                new GitStagedFileSnapshot(
                    "old.cs", "old.cs", GitStagedChangeKind.Deleted, false, false, 1, null)
            ],
            []);
        var messages = new List<string>();

        var result = await new MintLintPreflightStep().ExecuteAsync(
            new GitPreflightStepContext(
                "run",
                Request(),
                snapshot,
                (message, _, _) =>
                {
                    messages.Add(message);
                    return ValueTask.CompletedTask;
                }),
            TestContext.Current.CancellationToken);

        Assert.NotEqual(GitPreflightStepStatus.Skipped, result.Status);
        Assert.Equal("1", result.Details!["supportedFileCount"]);
        Assert.Equal("2", result.Details["skippedFileCount"]);
        // A clean scan says its grade and that nothing needs fixing; it lists no file.
        Assert.Equal("A", result.Details["overallGrade"]);
        Assert.Contains(messages, message => message.StartsWith("Overall grade A · health ", StringComparison.Ordinal));
        Assert.Contains("No file grades below C.", messages);
        Assert.Equal(3, messages.Count);
        // Readers of a result without a report still get the older one-line-per-file form.
        Assert.StartsWith("src/demo.cs: ", result.Details["worstFiles"]);
    }

    [Fact]
    public async Task ExecuteAsync_EmitsFullMetricReport_InDetails()
    {
        var snapshot = new GitStagedSnapshot(
            Directory.GetCurrentDirectory(),
            [File("src/demo.cs", "public class Demo { public void Run() { if (true) { } } }")],
            []);

        var result = await new MintLintPreflightStep().ExecuteAsync(
            new GitPreflightStepContext("run", Request(), snapshot, (_, _, _) => ValueTask.CompletedTask),
            TestContext.Current.CancellationToken);

        var report = MintLintReportFactory.FromJson(result.Details![MintLintReportFactory.DetailsKey]);
        Assert.NotNull(report);
        Assert.Equal(1, report.AnalyzedFileCount);
        var file = Assert.Single(report.Files);
        Assert.Equal("src/demo.cs", file.File);
        Assert.NotEmpty(file.Categories);

        // The worst offender per metric names the code that caused it and carries a snippet.
        Assert.NotNull(report.WorstMetrics);
        var worstCyclomatic = Assert.Single(report.WorstMetrics, metric => metric.Name == "cyclomatic_complexity");
        Assert.Equal("src/demo.cs", worstCyclomatic.File);
        Assert.Equal("Run", worstCyclomatic.Source);
        Assert.Equal(1, worstCyclomatic.Line);
        Assert.Contains("Run", worstCyclomatic.Snippet);
        Assert.All(file.Categories, category =>
        {
            Assert.NotEmpty(category.Metrics);
            Assert.True(category.Weight > 0);
            // The category is its worst metric; the roll-up input is score × weight.
            Assert.Equal(category.Metrics.Max(metric => metric.Score), category.Score, 1);
            Assert.Equal(Math.Round(category.Score * category.Weight, 1), category.WeightedScore, 1);
        });
        // Every raw metric the analyzer measures shows up in at least one category.
        var reportedMetrics = file.Categories
            .SelectMany(category => category.Metrics)
            .Select(metric => metric.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Superset(
            new HashSet<string>(StringComparer.Ordinal)
            {
                "cyclomatic_complexity", "cognitive_complexity", "npath_complexity", "nesting_depth",
                "lines_of_code", "method_count", "field_count", "parameter_count",
                "lack_of_cohesion", "fan_out", "hard_coded_dependencies", "ambient_dependencies",
                "duplication", "maintainability_index", "halstead_difficulty"
            },
            reportedMetrics);
    }

    [Fact]
    public async Task ExecuteAsync_WithoutReport_PrintsTheSameLinesAndVerdict()
    {
        var snapshot = new GitStagedSnapshot(
            Directory.GetCurrentDirectory(),
            [
                File("src/demo.cs", "public class Demo { public void Run() { if (true) { } } }"),
                File("src/other.cs", "public class Other { Demo demo; }"),
                File("README.md", "not supported")
            ],
            []);

        async Task<(GitPreflightStepResult Result, List<string> Messages)> RunAsync(bool includeReport)
        {
            var messages = new List<string>();
            var result = await new MintLintPreflightStep().ExecuteAsync(
                new GitPreflightStepContext(
                    "run",
                    Request() with { IncludeReport = includeReport },
                    snapshot,
                    (message, _, _) =>
                    {
                        messages.Add(message);
                        return ValueTask.CompletedTask;
                    }),
                TestContext.Current.CancellationToken);
            return (result, messages);
        }

        var full = await RunAsync(includeReport: true);
        var hook = await RunAsync(includeReport: false);

        Assert.Equal(full.Messages, hook.Messages);
        Assert.Equal(full.Result.Output, hook.Result.Output);
        Assert.Equal(full.Result.Status, hook.Result.Status);
        Assert.Equal(full.Result.Summary, hook.Result.Summary);
        Assert.Equal(full.Result.Blocking, hook.Result.Blocking);
        Assert.True(full.Result.Details!.ContainsKey(MintLintReportFactory.DetailsKey));
        Assert.False(hook.Result.Details!.ContainsKey(MintLintReportFactory.DetailsKey));
        Assert.Equal(
            full.Result.Details.Where(pair => pair.Key != MintLintReportFactory.DetailsKey),
            hook.Result.Details);
    }

    // Run has fifteen decision points: the first `if` holds one `if`, three `&&` and four `||`, then six
    // more `if` statements, plus one. That is past the warning limit of 10.
    private static readonly string MessyRun = """
        public class Demo
        {
            public int Run(int a, int b)
            {
                if (a > 0 && b > 0 || a == 1 && b == 2 || a == 3 && b == 4 || a == 5 || a == 6) { return 1; }
                if (a == 7) { return 2; }
                if (a == 8) { return 3; }
                if (a == 9) { return 4; }
                if (a == 10) { return 5; }
                if (a == 11) { return 6; }
                if (a == 12) { return 7; }
                return 0;
            }
        }
        """.ReplaceLineEndings("\n");

    private static GitStagedSnapshot ReturnAdded(string previousContent) => new(
        Directory.GetCurrentDirectory(),
        [
            new GitStagedFileSnapshot(
                "src/demo.cs", "src/demo.cs", GitStagedChangeKind.Modified, true, false, 1,
                Content: MessyRun,
                PreviousRelativePath: null,
                PreviousContent: previousContent,
                AddedContent: "        return 0;",
                AddedLineNumbers: [12])
        ],
        []);

    [Fact]
    public async Task ExecuteAsync_DoesNotGradeAFunctionTheChangeOnlyTouched()
    {
        // Run was just as complex before: the change added its last statement and no branch.
        var snapshot = ReturnAdded(MessyRun.Replace("        return 0;\n", ""));

        var result = await new MintLintPreflightStep().ExecuteAsync(
            new GitPreflightStepContext("run", Request(), snapshot, (_, _, _) => ValueTask.CompletedTask),
            TestContext.Current.CancellationToken);

        var report = MintLintReportFactory.FromJson(result.Details![MintLintReportFactory.DetailsKey]);
        Assert.NotNull(report);
        var file = Assert.Single(report.Files);
        var wholeFileScore = MintLintAnalyzer.ScanSources(
            [new SourceInput("src/demo.cs", MessyRun)]).Overall.Score;
        Assert.True(file.Score < wholeFileScore);
        Assert.Equal("A", file.Grade);
        Assert.Empty(file.Findings!);
        Assert.Null(file.BaselineScore);
        Assert.Null(file.IntroducedScore);
        Assert.Equal(GitPreflightStepStatus.Passed, result.Status);
    }

    [Fact]
    public async Task ExecuteAsync_GradesAFunctionTheChangeMadeWorse_AtItsRealLine()
    {
        // Run existed and was trivial. The staged lines are one statement of what it became, so the
        // added line alone says nothing; the function it belongs to is what the change produced.
        var snapshot = ReturnAdded("public class Demo { public int Run(int a, int b) { return 0; } }");
        var messages = new List<string>();

        var result = await new MintLintPreflightStep().ExecuteAsync(
            new GitPreflightStepContext("run", Request(), snapshot, (message, _, _) =>
            {
                messages.Add(message);
                return ValueTask.CompletedTask;
            }),
            TestContext.Current.CancellationToken);

        var report = MintLintReportFactory.FromJson(result.Details![MintLintReportFactory.DetailsKey]);
        Assert.NotNull(report);
        var file = Assert.Single(report.Files);
        var cyclomatic = Assert.Single(file.Findings!, finding => finding.Name == "cyclomatic_complexity");
        Assert.Equal(("warning", 15.0, "function", "Run", (int?)3, (int?)13, (double?)1.0),
            (cyclomatic.Level, cyclomatic.Value, cyclomatic.Scope, cyclomatic.Source, cyclomatic.Line, cyclomatic.EndLine, cyclomatic.Previous));
        // The category's worst value points at the same real line, with the code that starts there.
        var worst = Assert.Single(report.WorstMetrics!, metric => metric.Name == "cyclomatic_complexity");
        Assert.Equal((15.0, "Run", (int?)3), (worst.Value, worst.Source, worst.Line));
        Assert.StartsWith("    public int Run(int a, int b)", worst.Snippet);

        // 15 of 10/20 → Complexity 75 → 15 + 25 / 50 × 45 = 37.5 at least: a D or worse, so it is named.
        Assert.True(file.Score >= 37.5, $"Expected at least 37.5 but got {file.Score}.");
        Assert.Equal(GitPreflightStepStatus.Warning, result.Status);
        Assert.Contains("1 file(s) grade below C. Fix these, worst first:", messages);
        Assert.Contains(messages, message => message.StartsWith($"{file.Grade} src/demo.cs (health ", StringComparison.Ordinal));
        Assert.Contains(messages, message => message.Contains("Cyclomatic complexity 15 in Run, line 3 (warning above 10; was 1 before this change). Fix: Split it into smaller functions", StringComparison.Ordinal));
        Assert.Contains("need work. The commit remains allowed.", result.Summary);
    }

    [Fact]
    public async Task ExecuteAsync_RemovalOnlyChange_ProducesNoMintLintScore()
    {
        var snapshot = new GitStagedSnapshot(
            Directory.GetCurrentDirectory(),
            [
                new GitStagedFileSnapshot(
                    "src/demo.cs",
                    "src/demo.cs",
                    GitStagedChangeKind.Modified,
                    ExistsInIndex: true,
                    IsBinary: false,
                    ChangedLineCount: 3,
                    Content: "public class Demo { }",
                    PreviousContent: "public class Demo { public void Removed() { } }",
                    AddedContent: string.Empty,
                    AddedLineNumbers: [])
            ],
            []);

        var result = await new MintLintPreflightStep().ExecuteAsync(
            new GitPreflightStepContext(
                "run",
                Request(),
                snapshot,
                (_, _, _) => ValueTask.CompletedTask),
            TestContext.Current.CancellationToken);

        Assert.Equal(GitPreflightStepStatus.Skipped, result.Status);
        Assert.Equal("0", result.Details!["supportedFileCount"]);
        Assert.Equal("1", result.Details["skippedFileCount"]);
        Assert.Equal("1", result.Details["noAddedCodeFileCount"]);
        Assert.Contains("no added lines", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.False(result.Details.ContainsKey(MintLintReportFactory.DetailsKey));
    }

    [Fact]
    public async Task ExecuteAsync_MapsFragmentLineBackToTheCompleteFile()
    {
        const string addedLine =
            "public class Demo { public void Run() { if (true) { } } }";
        var completeFile = string.Join(
            '\n',
            Enumerable.Repeat(string.Empty, 41).Append(addedLine));
        var snapshot = new GitStagedSnapshot(
            Directory.GetCurrentDirectory(),
            [
                new GitStagedFileSnapshot(
                    "src/demo.cs",
                    "src/demo.cs",
                    GitStagedChangeKind.Modified,
                    ExistsInIndex: true,
                    IsBinary: false,
                    ChangedLineCount: 1,
                    Content: completeFile,
                    PreviousContent: string.Join('\n', Enumerable.Repeat(string.Empty, 41)),
                    AddedContent: addedLine,
                    AddedLineNumbers: [42])
            ],
            []);

        var result = await new MintLintPreflightStep().ExecuteAsync(
            new GitPreflightStepContext(
                "run",
                Request(),
                snapshot,
                (_, _, _) => ValueTask.CompletedTask),
            TestContext.Current.CancellationToken);

        var report = MintLintReportFactory.FromJson(
            result.Details![MintLintReportFactory.DetailsKey]);
        Assert.NotNull(report);
        var cyclomatic = Assert.Single(
            report.WorstMetrics!,
            metric => metric.Name == "cyclomatic_complexity");
        Assert.Equal(42, cyclomatic.Line);
        Assert.Contains("public class Demo", cyclomatic.Snippet);
    }

    [Fact]
    public async Task ExecuteAsync_CommitMessage_DoesNotRescanSources()
    {
        var request = Request() with
        {
            Invocation = Request().Invocation with { Kind = VcaHookKind.CommitMessage }
        };
        var result = await new MintLintPreflightStep().ExecuteAsync(
            new GitPreflightStepContext(
                "run",
                request,
                new GitStagedSnapshot(Directory.GetCurrentDirectory(), [File("demo.cs", "class Demo { }")], []),
                (_, _, _) => ValueTask.CompletedTask),
            TestContext.Current.CancellationToken);

        Assert.Equal(GitPreflightStepStatus.Skipped, result.Status);
        Assert.Contains("already ran", result.Summary);
    }

    [Fact]
    public async Task ExecuteAsync_WorkingTreeRequest_DescribesChangedSourceFiles()
    {
        var snapshot = new GitStagedSnapshot(
            Directory.GetCurrentDirectory(),
            [File("src/demo.cs", "public class Demo { }")],
            []);

        var result = await new MintLintPreflightStep().ExecuteAsync(
            new GitPreflightStepContext(
                "run",
                Request() with { WorkingTreeChanges = true },
                snapshot,
                (_, _, _) => ValueTask.CompletedTask),
            TestContext.Current.CancellationToken);

        Assert.StartsWith("Scanned added code in 1 supported changed source file(s)", result.Output[0]);
    }

    [Fact]
    public async Task ExecuteAsync_UnpushedImpactUsesCapturedHeadSources_NotWorkingTree()
    {
        var repository = Path.Combine(Path.GetTempPath(), $"mintlint_unpushed_{Guid.NewGuid():N}");
        Directory.CreateDirectory(repository);
        try
        {
            await System.IO.File.WriteAllTextAsync(
                Path.Combine(repository, "caller.cs"),
                "public class WorkingTreeCaller { }",
                TestContext.Current.CancellationToken);
            var snapshot = new GitStagedSnapshot(
                repository,
                [File("alpha.cs", "public class AlphaService { }")],
                [],
                TrackedFiles: ["alpha.cs", "caller.cs"],
                ImpactFiles:
                [
                    new GitIndexTextFile("alpha.cs", "public class AlphaService { }"),
                    new GitIndexTextFile("caller.cs", "public class HeadCaller { AlphaService service; }")
                ]);

            var result = await new MintLintPreflightStep().ExecuteAsync(
                new GitPreflightStepContext(
                    "run",
                    Request() with { UnpushedChanges = true },
                    snapshot,
                    (_, _, _) => ValueTask.CompletedTask),
                TestContext.Current.CancellationToken);

            var report = MintLintReportFactory.FromJson(result.Details![MintLintReportFactory.DetailsKey]);
            Assert.NotNull(report);
            Assert.Equal(1, Assert.Single(report.Files).ReferencedByCount);
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    private static GitStagedFileSnapshot File(string path, string content) => new(
        path,
        path,
        GitStagedChangeKind.Modified,
        true,
        false,
        1,
        content);

    private static GitPreflightRequest Request() => new(
        Directory.GetCurrentDirectory(),
        new VcaHookInvocation(
            VcaHookKind.PreCommit,
            null,
            Directory.GetCurrentDirectory(),
            false,
            TimeSpan.Zero,
            false));
}
