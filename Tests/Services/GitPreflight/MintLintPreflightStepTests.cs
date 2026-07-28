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
        Assert.Contains(messages, message => message.Contains("src/demo.cs", StringComparison.Ordinal));
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
    public async Task ExecuteAsync_ScoresOnlyAddedContent_ForModifiedFiles()
    {
        var messy = """
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
            """;
        var snapshot = new GitStagedSnapshot(
            Directory.GetCurrentDirectory(),
            [
                new GitStagedFileSnapshot(
                    "src/demo.cs", "src/demo.cs", GitStagedChangeKind.Modified, true, false, 1,
                    Content: messy,
                    PreviousRelativePath: null,
                    PreviousContent: "public class Demo { public void Run() { } }",
                    AddedContent: "return 0;",
                    AddedLineNumbers: [13])
            ],
            []);

        var result = await new MintLintPreflightStep().ExecuteAsync(
            new GitPreflightStepContext("run", Request(), snapshot, (_, _, _) => ValueTask.CompletedTask),
            TestContext.Current.CancellationToken);

        var report = MintLintReportFactory.FromJson(result.Details![MintLintReportFactory.DetailsKey]);
        Assert.NotNull(report);
        var file = Assert.Single(report.Files);
        var wholeFileScore = MintLintAnalyzer.ScanSources(
            [new SourceInput("src/demo.cs", messy)]).Overall.Score;
        Assert.True(file.Score < wholeFileScore);
        Assert.Null(file.BaselineScore);
        Assert.Null(file.IntroducedScore);
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
