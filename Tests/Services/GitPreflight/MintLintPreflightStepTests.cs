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
    public async Task ExecuteAsync_ScoresTheCommittedBaseline_ForModifiedFiles()
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
                    PreviousContent: "public class Demo { public void Run() { } }")
            ],
            []);

        var result = await new MintLintPreflightStep().ExecuteAsync(
            new GitPreflightStepContext("run", Request(), snapshot, (_, _, _) => ValueTask.CompletedTask),
            TestContext.Current.CancellationToken);

        var report = MintLintReportFactory.FromJson(result.Details![MintLintReportFactory.DetailsKey]);
        Assert.NotNull(report);
        var file = Assert.Single(report.Files);
        Assert.NotNull(file.BaselineScore);
        Assert.NotNull(file.IntroducedScore);
        // The staged version is worse than the committed one, so the change owns the delta.
        Assert.True(file.IntroducedScore > 0);
        Assert.Equal(Math.Round(file.Score - file.BaselineScore.Value, 1), file.IntroducedScore);
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
